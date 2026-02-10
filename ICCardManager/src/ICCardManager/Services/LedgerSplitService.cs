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
    /// 履歴分割の結果（Issue #634）
    /// </summary>
    public class LedgerSplitResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public List<int> CreatedLedgerIds { get; set; } = new();
    }

    /// <summary>
    /// Ledgerレコードをグループに基づいて別々の履歴レコードに分割するサービス（Issue #634）
    /// </summary>
    public class LedgerSplitService
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly SummaryGenerator _summaryGenerator;
        private readonly OperationLogger _operationLogger;
        private readonly ILogger<LedgerSplitService> _logger;

        public LedgerSplitService(
            ILedgerRepository ledgerRepository,
            SummaryGenerator summaryGenerator,
            OperationLogger operationLogger,
            ILogger<LedgerSplitService> logger)
        {
            _ledgerRepository = ledgerRepository;
            _summaryGenerator = summaryGenerator;
            _operationLogger = operationLogger;
            _logger = logger;
        }

        /// <summary>
        /// Ledgerをグループに基づいて分割する
        /// </summary>
        /// <param name="ledgerId">分割対象のLedger ID</param>
        /// <param name="groupedDetails">GroupId付きの詳細リスト</param>
        /// <param name="operatorIdm">操作者IDm</param>
        /// <returns>分割結果</returns>
        public async Task<LedgerSplitResult> SplitAsync(
            int ledgerId,
            IReadOnlyList<LedgerDetail> groupedDetails,
            string? operatorIdm = null)
        {
            // バリデーション: GroupIdが2種類以上あること
            var groups = groupedDetails
                .Where(d => d.GroupId.HasValue)
                .GroupBy(d => d.GroupId!.Value)
                .OrderBy(g => g.Key)
                .ToList();

            if (groups.Count < 2)
            {
                return new LedgerSplitResult
                {
                    Success = false,
                    ErrorMessage = "分割するには2つ以上のグループが必要です"
                };
            }

            // 元のLedgerをDBから取得
            var originalLedger = await _ledgerRepository.GetByIdAsync(ledgerId);
            if (originalLedger == null)
            {
                return new LedgerSplitResult
                {
                    Success = false,
                    ErrorMessage = "履歴データが見つかりません"
                };
            }

            // 操作ログ用に元のデータを保存
            var beforeLedger = CloneLedger(originalLedger);

            try
            {
                var createdIds = new List<int>();
                var allSplitLedgers = new List<Ledger>();

                // グループ1: 元のLedgerを更新
                var firstGroup = groups[0].ToList();
                ClearGroupIds(firstGroup);

                var (firstIncome, firstExpense, firstBalance) = CalculateGroupFinancials(firstGroup);
                var firstSummary = _summaryGenerator.Generate(firstGroup);

                originalLedger.Summary = !string.IsNullOrEmpty(firstSummary) ? firstSummary : originalLedger.Summary;
                originalLedger.Income = firstIncome;
                originalLedger.Expense = firstExpense;
                originalLedger.Balance = firstBalance;

                await _ledgerRepository.ReplaceDetailsAsync(originalLedger.Id, firstGroup);
                await _ledgerRepository.UpdateAsync(originalLedger);
                allSplitLedgers.Add(originalLedger);

                // グループ2以降: 新しいLedgerを作成
                for (int i = 1; i < groups.Count; i++)
                {
                    var groupDetails = groups[i].ToList();
                    ClearGroupIds(groupDetails);

                    var (income, expense, balance) = CalculateGroupFinancials(groupDetails);
                    var summary = _summaryGenerator.Generate(groupDetails);

                    // 新しいLedgerを作成（メタデータは元からコピー）
                    var newLedger = new Ledger
                    {
                        CardIdm = originalLedger.CardIdm,
                        LenderIdm = originalLedger.LenderIdm,
                        StaffName = originalLedger.StaffName,
                        ReturnerIdm = originalLedger.ReturnerIdm,
                        LentAt = originalLedger.LentAt,
                        ReturnedAt = originalLedger.ReturnedAt,
                        IsLentRecord = false,
                        Date = GetGroupDate(groupDetails, originalLedger.Date),
                        Summary = !string.IsNullOrEmpty(summary) ? summary : "（分割）",
                        Income = income,
                        Expense = expense,
                        Balance = balance,
                        Note = null
                    };

                    var newId = await _ledgerRepository.InsertAsync(newLedger);
                    newLedger.Id = newId;
                    await _ledgerRepository.InsertDetailsAsync(newId, groupDetails);

                    createdIds.Add(newId);
                    allSplitLedgers.Add(newLedger);
                }

                // 操作ログを記録
                await _operationLogger.LogLedgerSplitAsync(operatorIdm, beforeLedger, allSplitLedgers);

                _logger.LogInformation(
                    "Split ledger {LedgerId} into {Count} ledgers (new IDs: {NewIds})",
                    ledgerId, allSplitLedgers.Count, string.Join(", ", createdIds));

                return new LedgerSplitResult
                {
                    Success = true,
                    CreatedLedgerIds = createdIds
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to split ledger {LedgerId}", ledgerId);
                return new LedgerSplitResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// グループ内の詳細からIncome/Expense/Balanceを計算
        /// </summary>
        internal static (int Income, int Expense, int Balance) CalculateGroupFinancials(
            List<LedgerDetail> groupDetails)
        {
            int income = groupDetails
                .Where(d => d.IsCharge && d.Amount.HasValue)
                .Sum(d => d.Amount!.Value);

            int expense = groupDetails
                .Where(d => !d.IsCharge && !d.IsPointRedemption && d.Amount.HasValue)
                .Sum(d => d.Amount!.Value);

            // Balance = グループ内の最後のdetailの残高（時系列順）
            var lastDetail = groupDetails
                .Where(d => d.Balance.HasValue)
                .OrderBy(d => d.SequenceNumber > 0 ? d.SequenceNumber : int.MaxValue)
                .ThenBy(d => d.UseDate ?? DateTime.MaxValue)
                .LastOrDefault();

            int balance = lastDetail?.Balance ?? 0;

            return (income, expense, balance);
        }

        /// <summary>
        /// グループの日付を決定（最初のdetailのUseDate、なければ元の日付）
        /// </summary>
        private static DateTime GetGroupDate(List<LedgerDetail> groupDetails, DateTime originalDate)
        {
            var firstDate = groupDetails
                .Where(d => d.UseDate.HasValue)
                .OrderBy(d => d.SequenceNumber > 0 ? d.SequenceNumber : int.MaxValue)
                .ThenBy(d => d.UseDate)
                .FirstOrDefault()
                ?.UseDate;

            return firstDate ?? originalDate;
        }

        /// <summary>
        /// 分割後の詳細のGroupIdをクリア（自動検出モードに戻す）
        /// </summary>
        private static void ClearGroupIds(List<LedgerDetail> details)
        {
            foreach (var detail in details)
            {
                detail.GroupId = null;
            }
        }

        /// <summary>
        /// Ledgerのクローン（操作ログ用）
        /// </summary>
        private static Ledger CloneLedger(Ledger source)
        {
            return new Ledger
            {
                Id = source.Id,
                CardIdm = source.CardIdm,
                LenderIdm = source.LenderIdm,
                Date = source.Date,
                Summary = source.Summary,
                Income = source.Income,
                Expense = source.Expense,
                Balance = source.Balance,
                StaffName = source.StaffName,
                Note = source.Note,
                ReturnerIdm = source.ReturnerIdm,
                LentAt = source.LentAt,
                ReturnedAt = source.ReturnedAt,
                IsLentRecord = source.IsLentRecord
            };
        }
    }
}
