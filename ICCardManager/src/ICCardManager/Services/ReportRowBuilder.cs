using ICCardManager.Common;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// MonthlyReportData から帳票行データ（ReportRowSet）を構築する
    /// </summary>
    /// <remarks>
    /// Issue #1023: PrintService（プレビュー）と ReportService（Excel出力）で
    /// 重複していた MonthlyReportData → 行データの変換ロジックを共通化。
    ///
    /// 変換ルール:
    /// - 日付: WarekiConverter.ToWareki() で和暦に変換
    /// - 受入金額/払出金額: 0 の場合は null（空欄表示）
    /// - 繰越行: 4月は受入金額あり（前年度繰越）、他の月は受入金額なし
    /// - 月計/累計の受入・払出: 0 でも表示（空欄にしない）
    /// </remarks>
    public static class ReportRowBuilder
    {
        /// <summary>
        /// MonthlyReportData から帳票行データ一式を構築
        /// </summary>
        /// <param name="data">月次帳票データ（ReportDataBuilder が生成）</param>
        /// <returns>帳票行データ一式</returns>
        public static ReportRowSet Build(MonthlyReportData data)
        {
            var result = new ReportRowSet();

            // 繰越行
            if (data.Carryover != null)
            {
                result.DataRows.Add(new ReportRow
                {
                    DateDisplay = WarekiConverter.ToWareki(data.Carryover.Date),
                    Summary = data.Carryover.Summary,
                    Income = data.Carryover.Income,
                    Balance = data.Carryover.Balance,
                    IsBold = true,
                    RowType = ReportRowType.Carryover
                });
            }

            // 各履歴行
            foreach (var ledger in data.Ledgers)
            {
                // 年度途中の「○月から繰越」行は残高引継ぎのみなので受入欄は空欄にする
                // （既存データで受入に値が残っていても表示しないための防御的処理）
                var isMidYearCarryoverRow = SummaryGenerator.IsMidYearCarryoverSummary(ledger.Summary);
                result.DataRows.Add(new ReportRow
                {
                    DateDisplay = WarekiConverter.ToWareki(ledger.Date),
                    Summary = ledger.Summary,
                    Income = (!isMidYearCarryoverRow && ledger.Income > 0) ? (int?)ledger.Income : null,
                    Expense = ledger.Expense > 0 ? ledger.Expense : null,
                    Balance = ledger.Balance,
                    StaffName = ledger.StaffName,
                    Note = ledger.Note,
                    RowType = ReportRowType.Data
                });
            }

            // 月計
            result.MonthlyTotal = new ReportTotal
            {
                Label = data.MonthlyTotal.Label,
                Income = data.MonthlyTotal.Income,
                Expense = data.MonthlyTotal.Expense,
                Balance = data.MonthlyTotal.Balance
            };

            // 累計
            if (data.CumulativeTotal != null)
            {
                result.CumulativeTotal = new ReportTotal
                {
                    Label = data.CumulativeTotal.Label,
                    Income = data.CumulativeTotal.Income,
                    Expense = data.CumulativeTotal.Expense,
                    Balance = data.CumulativeTotal.Balance
                };
            }

            // 次年度繰越
            result.CarryoverToNextYear = data.CarryoverToNextYear;

            return result;
        }
    }
}
