using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data;
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
        private readonly DbContext _dbContext;
        private readonly ILogger<LedgerSplitService> _logger;

        public LedgerSplitService(
            ILedgerRepository ledgerRepository,
            SummaryGenerator summaryGenerator,
            OperationLogger operationLogger,
            DbContext dbContext,
            ILogger<LedgerSplitService> logger)
        {
            _ledgerRepository = ledgerRepository;
            _summaryGenerator = summaryGenerator;
            _operationLogger = operationLogger;
            _dbContext = dbContext;
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
            var originalLedger = await _ledgerRepository.GetByIdAsync(ledgerId).ConfigureAwait(false);
            if (originalLedger == null)
            {
                return new LedgerSplitResult
                {
                    Success = false,
                    // Issue #1753: 内部 ID を出さず「なぜ／どうすれば」を伝える（.claude/rules/error-messages.md）
                    ErrorMessage = "分割対象の履歴が見つかりません。他のPCまたは他の操作で削除・統合された可能性があります。" +
                                   "画面を最新の状態に更新してから再度お試しください。"
                };
            }

            // 操作ログ用に元のデータを保存
            // Issue #1959: コピーの生成手段は Common/LedgerCloner ただ 1 つに寄せる。
            // 私的なクローンを各サービスに置くと、モデルへ列を足したとき片方だけが更新される。
            var beforeLedger = LedgerCloner.Clone(originalLedger);

            try
            {
                var createdIds = new List<int>();
                var allSplitLedgers = new List<Ledger>();

                // Issue #1458: 分割処理全体 (ReplaceDetails/Update/Insert/InsertDetails と監査ログ INSERT)
                // を単一の SQLiteTransaction で実行。部分書き込みも原子的に rollback される。
                using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

                // グループ1: 元のLedgerを更新
                var firstGroup = groups[0].ToList();
                ClearGroupIds(firstGroup);

                var (firstIncome, firstExpense, firstBalance) = CalculateGroupFinancials(firstGroup);
                var firstSummary = _summaryGenerator.Generate(firstGroup);

                originalLedger.Summary = !string.IsNullOrEmpty(firstSummary) ? firstSummary : originalLedger.Summary;
                originalLedger.Income = firstIncome;
                originalLedger.Expense = firstExpense;
                originalLedger.Balance = firstBalance;

                // Issue #880: 挿入順を逆にして、FeliCa互換のrowid順序を維持
                // ReplaceDetailsAsync はDELETE+INSERTのため、rowidが再採番される
                // DBは rowid DESC で時系列表示（大きいrowid＝古い＝先に表示）するので、
                // 新しい明細から先に挿入して小さいrowidを割り当てる必要がある
                var detailsReplaced = await _ledgerRepository.ReplaceDetailsAsync(
                    originalLedger.Id, firstGroup.AsEnumerable().Reverse(), scope.Transaction).ConfigureAwait(false);

                // Issue #1753: UpdateAsync は影響行数 0 で false を返す。共有モードでは読み取り（上の
                // GetByIdAsync）と本トランザクションの間に他 PC が同じ履歴を統合・削除し得るため、
                // 戻り値を破棄すると「元 Ledger を更新できていないのに新 Ledger だけ作られる」状態になる。
                // 早期 return で scope は Dispose 時に自動ロールバックされる。
                var targetUpdated = await _ledgerRepository.UpdateAsync(
                    originalLedger, scope.Transaction).ConfigureAwait(false);

                if (!detailsReplaced || !targetUpdated)
                {
                    return new LedgerSplitResult
                    {
                        Success = false,
                        ErrorMessage = "履歴の分割に失敗しました。対象の履歴が他の操作で変更された可能性があります。" +
                                       "画面を最新の状態に更新してから再度お試しください。"
                    };
                }

                // Issue #1959: 監査ログの AfterData は allSplitLedgers をそのまま JSON 化する。
                // originalLedger.Details は分割前の全明細を保持したままなので、更新しないと
                // 「グループ1が全明細を保持し、新しい台帳は明細ゼロ」という実際とは逆の記録になる
                //（BeforeData が明細を持つようになったことで、この非対称が監査ログから読めてしまう）。
                // 並びは時系列昇順のまま（反転は DB 呼び出しにだけ適用する。Issue #1913）。
                originalLedger.Details = firstGroup;

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
                        CompanionCount = originalLedger.CompanionCount,
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

                    var newId = await _ledgerRepository.InsertAsync(newLedger, scope.Transaction).ConfigureAwait(false);
                    newLedger.Id = newId;
                    // Issue #880: 挿入順を逆にしてFeliCa互換のrowid順序を維持（上記コメント参照）
                    await _ledgerRepository.InsertDetailsAsync(newId, groupDetails.AsEnumerable().Reverse(), scope.Transaction).ConfigureAwait(false);

                    // Issue #1959: 監査ログの AfterData に実際の明細を載せる（既定の空リストのままにしない）
                    newLedger.Details = groupDetails;

                    createdIds.Add(newId);
                    allSplitLedgers.Add(newLedger);
                }

                // 操作ログを同一 tx で記録
                await _operationLogger.LogLedgerSplitAsync(beforeLedger, allSplitLedgers, scope.Transaction).ConfigureAwait(false);

                scope.Commit();

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
                    // Issue #1991: 生の ex.Message は英語・技術用語を含み職員には解読不能で、
                    // 内部実装の露出にもなる（#1614）。技術的詳細は直前の LogError が残す。
                    ErrorMessage = ExceptionMessageFormatter.ToUserMessage(ex, "履歴の分割")
                };
            }
        }

        /// <summary>
        /// グループ内の詳細からIncome/Expense/Balanceを計算
        /// </summary>
        internal static (int Income, int Expense, int Balance) CalculateGroupFinancials(
            List<LedgerDetail> groupDetails)
        {
            // チャージの受入金額
            int chargeIncome = groupDetails
                .Where(d => d.IsCharge && d.Amount.HasValue)
                .Sum(d => d.Amount!.Value);

            // Issue #1053: ポイント還元の受入金額（金額は負値なので絶対値をIncomeとする）
            int pointRedemptionIncome = groupDetails
                .Where(d => (d.IsPointRedemption || SummaryGenerator.IsImplicitPointRedemption(d)) && d.Amount.HasValue)
                .Sum(d => Math.Abs(d.Amount!.Value));

            int income = chargeIncome + pointRedemptionIncome;

            // Issue #1053: 暗黙的ポイント還元もExpenseから除外
            int expense = groupDetails
                .Where(d => !d.IsCharge && !d.IsPointRedemption
                            && !SummaryGenerator.IsImplicitPointRedemption(d)
                            && d.Amount.HasValue)
                .Sum(d => d.Amount!.Value);

            // Balance = グループ内の最後のdetailの残高（時系列順）
            // Issue #1004: 残高チェーンで正しい時系列順を決定し、最新（最後）の残高を取得
            // カスタムソート（SequenceNumber DESC等）ではポイント還元と利用の順序が
            // 正しくない場合がある
            var sorted = LedgerDetailChronologicalSorter.Sort(groupDetails);
            var lastDetail = sorted.LastOrDefault(d => d.Balance.HasValue);

            int balance = lastDetail?.Balance ?? 0;

            return (income, expense, balance);
        }

        /// <summary>
        /// グループの日付を決定（最初のdetailのUseDate、なければ元の日付）
        /// </summary>
        private static DateTime GetGroupDate(List<LedgerDetail> groupDetails, DateTime originalDate)
        {
            // Issue #1004: 残高チェーンで正しい時系列順を決定し、最古の日付を取得
            var firstDate = LedgerDetailChronologicalSorter.Sort(groupDetails)
                .FirstOrDefault(d => d.UseDate.HasValue)
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
    }
}
