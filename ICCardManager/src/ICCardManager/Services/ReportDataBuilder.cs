using System;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
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
            var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);
            if (card == null)
            {
                return null;
            }

            // 前月末残高を取得（繰越行表示および残高チェーン並替に使用）
            int? precedingBalance;
            if (month == 4)
            {
                precedingBalance = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1).ConfigureAwait(false);
            }
            else
            {
                precedingBalance = await GetPreviousMonthBalanceAsync(cardIdm, year, month).ConfigureAwait(false);
            }

            // Issue #784: 残高チェーンに基づいて同一日内の時系列順を復元
            var ledgers = LedgerOrderHelper.ReorderByBalanceChain(
                (await _ledgerRepository.GetByMonthAsync(cardIdm, year, month).ConfigureAwait(false))
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
            // 紙出納簿移行カード（Issue #510）では「○月から繰越」レコードが DB に Income=残高 で
            // 保存される運用のため、月計から除外しないと前月の累計と二重計上になる。
            // 通常運用カードでは Income=null で生成されるため Sum 影響なし。
            var monthlyIncome = ledgers
                .Where(l => !SummaryGenerator.IsMidYearCarryoverSummary(l.Summary))
                .Sum(l => l.Income);
            var monthlyExpense = ledgers.Sum(l => l.Expense);

            // 累計データを計算（4月の月計残額表示にも使用）
            var fiscalYearStartYear = FiscalYearHelper.GetFiscalYear(year, month);
            var fiscalYearStart = FiscalYearHelper.GetFiscalYearStart(fiscalYearStartYear);
            var fiscalYearEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            // Issue #784: 残高チェーンに基づいて時系列順を復元
            var yearlyLedgers = LedgerOrderHelper.ReorderByBalanceChain(
                (await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd).ConfigureAwait(false))
                    .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()));

            // Issue #1494: 「前年度より繰越」レコードは DB に保存されず CarryoverRowData として
            // 表示用に合成されるため、月計・累計の集計に明示的に加算する必要がある。
            // 4月の precedingBalance と「年度開始時の前年度繰越額」は同値なので、変数として一元化。
            var fiscalYearCarryoverIncome = (month == 4)
                ? (precedingBalance ?? 0)
                : (await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, fiscalYearStartYear - 1).ConfigureAwait(false) ?? 0);

            // 累計も同様に「○月から繰越」レコードを除外（紙出納簿移行カード対策）
            var yearlyIncome = yearlyLedgers
                .Where(l => !SummaryGenerator.IsMidYearCarryoverSummary(l.Summary))
                .Sum(l => l.Income) + fiscalYearCarryoverIncome;
            var yearlyExpense = yearlyLedgers.Sum(l => l.Expense);

            // Issue #1728: 年度内に台帳が1件もない遊休カードでは前年度繰越へフォールバックする。
            // 旧実装は当月末残高（monthEndBalance）へ落としていたが、年度範囲は当月を包含するため
            // 年度内が空なら当月も必ず空であり、フォールバック先は常に 0 だった。その結果、
            // 5月以降の累計残額と3月の次年度繰越が 0 円で出力され、同一帳票内の繰越行および
            // 翌年度4月の「前年度より繰越」と矛盾していた。
            // precedingBalance は 4月なら前年度繰越、5月以降は GetPreviousMonthBalanceAsync
            // （年度内が空なら同じ前年度繰越へフォールバックする）。前年度繰越も無い新規カードでは
            // null になり、その場合 fiscalYearCarryoverIncome が 0 を与える。
            var currentBalance = yearlyLedgers.LastOrDefault()?.Balance
                ?? precedingBalance
                ?? fiscalYearCarryoverIncome;

            // Issue #1215: 紙の出納簿から年度途中で移行した場合、
            // 該当年度の累計には紙の出納簿時代の累計を加算する。
            // 4月は cumulativeTotal=null（Issue #813）かつ月計には紙時代累計を含めない設計のため
            // 加算は5月以降のみで意味を持つ。意図を明確化するため month != 4 ガードを併記。
            if (card.CarryoverFiscalYear.HasValue
                && card.CarryoverFiscalYear.Value == fiscalYearStartYear
                && month != 4)
            {
                yearlyIncome += card.CarryoverIncomeTotal;
                yearlyExpense += card.CarryoverExpenseTotal;
            }

            // 月計・累計の組み立て
            ReportTotalData monthlyTotal;
            ReportTotalData cumulativeTotal;

            if (month == 4)
            {
                // Issue #813: 4月は月計と累計が同額のため累計行を省略し、月計行に残額を表示
                // Issue #1494: 4月計の受入には前年度繰越額を含める（紙の出納簿様式で「受入−払出=残額」が成立するように）
                // Issue #1728: 台帳なしのときの前年度繰越フォールバックは currentBalance が担う。
                // 以前は4月だけ別式（aprilBalance）で手当てしていたため、同じ考慮が5月以降と
                // 3月の次年度繰越に入らず実装漏れとなった。等価であることを確認のうえ一本化した。
                monthlyTotal = new ReportTotalData
                {
                    Label = SummaryGenerator.GetMonthlySummary(month),
                    Income = monthlyIncome + fiscalYearCarryoverIncome,
                    Expense = monthlyExpense,
                    Balance = currentBalance
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
        /// 前月末の残高を取得
        /// </summary>
        /// <remarks>
        /// Issue #1602: 直前月が利用空白でも、年度開始月（4月）まで 1 ヶ月ずつ遡り、
        /// 利用のあった最後の月の月末残高を返す。これにより「○月から繰越」行の残高が
        /// 利用空白月をまたいでも残高チェーン（受入 − 払出 = 残額）を維持する。
        /// 年度開始月まで遡っても 1 件もデータがない場合のみ前年度繰越にフォールバックする。
        /// （本メソッドは month != 4 のときだけ呼ばれる。4 月は呼び出し側で前年度繰越を直接取得する）
        /// </remarks>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <returns>前月末残高。年度内に過去データがない場合は前年度繰越（それもなければnull）</returns>
        private async Task<int?> GetPreviousMonthBalanceAsync(string cardIdm, int year, int month)
        {
            var fiscalYearStartYear = FiscalYearHelper.GetFiscalYear(year, month);
            var (cursorYear, cursorMonth) = FiscalYearHelper.GetPreviousMonth(year, month);

            // 年度開始月（4月）まで 1 ヶ月ずつ遡り、利用のあった最後の月の月末残高を返す
            while (true)
            {
                // Issue #784: 残高チェーンに基づいて時系列順を復元
                var ledgers = LedgerOrderHelper.ReorderByBalanceChain(
                    (await _ledgerRepository.GetByMonthAsync(cardIdm, cursorYear, cursorMonth).ConfigureAwait(false))
                        .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()));

                if (ledgers.Count > 0)
                {
                    return ledgers.Last().Balance;
                }

                // 年度開始月（4月）まで遡り切ったら終了
                if (cursorYear == fiscalYearStartYear && cursorMonth == 4)
                {
                    break;
                }

                (cursorYear, cursorMonth) = FiscalYearHelper.GetPreviousMonth(cursorYear, cursorMonth);
            }

            // 年度内に 1 件もデータがない場合は前年度繰越にフォールバック
            var carryover = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, fiscalYearStartYear - 1).ConfigureAwait(false);
            return carryover;
        }
    }
}
