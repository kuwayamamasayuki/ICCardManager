using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 複数のLedgerレコードを統合するサービス
    /// </summary>
    /// <remarks>
    /// Issue #548対応: 履歴一覧から隣接するエントリを1つに統合する。
    /// 統合先は最も古い（最初の）エントリとし、他のエントリのDetailsを移動後に削除する。
    /// </remarks>
    public class LedgerMergeService
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly OperationLogger _operationLogger;
        private readonly ILogger<LedgerMergeService> _logger;

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

            // 統合前の状態を保存（ログ用）
            var beforeLedgers = ledgers.ToList();

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

            return null;
        }
    }
}
