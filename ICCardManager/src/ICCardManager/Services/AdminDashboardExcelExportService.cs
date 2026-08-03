using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClosedXML.Excel;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Security;

namespace ICCardManager.Services
{
    /// <summary>
    /// 管理者ダッシュボードの集計結果を Excel ファイルへ出力するサービス（Issue #1692）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 監査対応や予算要求の資料として、画面の内容をそのまま配布できる形にする。
    /// 作法は <see cref="OperationLogExcelExportService"/> に合わせる（<c>Task.Run</c> での
    /// オフロード、ヘッダーの書式、<c>FreezeRows(1)</c>、式インジェクション対策）。
    /// </para>
    /// <para>
    /// 帳票（物品出納簿）用の <c>ExcelStyleFormatter</c> は流用しない。あちらは A〜L 列固定・
    /// セル結合ありの様式専用で、汎用の一覧に当てると帳票側の変更が波及するため。
    /// </para>
    /// </remarks>
    public class AdminDashboardExcelExportService
    {
        /// <summary>ヘッダー行の背景色（操作ログ出力と同じ青）</summary>
        private static readonly XLColor HeaderBackground = XLColor.FromHtml("#4472C4");

        internal const string OverviewSheetName = "概要";
        internal const string OperationSheetName = "運用状況";
        internal const string UtilizationSheetName = "稼働状況";
        internal const string MonthlyUsageSheetName = "月別利用額";
        internal const string BalanceSheetName = "残高推移";

        private const string NumberFormat = "#,##0";
        private const string PercentFormat = "0.0%";

        /// <summary>
        /// 管理者ダッシュボードの集計結果を Excel ファイルへ出力する。
        /// </summary>
        /// <param name="status">運用状況（必須）</param>
        /// <param name="analytics">利用分析。未取得（分析タブを開いていない）場合は null</param>
        /// <param name="filePath">出力先ファイルパス</param>
        /// <remarks>
        /// <paramref name="analytics"/> が null の場合、分析系の 3 シートは作成しない。
        /// 空のシートを付けると「分析結果が 0 件だった」と誤読されるため。
        /// </remarks>
        public virtual async Task ExportAsync(
            AdminDashboardOperationStatus status, AdminDashboardAnalytics analytics, string filePath)
        {
            if (status == null)
            {
                throw new ArgumentNullException(nameof(status));
            }

            await Task.Run(() =>
            {
                using var workbook = new XLWorkbook();

                WriteOverviewSheet(workbook, status, analytics);
                WriteOperationSheet(workbook, status);

                if (analytics != null)
                {
                    WriteUtilizationSheet(workbook, analytics);
                    WriteMonthlyUsageSheet(workbook, analytics);
                    WriteBalanceSheet(workbook, analytics);
                }

                workbook.SaveAs(filePath);
            }).ConfigureAwait(false);
        }

        #region 概要シート

        private static void WriteOverviewSheet(
            XLWorkbook workbook, AdminDashboardOperationStatus status, AdminDashboardAnalytics analytics)
        {
            var sheet = workbook.Worksheets.Add(OverviewSheetName);

            var row = 1;
            sheet.Cell(row, 1).Value = "項目";
            sheet.Cell(row, 2).Value = "値";
            StyleHeaderRow(sheet, row, 2);
            row++;

            row = WriteOverviewRow(sheet, row, "集計基準日時", DisplayFormatters.FormatDateTime(status.AsOf));
            row = WriteOverviewRow(sheet, row, "対象カード枚数", status.TotalCardCount);
            row = WriteOverviewRow(sheet, row, "貸出中", status.LentCardCount);
            row = WriteOverviewRow(sheet, row,
                $"長期未返却（{status.LongTermUnreturnedThresholdDays}日以上）", status.LongTermUnreturnedCount);
            row = WriteOverviewRow(sheet, row,
                $"残額不足（{status.WarningBalance:N0}円以下）", status.LowBalanceCount);
            row = WriteOverviewRow(sheet, row,
                $"{status.ReportYear}年{status.ReportMonth}月の帳票が未出力", status.ReportNotExportedCount);
            row = WriteOverviewRow(sheet, row, "帳票の出力状況を判定できず", status.ReportStatusUnknownCount);

            if (analytics != null)
            {
                row++;
                row = WriteOverviewRow(sheet, row, "分析期間（開始）", DisplayFormatters.FormatDate(analytics.FromDate));
                row = WriteOverviewRow(sheet, row, "分析期間（終了）", DisplayFormatters.FormatDate(analytics.ToDate));
                row = WriteOverviewRow(sheet, row, "分析期間の日数", analytics.PeriodDayCount);
            }

            row++;
            sheet.Cell(row, 1).Value = "稼働率の定義";
            sheet.Cell(row, 2).Value =
                "期間内に利用実績があった日数 ÷ 期間日数。返却時に貸出記録が削除されるため貸出日数では算出できない。"
                + "利用回数・利用総額・未使用日数と併せて判断すること。";
            sheet.Cell(row, 2).Style.Alignment.WrapText = true;

            sheet.Column(1).Width = 34;
            sheet.Column(2).Width = 60;
            sheet.SheetView.FreezeRows(1);
        }

