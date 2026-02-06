using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public Ledger? MergedLedger { get; set; }
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
                IsLentRecord = ledger.IsLentRecord
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
                IsLentRecord = IsLentRecord
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
            ILogger<LedgerMergeService> logger)
        {
            _ledgerRepository = ledgerRepository;
            _summaryGenerator = summaryGenerator;
            _operationLogger = operationLogger;
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
                var ledger = await _ledgerRepository.GetByIdAsync(id);
                if (ledger == null)
                {
                    return new LedgerMergeResult
                    {
                        Success = false,
                        ErrorMessage = $"履歴 ID={id} が見つかりません"
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
            var beforeLedgers = ledgers.ToList();
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

            // 残高: 最新のDetailの残高を使用
            var latestDetail = allDetails
                .Where(d => d.Balance.HasValue)
                .OrderBy(d => d.SequenceNumber > 0 ? d.SequenceNumber : int.MaxValue)
                .ThenBy(d => d.UseDate ?? DateTime.MaxValue)
                .LastOrDefault();
            if (latestDetail != null)
            {
                target.Balance = latestDetail.Balance!.Value;
            }

            // 摘要を再生成
            target.Summary = _summaryGenerator.Generate(allDetails);

            // Noteの統合（非空のものを連結）
            var notes = ledgers
                .Where(l => !string.IsNullOrWhiteSpace(l.Note))
                .Select(l => l.Note!)
                .Distinct()
                .ToList();
            target.Note = notes.Count > 0 ? string.Join("、", notes) : null;

            // 説明テキスト（UI表示用）
            var description = $"{beforeLedgers[0].Date:yyyy/MM/dd} {string.Join(" + ", originalSummaryTexts)}";

            try
            {
                // リポジトリで統合実行（トランザクション）
                var sourceIds = sources.Select(s => s.Id).ToList();
                var success = await _ledgerRepository.MergeLedgersAsync(target.Id, sourceIds, target);

                if (!success)
                {
                    return new LedgerMergeResult
                    {
                        Success = false,
                        ErrorMessage = "統合処理に失敗しました"
                    };
                }

                // UndoデータをDBに保存
                var undoJson = JsonSerializer.Serialize(undoData, JsonOptions);
                await _ledgerRepository.SaveMergeHistoryAsync(target.Id, description, undoJson);

                // 操作ログ記録
                await _operationLogger.LogLedgerMergeAsync(operatorIdm, beforeLedgers, target);

                _logger.LogInformation(
                    "Merged {Count} ledgers into ledger {TargetId}: {Summary}",
                    ledgers.Count, target.Id, target.Summary);

                return new LedgerMergeResult
                {
                    Success = true,
                    MergedLedger = target
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to merge ledgers: {Ids}", string.Join(", ", ledgerIds));
                return new LedgerMergeResult
                {
                    Success = false,
                    ErrorMessage = $"統合中にエラーが発生しました: {ex.Message}"
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
                var entry = await GetMergeHistoryEntryAsync(mergeHistoryId);
                if (entry == null)
                {
                    return new LedgerMergeResult
                    {
                        Success = false,
                        ErrorMessage = "統合履歴が見つかりません"
                    };
                }

                var success = await _ledgerRepository.UnmergeLedgersAsync(entry.UndoData);

                if (!success)
                {
                    return new LedgerMergeResult
                    {
                        Success = false,
                        ErrorMessage = "統合の取り消しに失敗しました"
                    };
                }

                // 履歴を取り消し済みにマーク
                await _ledgerRepository.MarkMergeHistoryUndoneAsync(mergeHistoryId);

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
                    ErrorMessage = $"統合の取り消し中にエラーが発生しました: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 元に戻せる統合履歴の一覧を取得
        /// </summary>
        public async Task<List<MergeHistoryEntry>> GetUndoableMergeHistoriesAsync()
        {
            var rawEntries = await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false);
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
            var rawEntries = await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false);
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

            return null;
        }
    }
}
