#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services.Import.Builders
{
    /// <summary>
    /// 利用履歴 ID 空欄の詳細行から、segment 分割を伴って新規 Ledger を自動生成する。
    /// Detail CSV インポートの一機能として使用（Issue #906, #918, #1053）。
    /// Issue #1284 で CsvImportService.Detail.cs から抽出。
    /// </summary>
    /// <remarks>
    /// Issue #2176: 1 グループ（カード IDm＋日付）ぶんの「台帳の行の INSERT＋明細の INSERT」を、
    /// チャージ境界で分けたすべてのセグメントを含めて <b>1 つのトランザクション</b>で確定させる。
    /// 旧実装はどちらも autocommit で、明細の INSERT が失敗すると<b>明細を持たない台帳の行だけ</b>が
    /// 6 年保存の台帳に残り、月次帳票に「中身の無い利用」として印字された。利用履歴 ID が空欄の CSV は
    /// 再インポートで重複判定されない（#1781）ため、残った行を消し忘れると二重計上にもなった。
    /// 単位をセグメントではなくグループにしたのは、1 日の一部のセグメントだけが入った状態を作らないため
    /// （直した行を取り込み直すと、入っていたセグメントが二重になる）。失敗したグループだけを
    /// 行番号付きのエラーとして報告し、ほかのグループは取り込む報告形は保つ（#2155 と同じ）。
    /// </remarks>
    internal class NewLedgerFromSegmentsBuilder
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly DbContext _dbContext;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly ILogger? _logger;

        /// <param name="ledgerRepository">台帳リポジトリ</param>
        /// <param name="dbContext">
        /// グループ単位のトランザクションを開くための DB コンテキスト（Issue #2176）。
        /// tx は <c>scope.Transaction</c> をリポジトリへ明示的に渡す（<c>db-write-conventions.md</c> の「①」）。
        /// </param>
        /// <param name="summaryGenerator">
        /// 摘要生成器。Issue #1955: 以前は <c>new SummaryGenerator()</c> を自前で生成しており、
        /// 部署種別が既定（市長事務部局）に固定されていたため、企業会計部局の組織でも
        /// チャージ行が「役務費によりチャージ」で台帳に書き込まれていた。
        /// 呼び出し元（<c>CsvImportService.CreateSummaryGeneratorAsync</c>）が DB の設定から組み立てる
        /// （DI シングルトンを注入しない理由はそちらの remarks を参照。Issue #1975 で更新）。
        /// <b>省略可能にしない</b> — 省略時の既定値は本来の値と一致しないため、配線漏れが
        /// 「設定した部署種別が静かに無視される」形で潜在化する
        /// （<c>.claude/rules/development-conventions.md</c> #1820）。
        /// </param>
        /// <param name="logger">
        /// ロガー（<c>null</c> 可）。Issue #1986: 失敗の文言から生の <c>ex.Message</c> を外したため、
        /// 技術的詳細の出口はここだけになる。<b>省略可能にしない</b> — 既定値を付けると
        /// 配線漏れが「障害の痕跡がどこにも残らない」形で潜在化する
        /// （<c>.claude/rules/error-messages.md</c> #1817「UI 文言とログを対で数える」／
        /// <c>development-conventions.md</c> #1820）。
        /// </param>
        public NewLedgerFromSegmentsBuilder(
            ILedgerRepository ledgerRepository,
            DbContext dbContext,
            SummaryGenerator summaryGenerator,
            ILogger? logger)
        {
            _ledgerRepository = ledgerRepository ?? throw new ArgumentNullException(nameof(ledgerRepository));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _summaryGenerator = summaryGenerator ?? throw new ArgumentNullException(nameof(summaryGenerator));
            _logger = logger;
        }

        /// <summary>
        /// 1 カード・1 日分の詳細リストから、チャージ境界で segment 分割し、
        /// 各 segment ごとに Ledger を作成して detail を挿入する。
        /// </summary>
        /// <param name="cardIdm">カード IDm</param>
        /// <param name="groupDate">グループキーの日付（DateTime.MinValue なら detail.UseDate から推定）</param>
        /// <param name="detailRows">(line_number, LedgerDetail) のリスト</param>
        /// <param name="errors">エラー追加先</param>
        /// <returns>挿入成功した detail 件数（失敗したらグループ全体を巻き戻して 0）</returns>
        public async Task<int> BuildAndInsertAsync(
            string cardIdm,
            DateTime groupDate,
            List<(int LineNumber, LedgerDetail Detail)> detailRows,
            List<CsvImportError> errors)
        {
            if (detailRows.Count == 0)
            {
                return 0;
            }

            var firstLineNumber = detailRows.First().LineNumber;
            var detailList = detailRows.Select(r => r.Detail).ToList();

            TransactionScope? scope = null;
            try
            {
                // チャージ/ポイント還元の位置で利用グループを分割
                var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(detailList);

                // セグメントがない場合（空リスト対策）は元のリストで 1 segment として扱う
                if (segments.Count == 0)
                {
                    segments = new List<LendingHistoryAnalyzer.DailySegment>
                    {
                        new LendingHistoryAnalyzer.DailySegment
                        {
                            IsCharge = false,
                            IsPointRedemption = false,
                            Details = detailList
                        }
                    };
                }

                scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

                foreach (var segment in segments)
                {
                    var segmentDetails = segment.Details;
                    var newLedger = BuildLedger(cardIdm, groupDate, segmentDetails);

                    var newLedgerId = await _ledgerRepository.InsertAsync(newLedger, scope.Transaction).ConfigureAwait(false);
                    // Issue #1913: SplitAtChargeBoundaries は時系列昇順（古い→新しい）で返す。
                    // 挿入順がそのまま ledger_detail.id の並びになるため、昇順のまま渡すと
                    // LedgerDetail.SequenceNumber の規約（FeliCa 互換で小さい id ＝ 新しい）が
                    // 反転する。新しい順にしてから渡す（LendingService の同型の挿入と同じ）。
                    // 摘要・金額（BuildLedger の Generate / CalculateGroupFinancials）は昇順のまま使う。
                    var success = await _ledgerRepository.InsertDetailsAsync(
                        newLedgerId, segmentDetails.AsEnumerable().Reverse(), scope.Transaction).ConfigureAwait(false);

                    if (!success)
                    {
                        LogFailure(
                            null,
                            "Issue #906: 新規台帳の利用明細を登録できませんでした（影響行数 0）。グループを巻き戻します: "
                            + "CardIdm={CardIdm}, LedgerId={LedgerId}, 行番号={LineNumber}, 明細件数={DetailCount}",
                            IdmMasker.Mask(cardIdm), newLedgerId, firstLineNumber, segmentDetails.Count);
                        var openedScope = scope;
                        SafeRollback.TryRollback(() => openedScope.Rollback(), _logger, "利用履歴の自動作成");
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstLineNumber,
                            // Issue #2176: 台帳の行と明細は同じトランザクションで巻き戻るので、
                            // #1986 の旧文言（「台帳の行だけが残っています。不要な行を削除してから」）の
                            // 状態はもう起きない。この日の行は何も登録されていない。
                            // ただし CSV 全体を取り込み直すと、成功した他の日（利用履歴 ID が空欄）が
                            // 二重に登録される（#1781）ため、取り込み直す範囲を名指しする。
                            // 日付は CSV の行から分かったときだけ名指しする（全行に利用日時が無いグループの
                            // newLedger.Date は「今日」で補っており、CSV の行と対応しない）
                            Message = BuildNotRegisteredMessage(
                                IdmMasker.Mask(cardIdm),
                                HasKnownDate(groupDate, segmentDetails) ? newLedger.Date : (DateTime?)null,
                                reason: null),
                            // Data は突き合わせ用の内部キーであり、画面にもログにも出ない
                            // （表示されるのは Message だけ。DataExportImportViewModel を参照）。
                            // マスクすると呼び出し元がカードを一意に特定できなくなるため生のまま保持する
                            // （Issue #1986 で消費側を数え上げて確認した）。
                            Data = cardIdm
                        });
                        return 0;
                    }
                }

                scope.Commit();
                return detailRows.Count;
            }
            catch (Exception ex)
            {
                // 技術的詳細（ex.Message・スタックトレース）はログへ逃がし、
                // ユーザー向けには 3 要素の文言だけを出す（#1614）。
                // ログは巻き戻しより先に書く（巻き戻しの失敗が本来の失敗要因の痕跡を消さないように。#1745）。
                // トランザクションの開始自体が失敗した（scope == null。共有モードの BEGIN 時の Busy 等）ときは、
                // 巻き戻す対象が無いのでログでも「巻き戻す」と述べない（#2155 と同じ書き分け）
                LogFailure(
                    ex,
                    (scope != null
                        ? "Issue #906: 利用履歴の自動作成に失敗しました。グループを巻き戻します: "
                        : "Issue #906: 利用履歴の自動作成のトランザクションを開始できませんでした: ")
                    + "CardIdm={CardIdm}, 行番号={LineNumber}, 明細件数={DetailCount}",
                    IdmMasker.Mask(cardIdm), firstLineNumber, detailRows.Count);
                if (scope != null)
                {
                    // Issue #2176: 台帳の行と明細は同じ tx なので、ここで巻き戻ればこの日の行は何も残らない。
                    // 巻き戻しの失敗（COMMIT 失敗後の無効な tx 等）は SafeRollback が握る（#1831）
                    var openedScope = scope;
                    SafeRollback.TryRollback(() => openedScope.Rollback(), _logger, "利用履歴の自動作成");
                }

                errors.Add(new CsvImportError
                {
                    LineNumber = firstLineNumber,
                    // Issue #2176（コードレビューで検出）: 巻き戻したのでこの日の行は何も残っていないが、
                    // ToUserMessage の行動指示（SQLITE_BUSY なら「しばらく待ってから再度実行してください」）に
                    // 従って同じ CSV をそのまま取り込み直すと、取り込めていた他の日（利用履歴 ID が空欄。#1781）が
                    // 二重に登録される。0 行の分岐と同じく取り込み直す範囲を名指しし、例外からは「なぜ」だけを
                    // 埋め込む（ToReason。#1991「他の文へ埋め込むときは『なぜ』だけを埋める」）。
                    Message = BuildNotRegisteredMessage(
                        IdmMasker.Mask(cardIdm),
                        ResolveKnownDate(groupDate, detailList),
                        ExceptionMessageFormatter.ToReason(ex)),
                    // Data の扱いは上の分岐のコメントを参照（Issue #1986）。
                    Data = cardIdm
                });
                return 0;
            }
            finally
            {
                // 未コミットの tx は Dispose（接続リースの解放）で必ず巻き戻る
                scope?.Dispose();
            }
        }

        /// <summary>
        /// 1 セグメントぶんの台帳の行（摘要・金額・日付）を組み立てる。
        /// </summary>
        private Ledger BuildLedger(string cardIdm, DateTime groupDate, List<LedgerDetail> segmentDetails)
        {
            var summary = _summaryGenerator.Generate(segmentDetails);
            if (string.IsNullOrEmpty(summary))
            {
                summary = "CSVインポート";
            }

            var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(segmentDetails);

            var date = groupDate;
            if (date == DateTime.MinValue)
            {
                date = segmentDetails
                    .Where(d => d.UseDate.HasValue)
                    .OrderBy(d => d.UseDate!.Value)
                    .Select(d => d.UseDate!.Value)
                    .FirstOrDefault();
                if (date == default)
                {
                    date = DateTime.Now;
                }
            }

            return new Ledger
            {
                CardIdm = cardIdm,
                Date = date,
                Summary = summary,
                Income = income,
                Expense = expense,
                Balance = balance
            };
        }

        /// <summary>
        /// グループの日付が CSV の行から分かるか（<see cref="BuildLedger"/> が「今日」で補っていないか）。
        /// </summary>
        private static bool HasKnownDate(DateTime groupDate, List<LedgerDetail> segmentDetails) =>
            groupDate != DateTime.MinValue || segmentDetails.Any(d => d.UseDate.HasValue);

        /// <summary>
        /// グループの日付を CSV の行から求める（分からなければ <c>null</c>）。例外の経路で使う（台帳の行を組み立てる前に落ち得るため）。
        /// </summary>
        private static DateTime? ResolveKnownDate(DateTime groupDate, List<LedgerDetail> details)
        {
            if (groupDate != DateTime.MinValue)
            {
                return groupDate;
            }

            var dated = details.Where(d => d.UseDate.HasValue).Select(d => d.UseDate!.Value).ToList();
            return dated.Count > 0 ? dated.Min() : (DateTime?)null;
        }

        /// <summary>
        /// グループを巻き戻したときの文言（Issue #2176）。明細の INSERT が 0 行のときと、例外のときの両方で使う。
        /// </summary>
        /// <remarks>
        /// 何が（カードとその日の利用明細）／なぜ（データベースへ登録できなかった。この日の行は何も登録していない）／
        /// どうすれば（そのカードのその日の行だけを残した CSV で取り込み直す）の 3 要素。0 行のときは原因を断定しない
        /// （台帳 ID はこの取込がミリ秒前に採番したもので、他 PC の競合とは限らない。#1986）。例外のときは
        /// <see cref="ExceptionMessageFormatter.ToReason"/> の「なぜ」を添える（行動指示は含めない。#1991）。
        /// 取り込み直す範囲は「カード＋日付」で名指しする — 失敗の単位がそれで、同じ日の別のカードの行まで
        /// 残すと、取り込めていたそのカードの行が二重に登録される（利用履歴 ID が空欄の行は重複を判定しない。#1781）。
        /// 日付は画面表示の書式（<see cref="DisplayFormatters.FormatDate(DateTime)"/>。和暦の環境でも西暦）で出す。
        /// </remarks>
        /// <param name="maskedCardIdm">マスク済みのカード IDm（生の IDm を画面へ出さない。#1852）</param>
        /// <param name="date">CSV の行から分かった利用日（分からないときは <c>null</c>）</param>
        /// <param name="reason">例外の「なぜ」（<see cref="ExceptionMessageFormatter.ToReason"/>）。0 行のときは <c>null</c></param>
        internal static string BuildNotRegisteredMessage(string maskedCardIdm, DateTime? date, string? reason)
        {
            var target = date.HasValue
                ? $" {DisplayFormatters.FormatDate(date.Value)} の"
                : "利用日時の無い";
            var rows = date.HasValue ? "このカードのこの日の行" : "このカードの利用日時の無い行";
            // 0 行のときは原因を断定しない（「なぜ」は「書き込みが反映されなかった」まで）
            var why = string.IsNullOrWhiteSpace(reason)
                ? "データベースへの書き込みが反映されませんでした。"
                : reason!.Trim();
            // 「台帳には何も登録されていません」は、他の日が取り込まれた状況では「取り込み全体が失敗した」と
            // 読まれかねないため、巻き戻した範囲（この日の行）に限って述べる（コードレビューで検出）
            return $"カード {maskedCardIdm} の{target}利用明細を登録できませんでした。"
                + why
                + $"{rows}は台帳に登録されていません。"
                // 取り込めた行が無い（このグループだけの CSV・全グループ失敗）ときに起きない二重登録を断定しない（#1781）
                + "ほかに取り込めた行がある場合、CSV 全体を取り込み直すとそれらの履歴が二重に登録されるため、"
                + $"しばらく待ってから、{rows}だけを残した CSV で取り込み直してください。";
        }

        /// <summary>
        /// 失敗の痕跡をログへ残す。<see cref="_logger"/> が <c>null</c> のときは
        /// <see cref="ErrorDialogHelper.LogException"/>（既存のファイルログ機構）へ委譲する。
        /// </summary>
        /// <remarks>
        /// Issue #1986（コードレビューで検出）: 呼び出し元 <c>CsvImportService</c> の
        /// <c>_logger</c> は <c>ILogger&lt;CsvImportService&gt;?</c> で、7 引数の公開コンストラクタは
        /// <c>logger: null</c> で連鎖する。<c>_logger?.LogError</c> だけだとその構築経路では
        /// <b>本 Issue が文言から外した技術的詳細がどこにも残らない</b>
        /// （<c>error-messages.md</c> #1817「UI 文言とログを対で数える」。
        /// <c>ILogger</c> を持たない層の受け皿が <c>ErrorDialogHelper.LogException</c> であることも
        /// 同節が定めている）。
        /// </remarks>
        [SuppressMessage(
            "Usage",
            "CA2254:Template should be a static expression",
            Justification = "テンプレートを転送するだけのヘルパー。呼び出し元 2 か所はいずれも定数のテンプレートを渡しており、"
                + "ILogger が無いときにプレースホルダを値で埋めるため、テンプレートと引数を分けたまま受け取る必要がある。")]
        private void LogFailure(Exception? ex, string messageTemplate, params object[] args)
        {
            if (_logger != null)
            {
                _logger.LogError(ex, messageTemplate, args);
                return;
            }

            // 構造化テンプレートをそのまま渡せないため、プレースホルダを値で埋めてから記録する。
            var rendered = messageTemplate;
            var placeholders = Regex.Matches(messageTemplate, @"\{[A-Za-z_][A-Za-z0-9_]*\}");
            for (var i = 0; i < placeholders.Count && i < args.Length; i++)
            {
                rendered = rendered.Replace(
                    placeholders[i].Value,
                    Convert.ToString(args[i], CultureInfo.InvariantCulture));
            }

            ErrorDialogHelper.LogException(
                ex ?? new InvalidOperationException(rendered), rendered);
        }
    }
}
