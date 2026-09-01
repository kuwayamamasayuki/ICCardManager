using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
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
    internal class NewLedgerFromSegmentsBuilder
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly ILogger _logger;

        /// <param name="ledgerRepository">台帳リポジトリ</param>
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
            SummaryGenerator summaryGenerator,
            ILogger logger)
        {
            _ledgerRepository = ledgerRepository;
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
        /// <returns>挿入成功した detail 件数（segment 単位で失敗した場合は 0）</returns>
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

                var segmentFailed = false;

                foreach (var segment in segments)
                {
                    var segmentDetails = segment.Details;

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

                    var newLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = date,
                        Summary = summary,
                        Income = income,
                        Expense = expense,
                        Balance = balance
                    };

                    var newLedgerId = await _ledgerRepository.InsertAsync(newLedger).ConfigureAwait(false);
                    // Issue #1913: SplitAtChargeBoundaries は時系列昇順（古い→新しい）で返す。
                    // 挿入順がそのまま rowid の並びになるため、昇順のまま渡すと
                    // LedgerDetail.SequenceNumber の規約（FeliCa 互換で小さい rowid ＝ 新しい）が
                    // 反転する。新しい順にしてから渡す（LendingService の同型の挿入と同じ）。
                    // 摘要・金額（上の Generate / CalculateGroupFinancials）は昇順のまま使う。
                    var success = await _ledgerRepository.InsertDetailsAsync(
                        newLedgerId, segmentDetails.AsEnumerable().Reverse()).ConfigureAwait(false);

                    if (!success)
                    {
                        segmentFailed = true;
                        _logger?.LogError(
                            "Issue #906: 新規台帳の利用明細を登録できませんでした（影響行数 0）: "
                            + "CardIdm={CardIdm}, LedgerId={LedgerId}, 行番号={LineNumber}, 明細件数={DetailCount}",
                            IdmMasker.Mask(cardIdm), newLedgerId, firstLineNumber, segmentDetails.Count);
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstLineNumber,
                            Message = $"カード {IdmMasker.Mask(cardIdm)} の利用明細をデータベースへ登録できませんでした。"
                                + "他のパソコンや別の操作で対象の台帳が変更された可能性があります。"
                                + "画面を更新してから、この行をもう一度取り込んでください。",
                            // Data は突き合わせ用の内部キーであり、画面にもログにも出ない
                            // （表示されるのは Message だけ。DataExportImportViewModel を参照）。
                            // マスクすると呼び出し元がカードを一意に特定できなくなるため生のまま保持する
                            // （Issue #1986 で消費側を数え上げて確認した）。
                            Data = cardIdm
                        });
                    }
                }

                return segmentFailed ? 0 : detailRows.Count;
            }
            catch (Exception ex)
            {
                // 技術的詳細（ex.Message・スタックトレース）はログへ逃がし、
                // ユーザー向けには 3 要素の文言だけを出す（#1614）。
                _logger?.LogError(
                    ex,
                    "Issue #906: 利用履歴の自動作成に失敗しました: "
                    + "CardIdm={CardIdm}, 行番号={LineNumber}, 明細件数={DetailCount}",
                    IdmMasker.Mask(cardIdm), firstLineNumber, detailRows.Count);
                errors.Add(new CsvImportError
                {
                    LineNumber = firstLineNumber,
                    Message = $"カード {IdmMasker.Mask(cardIdm)} の"
                        + ExceptionMessageFormatter.ToUserMessage(ex, "利用履歴の自動作成"),
                    // Data の扱いは上の分岐のコメントを参照（Issue #1986）。
                    Data = cardIdm
                });
                return 0;
            }
        }
    }
}
