using System;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 月次帳票のデータ準備サービス
    /// </summary>
    /// <remarks>
    /// Issue #841: ReportService（Excel出力）とPrintService（印刷プレビュー）の
    /// 共通データ準備ロジックを統合。繰越・月計・累計・年度境界の判定を一元化する。
    /// </remarks>
    public class ReportDataBuilder : IReportDataBuilder
    {
        private readonly ICardRepository _cardRepository;
        private readonly ILedgerRepository _ledgerRepository;

        public ReportDataBuilder(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
        }

        /// <inheritdoc/>
        public async Task<MonthlyReportData> BuildAsync(string cardIdm, int year, int month)
        {
            // カード情報を取得
            var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
            if (card == null)
            {
                return null;
            }

            // 前月末残高を取得（繰越行表示および残高チェーン並替に使用）
            int? precedingBalance;
            if (month == 4)
            {
                precedingBalance = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
            }
            else
            {
                precedingBalance = await GetPreviousMonthBalanceAsync(cardIdm, year, month);
            }

            // Issue #784: 残高チェーンに基づいて同一日内の時系列順を復元
            var ledgers = LedgerOrderHelper.ReorderByBalanceChain(
                (await _ledgerRepository.GetByMonthAsync(cardIdm, year, month))
                    .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()),
                precedingBalance);

            // 繰越行データを生成
            CarryoverRowData carryover = null;
            if (precedingBalance.HasValue)
            {
                if (month == 4)
                {
                    carryover = new CarryoverRowData
                    {
                        Date = new DateTime(year, 4, 1),
                        Summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
                        Income = precedingBalance.Value,
                        Balance = precedingBalance.Value
                    };
                }
                else
                {
                    int previousMonth = month == 1 ? 12 : month - 1;
                    carryover = new CarryoverRowData
                    {
                        Date = new DateTime(year, month, 1),
                        Summary = SummaryGenerator.GetCarryoverFromPreviousMonthSummary(previousMonth),
                        // Issue #753: 月次繰越の受入欄は空欄（受入金額を表示するのは4月の前年度繰越のみ）
                        Income = null,
                        Balance = precedingBalance.Value
                    };
                }
            }

            // 月計を計算
            var monthlyIncome = ledgers.Sum(l => l.Income);
            var monthlyExpense = ledgers.Sum(l => l.Expense);
            var monthEndBalance = ledgers.LastOrDefault()?.Balance ?? 0;

            // 累計データを計算（4月の月計残額表示にも使用）
            var fiscalYearStartYear = month >= 4 ? year : year - 1;
            var fiscalYearStart = new DateTime(fiscalYearStartYear, 4, 1);
            var fiscalYearEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            // Issue #784: 残高チェーンに基づいて時系列順を復元
            var yearlyLedgers = LedgerOrderHelper.ReorderByBalanceChain(
                (await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd))
                    .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()));

            var yearlyIncome = yearlyLedgers.Sum(l => l.Income);
            var yearlyExpense = yearlyLedgers.Sum(l => l.Expense);
            var currentBalance = yearlyLedgers.LastOrDefault()?.Balance ?? monthEndBalance;

            // 月計・累計の組み立て
            ReportTotalData monthlyTotal;
            ReportTotalData cumulativeTotal;

            if (month == 4)
            {
                // Issue #813: 4月は月計と累計が同額のため累計行を省略し、月計行に残額を表示
                var aprilBalance = yearlyLedgers.Any()
                    ? currentBalance
                    : (precedingBalance ?? 0);
                monthlyTotal = new ReportTotalData
                {
                    Label = SummaryGenerator.GetMonthlySummary(month),
                    Income = monthlyIncome,
                    Expense = monthlyExpense,
                    Balance = aprilBalance
                };
                cumulativeTotal = null;
            }
            else
            {
                monthlyTotal = new ReportTotalData
                {
                    Label = SummaryGenerator.GetMonthlySummary(month),
                    Income = monthlyIncome,
                    Expense = monthlyExpense,
                    Balance = null
                };
                cumulativeTotal = new ReportTotalData
                {
                    Label = SummaryGenerator.GetCumulativeSummary(),
                    Income = yearlyIncome,
                    Expense = yearlyExpense,
                    Balance = currentBalance
                };
            }

            // 3月のみ次年度繰越
            int? carryoverToNextYear = month == 3 ? currentBalance : (int?)null;

            return new MonthlyReportData
            {
                Card = card,
                Year = year,
                Month = month,
                PrecedingBalance = precedingBalance,
                Carryover = carryover,
                Ledgers = ledgers,
                MonthlyTotal = monthlyTotal,
                CumulativeTotal = cumulativeTotal,
                CarryoverToNextYear = carryoverToNextYear
            };
        }

        /// <summary>
        /// 前月の残高を取得
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <returns>前月残高。過去のデータがない場合はnull</returns>
        private async Task<int?> GetPreviousMonthBalanceAsync(string cardIdm, int year, int month)
        {
            // 前月の年月を計算
            int previousYear, previousMonth;
            if (month == 1)
            {
                previousYear = year - 1;
                previousMonth = 12;
            }
            else
            {
                previousYear = year;
                previousMonth = month - 1;
            }

            // 前月の履歴を取得し、最後の残高を返す
            // Issue #784: 残高チェーンに基づいて時系列順を復元
            var previousLedgers = LedgerOrderHelper.ReorderByBalanceChain(
                (await _ledgerRepository.GetByMonthAsync(cardIdm, previousYear, previousMonth))
                    .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()));

            if (previousLedgers.Count > 0)
            {
                return previousLedgers.Last().Balance;
            }

            // 前月のデータがない場合は、さらに前の月から繰り越しを探す
            // 年度開始月（4月）まで遡って繰越残高を取得
            var fiscalYearStartYear = month >= 4 ? year : year - 1;
            var carryover = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, fiscalYearStartYear - 1);
            return carryover;
        }
    }
}
