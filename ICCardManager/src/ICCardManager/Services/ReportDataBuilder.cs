#nullable enable
using System;
using System.Collections.Generic;
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
        public async Task<MonthlyReportData?> BuildAsync(string cardIdm, int year, int month)
        {
            // カード情報を取得
            var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);
            if (card == null)
            {
                return null;
            }

            var fiscalYearStartYear = FiscalYearHelper.GetFiscalYear(year, month);

            // 年度で最も早い日付の行（貸出中レコードを除く）。導入行の判定に使う
            var fiscalYearFirstDayLedgers = (await _ledgerRepository
                .GetFiscalYearFirstDayLedgersAsync(cardIdm, fiscalYearStartYear).ConfigureAwait(false))
                .ToList();

            // Issue #2418: 3 月を繰越月にして登録したカードは、年度の初日に導入行「前年度より繰越」
            // （受入＝残高）が DB に実在し、年度の始まりの残高はその行が担う。帳票が前年度繰越を合成・加算すると
            // 導入行の受入と二重になるため、年度の台帳が導入行から始まるなら「前年度繰越は無い」として扱う。
            // 「導入行より前に台帳が無いから前年度繰越は null になる」には頼らない — 3 月中の貸出中レコード・
            // 履歴の「追加」・CSV の取り込みで、導入行より前の日付の行はあり得る。
            // 判定は紙の累計（#2326）・履歴の繰越行・残高の整合性チェックと同じ PaperCarryoverPolicy 1 か所（#1763）で、
            // 入力も同じ「年度全体で最も早い日の行」（帳票の月で範囲が変わると、導入行を年度の途中へ動かしたときに
            // 帳票と履歴で判定が食い違う）。
            var startsWithCarryoverRecord =
                PaperCarryoverPolicy.HasFiscalYearStartCarryoverRecord(fiscalYearFirstDayLedgers);

            // 年度開始前の最終残高（＝前年度繰越）。
            // 年度累計の残高チェーン並替のシードと、累計受入への加算（Issue #1494）の両方で使う。
            // GetCarryoverBalanceAsync は貸出中レコードを母集団から除く（#2418。上の判定が摘要の比較で
            // 導入行を認識できない場合（#2371）でも、3 月中の貸出中レコードだけでは前年度繰越が生じない）。
            var fiscalYearPrecedingBalance = startsWithCarryoverRecord
                ? null
                : await _ledgerRepository
                    .GetCarryoverBalanceAsync(cardIdm, fiscalYearStartYear - 1).ConfigureAwait(false);

            // 前月末残高を取得（繰越行表示および残高チェーン並替に使用）。
            // Issue #2043: 「当月 1 日より前の最終残高」はリポジトリの確定済み単票クエリで取る。
            // 4月は年度境界と一致するため前年度繰越（上）を使う。
            //
            // 【母集団について】business-logic.md「シードの母集団は本体クエリと揃える」（#1770）の
            // 例外にあたる。5 月以降の GetLatestBeforeDateAsync は貸出中レコード（is_lent_record = 1）を
            // 含むが、シード先の ledgers / yearlyLedgers は IsLentRecord で除外している。これが安全なのは
            // 貸出中プレースホルダが Income = Expense = 0 かつ Balance = 貸出時の残高（＝直前の
            // 実績行の残額）であり、残高チェーン上は自己ループで、返却されるまで後続の利用行が
            // 挿入されないため。つまり「直前に実績行があれば、貸出中行を含めても含めなくても最終残高は同じ」。
            // 直前に実績行が無い（貸出中行しか無い）形では「残高があるか」自体が変わるが、それが起こり得るのは
            // 台帳の先頭（3 月に登録して貸し出したカードの導入行より前）だけで、その形は導入行の判定
            // （PeriodOpensWithFiscalYearStartCarryover。当月が導入行以前なら前の残高を使わない）と、
            // 貸出中レコードを除いた前年度繰越（GetCarryoverBalanceAsync、#2418）で塞いでいる。
            // 回帰は ReportDataBuilderTests の
            // BuildAsync_PreviousMonthHasLentRecord_PrecedingBalanceIsUnchanged が固定する。
            int? precedingBalance;
            if (PaperCarryoverPolicy.PeriodOpensWithFiscalYearStartCarryover(
                new DateTime(year, month, 1), fiscalYearFirstDayLedgers))
            {
                precedingBalance = null;
            }
            else if (month == 4)
            {
                precedingBalance = fiscalYearPrecedingBalance;
            }
            else
            {
                precedingBalance = (await _ledgerRepository
                    .GetLatestBeforeDateAsync(cardIdm, new DateTime(year, month, 1)).ConfigureAwait(false))?.Balance;
            }

            // Issue #784: 残高チェーンに基づいて同一日内の時系列順を復元
            var ledgers = LedgerOrderHelper.ReorderByBalanceChain(
                ExcludeLentRecords(await _ledgerRepository.GetByMonthAsync(cardIdm, year, month).ConfigureAwait(false)),
                precedingBalance);

            // 繰越行データを生成
            CarryoverRowData? carryover = null;
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
            // 紙出納簿移行カード（Issue #510）の「○月から繰越」レコードは、#1219 以前に登録したカードや
            // CSV で取り込んだ行では DB に Income=残高 で残り得るため、月計から除外しないと前月の累計と二重計上になる。
            // 現行の登録では受入 0 で保存する（#1219）ため、除外しても Sum は変わらない。
            var monthlyIncome = ledgers
                .Where(l => !SummaryGenerator.IsMidYearCarryoverSummary(l.Summary))
                .Sum(l => l.Income);
            var monthlyExpense = ledgers.Sum(l => l.Expense);

            // 累計データを計算（4月の月計残額表示にも使用）
            var fiscalYearStart = FiscalYearHelper.GetFiscalYearStart(fiscalYearStartYear);
            var fiscalYearEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            // Issue #784: 残高チェーンに基づいて時系列順を復元
            // Issue #2043: 年度開始前の最終残高をチェーン開始点のシードとして渡す。
            // シード無しでは、年度先頭の稼働日が Issue #1004 形状（同額のポイント還元と利用で
            // 残高が循環する日）だと開始点を当日の行だけからは決められず id 順フォールバックへ落ちる。
            // 当月の明細（上）はシード付きなので、同じシート上で累計残額と最終データ行の残額が食い違う。
            var yearlyLedgers = LedgerOrderHelper.ReorderByBalanceChain(
                ExcludeLentRecords(await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd).ConfigureAwait(false)),
                fiscalYearPrecedingBalance);

            // Issue #1494: 年度開始前の残高があるときの「前年度より繰越」は DB に保存されず
            // CarryoverRowData として表示用に合成されるため、月計・累計の集計に明示的に加算する必要がある。
            // 3 月を繰越月にして登録したカードの「前年度より繰越」は DB に実在する導入行（4/1 付け・受入=残高）で、
            // 導入行の受入は通常の ledger 行として yearlyLedgers の Sum に入る。このとき
            // fiscalYearPrecedingBalance は null にしてある（上の startsWithCarryoverRecord、#2418）ので加算は 0。
            var fiscalYearCarryoverIncome = fiscalYearPrecedingBalance ?? 0;

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
            // precedingBalance は 4月なら前年度繰越、5月以降は「当月 1 日より前の最終残高」
            // （年度内が空なら年度開始前の最終残高＝同じ前年度繰越になる）。前年度繰越も無い
            // 新規カードでは null になり、その場合 fiscalYearCarryoverIncome が 0 を与える。
            var currentBalance = yearlyLedgers.LastOrDefault()?.Balance
                ?? precedingBalance
                ?? fiscalYearCarryoverIncome;

            // Issue #1215: 紙の出納簿から年度途中で移行した場合、
            // 該当年度の累計には紙の出納簿時代の累計を加算する。
            // 4月は cumulativeTotal=null（Issue #813）かつ月計には紙時代累計を含めない設計のため
            // 加算は5月以降のみで意味を持つ。意図を明確化するため month != 4 ガードを併記。
            // Issue #2326: 3 月繰越（導入行が「前年度より繰越」）のカードの紙の累計は旧年度のものなので加算しない。
            // #2326 より前に累計を入力して登録したカードにも DB を書き換えずに効かせるため、保存済みの台帳から判定する
            // （判定は PaperCarryoverPolicy 1 か所。登録時に保存しない判定と同じクラス）。
            if (month != 4
                && PaperCarryoverPolicy.AddsPaperCumulativeToFiscalYear(card, fiscalYearStartYear, fiscalYearFirstDayLedgers))
            {
                yearlyIncome += card.CarryoverIncomeTotal;
                yearlyExpense += card.CarryoverExpenseTotal;
            }

            // 月計・累計の組み立て
            ReportTotalData monthlyTotal;
            ReportTotalData? cumulativeTotal;

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
        /// 帳票の母集団から貸出中プレースホルダ（<c>is_lent_record = 1</c>）を除く。
        /// </summary>
        /// <remarks>
        /// Issue #2044: 以前は摘要が<b>現在の設定値</b>の「（貸出中）」と一致するかで除外していた。
        /// 組織設定 <c>SummaryText.LendingSummary</c> を貸出中に変更すると、既存の貸出中レコードは
        /// 旧文言のまま残るため除外をすり抜け、物品出納簿に「0円／0円」の行が出力されていた。
        /// 保存済みの行が貸出中かどうかは、行自身が持つフラグで判定する
        /// （<c>business-logic.md</c>「ledger を集計するときの前提」／<c>ReportPreflightChecker</c> と同じ母集団）。
        /// 逆に、摘要がたまたま同じ文言の通常行は除外しない。
        /// </remarks>
        private static IEnumerable<Ledger> ExcludeLentRecords(
            IEnumerable<Ledger> ledgers)
            => ledgers.Where(l => !l.IsLentRecord);
    }
}
