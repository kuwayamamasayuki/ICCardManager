using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 履歴統合の結果
    /// </summary>
    public class LedgerMergeResult
    {
        /// <summary>
        /// 統合（または取り消し）が<b>確定した</b>かどうか。
        /// <see cref="LedgerMergeService.MergeAsync"/> では、コミット後の後処理（Undo 情報の保存）の
        /// 失敗でこの値は落ちない（Issue #1954）。
        /// <see cref="LedgerMergeService.UnmergeAsync"/> はコミット後に DB I/O を持たないため、
        /// この扱いの対象になる後処理がそもそも無い（<see cref="HasPostCommitFailure"/> は常に false）。
        /// </summary>
        public bool Success { get; set; }

        public string ErrorMessage { get; set; } = string.Empty;
        public Ledger? MergedLedger { get; set; }

        /// <summary>
        /// Issue #1954: 統合は確定したが、コミット後の付帯処理（取り消し用の Undo 情報の保存）に
        /// 失敗したことを表す。<see cref="Success"/> は true のままで、呼び出し元は
        /// <b>再実行を促さず</b>「統合は完了・取り消しはできない」と案内する（Issue #1725）。
        /// </summary>
        public bool HasPostCommitFailure { get; set; }
    }

    /// <summary>
    /// 統合の元に戻すデータ（DB永続化用）
    /// </summary>
    /// <remarks>
    /// 統合前の各Ledgerの状態と、各DetailがどのLedgerに属していたかの
    /// マッピングを保持する。これにより統合を完全に逆転できる。
    /// JSON シリアライズ対応のため、プロパティはすべてパブリック。
    /// </remarks>
    public class LedgerMergeUndoData
    {
        /// <summary>
        /// 統合先Ledgerの元の状態
        /// </summary>
        public LedgerSnapshot OriginalTarget { get; set; } = null!;

        /// <summary>
        /// 削除されたソースLedger群
        /// </summary>
        public List<LedgerSnapshot> DeletedSources { get; set; } = new();

        /// <summary>
        /// Detail SequenceNumber → 元のLedger ID のマッピング
        /// </summary>
        /// <remarks>
        /// System.Text.Json 4.7（.NET Framework 4.8）ではDictionary&lt;int,int&gt;を
        /// シリアライズできないため、キーをstring型にしている。
        /// </remarks>
        public Dictionary<string, int> DetailOriginalLedgerMap { get; set; } = new();
    }

    /// <summary>
    /// Ledgerのスナップショット（JSONシリアライズ用）
    /// </summary>
    public class LedgerSnapshot
    {
        public int Id { get; set; }
        public string CardIdm { get; set; } = string.Empty;
        public string? LenderIdm { get; set; }
        public string DateText { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public int Income { get; set; }
        public int Expense { get; set; }
        public int Balance { get; set; }
        public string? StaffName { get; set; }
        public string? Note { get; set; }
        public string? ReturnerIdm { get; set; }
        public string? LentAtText { get; set; }
        public string? ReturnedAtText { get; set; }
        public bool IsLentRecord { get; set; }
        public int CompanionCount { get; set; }

        public static LedgerSnapshot FromLedger(Ledger ledger)
        {
            return new LedgerSnapshot
            {
                Id = ledger.Id,
                CardIdm = ledger.CardIdm,
                LenderIdm = ledger.LenderIdm,
                DateText = ledger.Date.ToString("yyyy-MM-dd HH:mm:ss"),
                Summary = ledger.Summary,
                Income = ledger.Income,
                Expense = ledger.Expense,
                Balance = ledger.Balance,
                StaffName = ledger.StaffName,
                Note = ledger.Note,
                ReturnerIdm = ledger.ReturnerIdm,
                LentAtText = ledger.LentAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                ReturnedAtText = ledger.ReturnedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                IsLentRecord = ledger.IsLentRecord,
                CompanionCount = ledger.CompanionCount
            };
        }

        public Ledger ToLedger()
        {
            return new Ledger
            {
                Id = Id,
                CardIdm = CardIdm,
                LenderIdm = LenderIdm,
                Date = DateTime.Parse(DateText),
                Summary = Summary,
                Income = Income,
                Expense = Expense,
                Balance = Balance,
                StaffName = StaffName,
                Note = Note,
                ReturnerIdm = ReturnerIdm,
                LentAt = string.IsNullOrEmpty(LentAtText) ? null : DateTime.Parse(LentAtText),
                ReturnedAt = string.IsNullOrEmpty(ReturnedAtText) ? null : DateTime.Parse(ReturnedAtText),
                IsLentRecord = IsLentRecord,
                CompanionCount = CompanionCount
            };
        }
    }

    /// <summary>
    /// 統合履歴エントリ（UI表示用）
    /// </summary>
    public class MergeHistoryEntry
    {
        public int Id { get; set; }
        public DateTime MergedAt { get; set; }
        public int TargetLedgerId { get; set; }
        public string Description { get; set; } = string.Empty;
        public LedgerMergeUndoData UndoData { get; set; } = null!;
    }

    /// <summary>
    /// 複数のLedgerレコードを統合するサービス
    /// </summary>
    /// <remarks>
    /// Issue #548対応: 履歴一覧から隣接するエントリを1つに統合する。
    /// 統合先は最も古い（最初の）エントリとし、他のエントリのDetailsを移動後に削除する。
    /// Undoデータはledger_merge_historyテーブルに永続化される。
    /// </remarks>
    public class LedgerMergeService
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly OperationLogger _operationLogger;
        private readonly DbContext _dbContext;
        private readonly ILogger<LedgerMergeService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public LedgerMergeService(
            ILedgerRepository ledgerRepository,
            SummaryGenerator summaryGenerator,
            OperationLogger operationLogger,
            DbContext dbContext,
            ILogger<LedgerMergeService> logger)
        {
            _ledgerRepository = ledgerRepository;
            _summaryGenerator = summaryGenerator;
            _operationLogger = operationLogger;
            _dbContext = dbContext;
            _logger = logger;
        }

        /// <summary>
        /// 複数のLedgerを統合する
        /// </summary>
        /// <param name="ledgerIds">統合するLedger IDのリスト（表示順＝古い順）</param>
        /// <param name="operatorIdm">操作者IDm（GUI操作の場合はnull）</param>
        /// <returns>統合結果</returns>
        public async Task<LedgerMergeResult> MergeAsync(IReadOnlyList<int> ledgerIds, string? operatorIdm = null)
        {
            if (ledgerIds.Count < 2)
            {
                return new LedgerMergeResult
                {
                    Success = false,
                    ErrorMessage = "統合するには2件以上の履歴を選択してください"
                };
            }

            // 全対象Ledgerを取得（Details含む）
            var ledgers = new List<Ledger>();
            foreach (var id in ledgerIds)
            {
                var ledger = await _ledgerRepository.GetByIdAsync(id).ConfigureAwait(false);
                if (ledger == null)
                {
                    // 共有モードの競合調査に必要なため Warning で残す（LogDebug は本番のログファイルに出ない）。
                    _logger.LogWarning(
                        "Merge aborted: ledger {LedgerId} not found. Requested ids: {Ids}",
                        id, string.Join(", ", ledgerIds));
                    return new LedgerMergeResult
                    {
                        Success = false,
                        // Issue #1753: 内部 ID を出さず「なぜ／どうすれば」を伝える（.claude/rules/error-messages.md）。
                        // 共有モードでは他 PC が先に同じ履歴を統合したときに実際に発生する。
                        // 技術的詳細（対象 ID）はログへ逃がす。
                        ErrorMessage = "統合対象の履歴が見つかりません。他のPCまたは他の操作で統合・削除された可能性があります。" +
                                       "画面を最新の状態に更新してから再度お試しください。"
                    };
                }
                ledgers.Add(ledger);
            }

            // バリデーション
            var validationError = Validate(ledgers);
            if (validationError != null)
            {
                return new LedgerMergeResult
                {
                    Success = false,
                    ErrorMessage = validationError
                };
            }

            // 統合先: 最初（最も古い）のエントリ
            var target = ledgers[0];
            var sources = ledgers.Skip(1).ToList();

            // 統合前の状態を保存（ログ用＋Undo用）
            // Issue #1959: リストの浅いコピー（ledgers.ToList()）では beforeLedgers[0] が target と
            // 同一インスタンスになり、以降の in-place 書き換え（Income / Expense / Balance / Summary /
            // Note / CompanionCount と、共有 LedgerDetail の BusStops・SequenceNumber）が
            // そのまま監査ログの BeforeData に載る。統合先だけ「変更前」と「変更後」が同一になり、
            // 6 年保存の operation_log から「何から何へ変わったのか」が失われるため、明細まで複製する。
            var beforeLedgers = ledgers.Select(LedgerCloner.Clone).ToList();
            // 説明テキスト用に元の摘要を保存（targetの変更前に取得）
            var originalSummaryTexts = ledgers.Select(l => l.Summary).ToList();

            // Undo用データを構築
            var undoData = new LedgerMergeUndoData
            {
                OriginalTarget = LedgerSnapshot.FromLedger(target),
                DeletedSources = sources.Select(LedgerSnapshot.FromLedger).ToList(),
                DetailOriginalLedgerMap = new Dictionary<string, int>()
            };

            // DetailのSequenceNumber→元のLedgerIDマッピングを構築
            foreach (var ledger in ledgers)
            {
                foreach (var detail in ledger.Details)
                {
                    if (detail.SequenceNumber > 0)
                    {
                        undoData.DetailOriginalLedgerMap[detail.SequenceNumber.ToString()] = ledger.Id;
                    }
                }
            }

            // フィールド再計算
            var allDetails = ledgers.SelectMany(l => l.Details).ToList();
            target.Income = ledgers.Sum(l => l.Income);
            target.Expense = ledgers.Sum(l => l.Expense);

            // Issue #1932: 統合対象の明細を時系列順（古い→新しい）に 1 度だけ並べ、
            // 残額の選択（末尾＝最新）と摘要の再生成（下の sortedDetailsForSummary）の
            // 両方をこの並びに載せる。判定を 2 か所に書き分けると片方だけ変わる日が来る
            // （development-conventions.md「同じ論理的な処理に手段が 2 通りあるか」）。
            var chronologicalDetails = OrderChronologically(allDetails);

            // 残高: 最新（時系列で末尾）のDetailの残高を使用
            var latestDetail = chronologicalDetails
                .LastOrDefault(d => d.Balance.HasValue);
            if (latestDetail != null)
            {
                target.Balance = latestDetail.Balance!.Value;
            }

            // Issue #983: 摘要が手動編集されている場合、Detail.BusStopsが未同期の
            // 可能性があるため、各Ledgerの摘要からバス停名を抽出してDetailに反映する
            SyncBusStopsFromSummary(ledgers);

            // Issue #920: 摘要を再生成（詳細を新しい順にソートしてからGenerateに渡す）
            // Generate()はICカードの読み取り順（新しい順）を前提に.Reverse()するため、
            // 上で確定した時系列順（古い→新しい）を逆順にして渡す。
            // Issue #1932: 以前はここだけ独自に「UseDate降順・Balance昇順」で並べていたが、
            // 残額の選択と並び順の定義が別々だと片方だけ変わる。定義は OrderChronologically 1 つ。
            var sortedDetailsForSummary = Enumerable.Reverse(chronologicalDetails).ToList();

            // GenerateRailwaySummary内部でSequenceNumber DESCに再ソートされるため、
            // ここで正しい順序に対応するSequenceNumberを一時的に再採番する。
            // FeliCa互換: 小さい値=新しい → sortedDetailsForSummary[0]=最新にSeq=1を割り当て
            // ※この変更はインメモリのみでDB永続化されない
            for (int i = 0; i < sortedDetailsForSummary.Count; i++)
            {
                sortedDetailsForSummary[i].SequenceNumber = i + 1;
            }

            // Issue #1736: Generate が空文字を返す詳細集合（明細を持たない行同士の統合等）では
            // 統合先の元の摘要を維持する（LedgerSplitService と同じ空文字ガード）。
            // 摘要が空欄の行は物品出納簿でどの取引か判別できなくなるため、空欄のまま保存しない。
            var regeneratedSummary = _summaryGenerator.Generate(sortedDetailsForSummary);
            target.Summary = !string.IsNullOrEmpty(regeneratedSummary) ? regeneratedSummary : target.Summary;

            // Noteの統合（非空のものを連結）
            var notes = ledgers
                .Where(l => !string.IsNullOrWhiteSpace(l.Note))
                .Select(l => l.Note!)
                .Distinct()
                .ToList();
            target.Note = notes.Count > 0 ? string.Join("、", notes) : null;

            // Issue #1906: 同行者数は統合対象の最大値を引き継ぐ。統合先の値だけを残すと、
            // 同行者のいた行を統合しただけで「外N名」が 6 年保存の台帳から消える
            // （Note を連結しているのと同じ判断: 統合で情報を落とさない）。
            target.CompanionCount = ledgers.Max(l => l.CompanionCount);

            // 説明テキスト（UI表示用）
            var description = $"{DisplayFormatters.FormatDate(beforeLedgers[0].Date)} {string.Join(" + ", originalSummaryTexts)}";

            // Issue #1954: 「成功」の意味を「統合が確定した」ことだけに定め、その確定位置を
            // scope.Commit() の直後へ置く。コミット後の後処理（Undo データの保存）の失敗を
            // Success に巻き込むと、統合済みなのに「再度お試しください」と案内され、案内どおりの
            // 再実行は「統合対象の履歴が見つかりません」に行き着く（統合元は既に DELETE 済み）。
            // 付帯情報の欠落は HasPostCommitFailure で別途伝える
            // （.claude/rules/development-conventions.md「コミット確定後の後処理を、成否の判定に巻き込まない」）。
            var result = new LedgerMergeResult();

            try
            {
                // Issue #1458: 統合と監査ログ INSERT を同一トランザクションで実行
                var sourceIds = sources.Select(s => s.Id).ToList();
                using (var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false))
                {
                    var success = await _ledgerRepository.MergeLedgersAsync(target.Id, sourceIds, target, scope.Transaction).ConfigureAwait(false);
                    if (!success)
                    {
                        return new LedgerMergeResult
                        {
                            Success = false,
                            ErrorMessage = "履歴の統合に失敗しました。対象の履歴が他の操作で変更された可能性があります。" +
                                           "画面を最新の状態に更新してから再度お試しください。"
                        };
                    }
                    await _operationLogger.LogLedgerMergeAsync(beforeLedgers, target, scope.Transaction).ConfigureAwait(false);
                    scope.Commit();
                }

                // ここから先は統合が確定している（統合元は DELETE 済み）。以降で例外が出ても
                // 下の catch (when result.Success) が付帯情報の欠落として扱い、Success は落とさない。
                result.Success = true;
                result.MergedLedger = target;

                // UndoデータをDBに保存（独立した tx。Undo データは別系統のため tx に含めない）
                var undoJson = JsonSerializer.Serialize(undoData, JsonOptions);
                await _ledgerRepository.SaveMergeHistoryAsync(target.Id, description, undoJson).ConfigureAwait(false);

                _logger.LogInformation(
                    "Merged {Count} ledgers into ledger {TargetId}: {Summary}",
                    ledgers.Count, target.Id, target.Summary);

                return result;
            }
            catch (Exception ex) when (result.Success)
            {
                // Issue #1954: 統合は確定済み。取り消し（Undo）情報を記録できなかっただけとして扱い、
                // Success と ErrorMessage は変えない（呼び出し元は HasPostCommitFailure で案内を切り替える）。
                // 統合自体は成功しているため Error ではなく Warning。本番の Logging:LogLevel=Information でも出力される。
                _logger.LogWarning(ex,
                    "履歴の統合は確定済みですが、コミット後の取り消し情報の保存に失敗しました（TargetLedgerId={TargetId}）",
                    target.Id);
                result.HasPostCommitFailure = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to merge ledgers: {Ids}", string.Join(", ", ledgerIds));
                return new LedgerMergeResult
                {
                    Success = false,
                    // 技術的詳細はログ（上の LogError）へ。UI には 3 要素のユーザー向け文言を返す（Issue #1614）。
                    ErrorMessage = ExceptionMessageFormatter.ToUserMessage(ex, "履歴の統合")
                };
            }
        }

        /// <summary>
        /// 統合を元に戻す（履歴IDで指定）
        /// </summary>
        /// <param name="mergeHistoryId">統合履歴のID</param>
        /// <param name="operatorIdm">操作者IDm</param>
        /// <returns>統合取り消し結果</returns>
        public async Task<LedgerMergeResult> UnmergeAsync(int mergeHistoryId, string? operatorIdm = null)
        {
            try
            {
                // 履歴からUndoデータを取得
                var entry = await GetMergeHistoryEntryAsync(mergeHistoryId).ConfigureAwait(false);
                if (entry == null)
                {
                    return new LedgerMergeResult
                    {
                        Success = false,
                        // 内部 ID は出さず「なぜ／どうすれば」を伝える（.claude/rules/error-messages.md）。
                        // 共有モードでは他 PC が先に同じ統合を取り消したときに実際に発生する。
                        ErrorMessage = "統合履歴が見つかりません。他のPCまたは他の操作で既に取り消された可能性があります。" +
                                       "画面を最新の状態に更新して履歴を確認してください。"
                    };
                }

                // Issue #1806: 台帳の復元と「取り消し済み」マークを同一トランザクションで確定させる。
                // 旧実装は復元を内部でコミットしてから別接続でマークしていたため、マークだけが失敗
                // （共有モードの SQLITE_BUSY / UNC 断）すると「台帳は復元済み・履歴は未取消」が残り、
                // 案内どおりの再実行で統合元が二重に INSERT された（月次帳票の二重計上）。
                using (var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false))
                {
                    var restored = await _ledgerRepository.UnmergeLedgersAsync(entry.UndoData, scope.Transaction).ConfigureAwait(false);
                    if (!restored)
                    {
                        // ログはロールバックより先に書く（Rollback が失敗しても診断の手掛かりを残す。#1745）
                        _logger.LogWarning(
                            "Unmerge aborted: undo data of history {HistoryId} no longer matches ledger {TargetId} (details edited/replaced or target deleted after merge)",
                            mergeHistoryId, entry.TargetLedgerId);
                        scope.Rollback();
                        return new LedgerMergeResult
                        {
                            Success = false,
                            // 復元の 0 行は「Undo データが指す明細・統合先がもう無い」。統合後の編集（明細の
                            // 保存は DELETE + INSERT で rowid が変わる）・分割・削除、または他 PC の先行取り消し。
                            ErrorMessage = "統合の取り消しに失敗しました。統合後にこの履歴の内容が編集・分割・削除されたか、" +
                                           "他のPCまたは他の操作で先に取り消された可能性があります。" +
                                           "画面を最新の状態に更新して履歴を確認してください。"
                        };
                    }

                    // 履歴を取り消し済みにマーク（0 行＝他 PC が先にマークした競合。復元ごと巻き戻す）
                    var marked = await _ledgerRepository.MarkMergeHistoryUndoneAsync(mergeHistoryId, scope.Transaction).ConfigureAwait(false);
                    if (!marked)
                    {
                        _logger.LogWarning(
                            "Unmerge aborted: history {HistoryId} was already marked undone by another operation",
                            mergeHistoryId);
                        scope.Rollback();
                        return new LedgerMergeResult
                        {
                            Success = false,
                            ErrorMessage = "統合の取り消しに失敗しました。この統合は他のPCまたは他の操作で既に取り消されています。" +
                                           "画面を最新の状態に更新して履歴を確認してください。"
                        };
                    }

                    scope.Commit();
                }

                _logger.LogInformation(
                    "Unmerged merge history {HistoryId}: restored ledger {TargetId}",
                    mergeHistoryId, entry.TargetLedgerId);

                return new LedgerMergeResult
                {
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unmerge history {HistoryId}", mergeHistoryId);
                return new LedgerMergeResult
                {
                    Success = false,
                    // 技術的詳細はログ（上の LogError）へ。UI には 3 要素のユーザー向け文言を返す（Issue #1614）。
                    ErrorMessage = ExceptionMessageFormatter.ToUserMessage(ex, "統合の取り消し")
                };
            }
        }

        /// <summary>
        /// 元に戻せる統合履歴の一覧を取得
        /// </summary>
        public async Task<List<MergeHistoryEntry>> GetUndoableMergeHistoriesAsync()
        {
            var rawEntries = await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false).ConfigureAwait(false);
            var result = new List<MergeHistoryEntry>();

            foreach (var (id, mergedAt, targetLedgerId, description, undoDataJson, isUndone) in rawEntries)
            {
                if (isUndone) continue;

                result.Add(new MergeHistoryEntry
                {
                    Id = id,
                    MergedAt = mergedAt,
                    TargetLedgerId = targetLedgerId,
                    Description = description
                    // UndoDataはunmerge実行時にのみロードする（パフォーマンス）
                });
            }

            return result;
        }

        /// <summary>
        /// 指定IDの統合履歴をUndoデータ付きで取得
        /// </summary>
        private async Task<MergeHistoryEntry?> GetMergeHistoryEntryAsync(int historyId)
        {
            var rawEntries = await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false).ConfigureAwait(false);
            var entry = rawEntries.FirstOrDefault(e => e.Id == historyId && !e.IsUndone);

            if (entry.Id == 0) return null;

            var undoData = JsonSerializer.Deserialize<LedgerMergeUndoData>(entry.UndoDataJson, JsonOptions);
            if (undoData == null) return null;

            return new MergeHistoryEntry
            {
                Id = entry.Id,
                MergedAt = entry.MergedAt,
                TargetLedgerId = entry.TargetLedgerId,
                Description = entry.Description,
                UndoData = undoData
            };
        }

        /// <summary>
        /// 統合のバリデーション
        /// </summary>
        private static string? Validate(List<Ledger> ledgers)
        {
            // 同一カードチェック
            var cardIdms = ledgers.Select(l => l.CardIdm).Distinct().ToList();
            if (cardIdms.Count > 1)
            {
                return "異なるカードの履歴は統合できません";
            }

            // 貸出中レコードチェック
            if (ledgers.Any(l => l.IsLentRecord))
            {
                return "貸出中のレコードは統合できません";
            }

            // チャージと利用の混在チェック
            var hasIncome = ledgers.Any(l => l.Income > 0);
            var hasExpense = ledgers.Any(l => l.Expense > 0);
            if (hasIncome && hasExpense)
            {
                return "チャージと利用の履歴は統合できません";
            }

            // チャージとポイント還元の混在チェック（Issue #1736）
            // 両者とも Income>0 / Expense=0 のため上のチェックには掛からないが、
            // 混在した詳細集合は SummaryGenerator.Generate がどの摘要パターンにも該当せず
            // 空文字を返す。集計はチャージ／ポイント還元行を摘要文字列で分類するため
            // （.claude/rules/business-logic.md「ledger を集計するときの前提」）、
            // 結合摘要を新設せず統合自体を拒否する。
            // 暗黙のポイント還元（Issue #942: 負金額・フラグなし）も Generate と同じ分類で扱う。
            var hasCharge = ledgers.Any(l => l.Details.Any(d => d.IsCharge));
            var hasPointRedemption = ledgers.Any(l => l.Details.Any(
                d => d.IsPointRedemption || SummaryGenerator.IsImplicitPointRedemption(d)));
            if (hasCharge && hasPointRedemption)
            {
                return "チャージとポイント還元の履歴は統合できません。" +
                       "取引の種類が異なるため、1行にまとめると摘要でどちらの取引か判別できなくなります。" +
                       "それぞれ別の行のまま管理してください。";
            }

            return null;
        }

        /// <summary>
        /// 統合対象の明細を時系列順（古い→新しい）に並べる（Issue #1932）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「最新の明細」は本メソッドの結果の**末尾**で、統合後の残額はそこから採る。
        /// 統合先の摘要を再生成するときは本メソッドの結果を逆順にして
        /// <c>SummaryGenerator.Generate</c>（ICカードの読み取り順＝新しい順が前提）へ渡す。
        /// 並び順の定義をこの 1 か所に置くのは、残額と摘要が別々の定義に載ると
        /// 片方だけ変わる日が来るため（development-conventions.md）。
        /// </para>
        /// <para>
        /// 決め方は 3 段:
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// <c>UseDate</c> の日付で昇順にグループ化する（日付なしは末尾）。
        /// Issue #1904 のとおり第一キーは rowid ではなく日付 —
        /// 統合済み台帳では別バッチ由来の rowid が日付と無関係に交錯する。
        /// </description></item>
        /// <item><description>
        /// 同一日内は残高チェーンで解決する（<c>LedgerDetailChronologicalSorter</c>。
        /// <c>LedgerRepository.GetByIdAsync</c> が明細の並びを決めるのに使っているのと同じ定義）。
        /// 同日の時刻はすべて 00:00 で保存されるため日付では順序が決まらず、
        /// 残高の増減が唯一の客観的な手掛かりになる（business-logic.md
        /// 「同一日内の順序は id では決まらない」）。
        /// </description></item>
        /// <item><description>
        /// 全体では解けないとき、<b>台帳ごと</b>に分けて解き直す。台帳の中の順序はその台帳の
        /// 残高チェーンで決め（解けなければ規約へ倒す）、台帳どうしの順序は規約
        /// （FeliCa 互換で小さい <c>SequenceNumber</c> ほど新しい）で決める。
        /// </description></item>
        /// </list>
        /// <para>
        /// 残高チェーンを規約より優先するのは、残高不足マージ（Issue #978）で作られた台帳が
        /// 「チャージ → 利用」の順に挿入される**規約の明示的な例外**だから。
        /// この台帳では最大 <c>SequenceNumber</c> が最新であり、規約だけで並べると
        /// チャージ後の残高（＝利用前の過大な値）が 6 年保存の台帳の残額欄に入る。
        /// 残高チェーンはこの台帳も通常の台帳も同じ手順で正しく解く。
        /// </para>
        /// <para>
        /// 段 3 で「まず台帳ごとに分ける」のが要（コードレビューで検出）。統合対象**全体**を
        /// 1 本のチェーンに掛けると、選択されなかった台帳が間に挟まるだけでチェーンが切れる
        /// （履歴一覧はチェックボックス選択なので通常操作で起きる）。そこで規約へ丸ごと倒すと、
        /// 規約の例外である残高不足マージ台帳ではチャージ側が最新と判定され、
        /// **本 Issue が消したはずの過大な残額が復活する**。台帳の中の並びは統合対象の選び方に
        /// 左右されないので、そこだけはチェーンが常に解ける（`LedgerRepository.GetByIdAsync` が
        /// 同じ定義で明細を並べているのと同じ理由）。
        /// </para>
        /// </remarks>
        /// <param name="details">統合対象の全明細（順序は問わない）</param>
        /// <returns>時系列順（古い→新しい）に並べた新しいリスト</returns>
        internal static List<LedgerDetail> OrderChronologically(IEnumerable<LedgerDetail> details)
        {
            return details
                .GroupBy(d => d.UseDate?.Date ?? DateTime.MaxValue)
                .OrderBy(g => g.Key)
                .SelectMany(g => OrderWithinSameDate(g.ToList()))
                .ToList();
        }

        /// <summary>
        /// 同一日の明細を時系列順（古い→新しい）に並べる（Issue #1932）
        /// </summary>
        private static List<LedgerDetail> OrderWithinSameDate(List<LedgerDetail> sameDateDetails)
        {
            if (sameDateDetails.Count <= 1)
            {
                return sameDateDetails;
            }

            // 残高チェーンで一意に決まるならそれが正。
            var chained = LedgerDetailChronologicalSorter.TrySortByBalanceChain(sameDateDetails);
            if (chained != null)
            {
                return chained;
            }

            // 解けないときは台帳ごとに分け直す。台帳の中は再びチェーンで（統合対象の選び方に
            // 左右されないので通常はここで解ける）、台帳どうしは規約で並べる。
            return sameDateDetails
                .GroupBy(d => d.LedgerId)
                .OrderByDescending(g => g.Min(d => d.SequenceNumber > 0 ? d.SequenceNumber : int.MinValue))
                .ThenByDescending(g => g.Max(d => d.Balance ?? 0))
                .SelectMany(g => OrderWithinSameLedger(g.ToList()))
                .ToList();
        }

        /// <summary>
        /// 同一台帳・同一日の明細を時系列順（古い→新しい）に並べる（Issue #1932）
        /// </summary>
        private static List<LedgerDetail> OrderWithinSameLedger(List<LedgerDetail> sameLedgerDetails)
        {
            if (sameLedgerDetails.Count <= 1)
            {
                return sameLedgerDetails;
            }

            return LedgerDetailChronologicalSorter.TrySortByBalanceChain(sameLedgerDetails)
                ?? SummaryGenerator.SortChronologically(sameLedgerDetails);
        }

        /// <summary>
        /// 各Ledgerの摘要からバス停名を抽出してDetail.BusStopsに反映する（Issue #983）
        /// </summary>
        /// <remarks>
        /// 摘要の直接編集（LedgerRowEditViewModel）ではDetail.BusStopsが更新されないため、
        /// 統合時にSummaryGenerator.Generate()が古いBusStopsから摘要を再生成してしまう。
        /// この処理で摘要とDetailの整合性を回復する。
        /// </remarks>
        internal static void SyncBusStopsFromSummary(List<Ledger> ledgers)
        {
            foreach (var ledger in ledgers)
            {
                // Issue #1904: 摘要は時系列（交互ブロック）で生成され、バスブロックは
                // 複数になり得る。抽出したバス停名（摘要中の出現順）と位置で対応付けるため、
                // バス明細は生成側の出力順そのもの（GetBusStopEmissionOrder）で並べる
                //（並び順の定義を消費側に書き写さない）
                var busDetails = SummaryGenerator.GetBusStopEmissionOrder(ledger.Details);
                if (busDetails.Count == 0) continue;

                // Issue #1818: 抽出パターンは組織設定 BusLabel から導出する
                //（生成側だけが設定値を使い、抽出側がリテラルを直書きする乖離の防止）
                var blocks = SummaryGenerator.ExtractBusStopBlocks(ledger.Summary);
                if (blocks.Count == 0) continue;

                if (busDetails.Count == 1)
                {
                    // 先頭ブロックのみ書き戻す（従来挙動）。複数ブロックの結合テキストを
                    // 1 明細へ書き込むと「A～B、C～D」となり ParseBusRoute で解析できない
                    busDetails[0].BusStops = blocks[0];
                }
                else
                {
                    // 複数件のバス利用: 「、」で分割してDetailに対応付け
                    var parts = string.Join("、", blocks).Split('、');
                    if (parts.Length == busDetails.Count)
                    {
                        for (int i = 0; i < parts.Length; i++)
                        {
                            busDetails[i].BusStops = parts[i];
                        }
                    }
                }
            }
        }
    }
}