        private static int WriteOverviewRow(IXLWorksheet sheet, int row, string label, string value)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 2).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(value);
            return row + 1;
        }

        private static int WriteOverviewRow(IXLWorksheet sheet, int row, string label, int value)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 2).Value = value;
            sheet.Cell(row, 2).Style.NumberFormat.Format = NumberFormat;
            return row + 1;
        }

        #endregion

        #region 運用状況シート

        private static void WriteOperationSheet(XLWorkbook workbook, AdminDashboardOperationStatus status)
        {
            var sheet = workbook.Worksheets.Add(OperationSheetName);

            var headers = new[]
            {
                "カード", "貸出状況", "貸出職員", "貸出日時", "経過日数", "長期未返却",
                "残額", "残額不足", "帳票の出力状況", "最終利用日"
            };
            WriteHeaders(sheet, headers);

            var row = 2;
            foreach (var card in status.Cards)
            {
                sheet.Cell(row, 1).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(card.DisplayName);
                sheet.Cell(row, 2).Value = card.IsLent ? "貸出中" : "在庫";
                sheet.Cell(row, 3).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(card.LentStaffName);
                sheet.Cell(row, 4).Value = DisplayFormatters.FormatDateTime(card.LentAt, string.Empty);
                WriteNullableInt(sheet.Cell(row, 5), card.ElapsedLentDays);
                sheet.Cell(row, 6).Value = card.IsLongTermUnreturned ? "○" : string.Empty;
                sheet.Cell(row, 7).Value = card.CurrentBalance;
                sheet.Cell(row, 7).Style.NumberFormat.Format = NumberFormat;
                sheet.Cell(row, 8).Value = card.IsBalanceWarning ? "○" : string.Empty;
                sheet.Cell(row, 9).Value = card.ReportStateText;
                sheet.Cell(row, 10).Value = DisplayFormatters.FormatDate(card.LastUsageDate, string.Empty);
                row++;
            }

            FinishSheet(sheet, headers.Length);
        }

        #endregion

        #region 稼働状況シート

        private static void WriteUtilizationSheet(XLWorkbook workbook, AdminDashboardAnalytics analytics)
        {
            var sheet = workbook.Worksheets.Add(UtilizationSheetName);

            var headers = new[] { "カード", "稼働率", "利用日数", "利用回数", "利用総額", "最終利用日", "未使用日数" };
            WriteHeaders(sheet, headers);

            var row = 2;
            foreach (var item in analytics.Utilizations)
            {
                sheet.Cell(row, 1).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(item.DisplayName);
                sheet.Cell(row, 2).Value = item.UtilizationRate;
                sheet.Cell(row, 2).Style.NumberFormat.Format = PercentFormat;
                sheet.Cell(row, 3).Value = item.UsedDayCount;
                sheet.Cell(row, 4).Value = item.UsageCount;
                sheet.Cell(row, 5).Value = item.TotalExpense;
                sheet.Cell(row, 5).Style.NumberFormat.Format = NumberFormat;
                sheet.Cell(row, 6).Value = DisplayFormatters.FormatDate(item.LastUsageDate, string.Empty);
                WriteNullableInt(sheet.Cell(row, 7), item.UnusedDays);
                row++;
            }

            FinishSheet(sheet, headers.Length);
        }

        #endregion

        #region 月別利用額シート

        private static void WriteMonthlyUsageSheet(XLWorkbook workbook, AdminDashboardAnalytics analytics)
        {
            var sheet = workbook.Worksheets.Add(MonthlyUsageSheetName);

            // 行＝年月、列＝職員。月をまたいだ比較がしやすい向きにする
            sheet.Cell(1, 1).Value = "年月";
            var column = 2;
            foreach (var series in analytics.UsageSeries)
            {
                sheet.Cell(1, column).Value = FormulaInjectionSanitizer.SanitizeOrEmpty(series.Name);
                column++;
            }

            sheet.Cell(1, column).Value = "合計";
            var lastColumn = column;
            StyleHeaderRow(sheet, 1, lastColumn);

            for (var monthIndex = 0; monthIndex < analytics.MonthLabels.Count; monthIndex++)
            {
                var row = monthIndex + 2;
                sheet.Cell(row, 1).Value = analytics.MonthLabels[monthIndex];

                var total = 0;
                for (var seriesIndex = 0; seriesIndex < analytics.UsageSeries.Count; seriesIndex++)
                {
                    var value = ValueAt(analytics.UsageSeries[seriesIndex].MonthlyExpenses, monthIndex);
                    sheet.Cell(row, seriesIndex + 2).Value = value;
                    sheet.Cell(row, seriesIndex + 2).Style.NumberFormat.Format = NumberFormat;
                    total += value;
                }

                sheet.Cell(row, lastColumn).Value = total;
                sheet.Cell(row, lastColumn).Style.NumberFormat.Format = NumberFormat;
            }

            FinishSheet(sheet, lastColumn);
        }

        #endregion

        #region 残高推移シート

        private static void WriteBalanceSheet(XLWorkbook workbook, AdminDashboardAnalytics analytics)
        {
            var sheet = workbook.Worksheets.Add(BalanceSheetName);

            sheet.Cell(1, 1).Value = "年月";
            var lastColumn = 1;
            for (var seriesIndex = 0; seriesIndex < analytics.BalanceSeries.Count; seriesIndex++)
            {
                lastColumn = seriesIndex + 2;
                sheet.Cell(1, lastColumn).Value =
                    FormulaInjectionSanitizer.SanitizeOrEmpty(analytics.BalanceSeries[seriesIndex].DisplayName);
            }

            StyleHeaderRow(sheet, 1, lastColumn);

            for (var monthIndex = 0; monthIndex < analytics.MonthLabels.Count; monthIndex++)
            {
                var row = monthIndex + 2;
                sheet.Cell(row, 1).Value = analytics.MonthLabels[monthIndex];

                for (var seriesIndex = 0; seriesIndex < analytics.BalanceSeries.Count; seriesIndex++)
                {
                    var balances = analytics.BalanceSeries[seriesIndex].MonthlyBalances;
                    var cell = sheet.Cell(row, seriesIndex + 2);

                    // 取引開始前の月は空欄にする。0 を書くと「残高が 0 になった」と誤読される
                    if (monthIndex < balances.Count && balances[monthIndex].HasValue)
                    {
                        cell.Value = balances[monthIndex].Value;
                        cell.Style.NumberFormat.Format = NumberFormat;
                    }
                }
            }

            FinishSheet(sheet, lastColumn);
        }

        #endregion

        #region 共通の書式

        private static void WriteHeaders(IXLWorksheet sheet, string[] headers)
        {
            for (var i = 0; i < headers.Length; i++)
            {
                sheet.Cell(1, i + 1).Value = headers[i];
            }

            StyleHeaderRow(sheet, 1, headers.Length);
        }

        private static void StyleHeaderRow(IXLWorksheet sheet, int row, int columnCount)
        {
            var range = sheet.Range(row, 1, row, Math.Max(1, columnCount));
            range.Style.Font.Bold = true;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Fill.BackgroundColor = HeaderBackground;
        }

        private static void FinishSheet(IXLWorksheet sheet, int columnCount)
        {
            sheet.SheetView.FreezeRows(1);
            sheet.Columns(1, Math.Max(1, columnCount)).AdjustToContents();
        }

        private static void WriteNullableInt(IXLCell cell, int? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            cell.Value = value.Value;
            cell.Style.NumberFormat.Format = NumberFormat;
        }

        private static int ValueAt(IReadOnlyList<int> values, int index)
            => values != null && index < values.Count ? values[index] : 0;

        #endregion
    }
}
