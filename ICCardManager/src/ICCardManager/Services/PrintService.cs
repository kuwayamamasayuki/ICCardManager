using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services;

/// <summary>
/// 帳票印刷用データ
/// </summary>
public record ReportPrintData
{
    /// <summary>カード種別</summary>
    public string CardType { get; init; } = string.Empty;

    /// <summary>管理番号</summary>
    public string CardNumber { get; init; } = string.Empty;

    /// <summary>年</summary>
    public int Year { get; init; }

    /// <summary>月</summary>
    public int Month { get; init; }

    /// <summary>和暦年月</summary>
    public string WarekiYearMonth { get; init; } = string.Empty;

    /// <summary>履歴データ</summary>
    public List<ReportPrintRow> Rows { get; init; } = new();

    /// <summary>月計</summary>
    public ReportPrintTotal MonthlyTotal { get; init; } = new();

    /// <summary>累計（3月のみ）</summary>
    public ReportPrintTotal? CumulativeTotal { get; init; }

    /// <summary>次年度繰越（3月のみ）</summary>
    public int? CarryoverToNextYear { get; init; }
}

/// <summary>
/// 帳票印刷用行データ
/// </summary>
public class ReportPrintRow
{
    /// <summary>日付表示</summary>
    public string DateDisplay { get; init; } = string.Empty;

    /// <summary>摘要</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>受入金額</summary>
    public int? Income { get; init; }

    /// <summary>払出金額</summary>
    public int? Expense { get; init; }

    /// <summary>残額</summary>
    public int? Balance { get; init; }

    /// <summary>利用者</summary>
    public string? StaffName { get; init; }

    /// <summary>備考</summary>
    public string? Note { get; init; }

    /// <summary>太字表示するか</summary>
    public bool IsBold { get; init; }
}

/// <summary>
/// 帳票印刷用合計データ
/// </summary>
public class ReportPrintTotal
{
    /// <summary>ラベル</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>受入合計</summary>
    public int Income { get; init; }

    /// <summary>払出合計</summary>
    public int Expense { get; init; }

    /// <summary>残高</summary>
    public int? Balance { get; init; }
}

/// <summary>
/// 印刷サービス
/// </summary>
public class PrintService
{
    private readonly ICardRepository _cardRepository;
    private readonly ILedgerRepository _ledgerRepository;

    public PrintService(
        ICardRepository cardRepository,
        ILedgerRepository ledgerRepository)
    {
        _cardRepository = cardRepository;
        _ledgerRepository = ledgerRepository;
    }

    /// <summary>
    /// 帳票データを取得
    /// </summary>
    public async Task<ReportPrintData?> GetReportDataAsync(string cardIdm, int year, int month)
    {
        var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
        if (card == null)
        {
            return null;
        }

        var ledgers = (await _ledgerRepository.GetByMonthAsync(cardIdm, year, month))
            .Where(l => l.Summary != SummaryGenerator.GetLendingSummary())
            .OrderBy(l => l.Date)
            .ThenBy(l => l.Id)
            .ToList();

        var targetDate = new DateTime(year, month, 1);
        var warekiYearMonth = WarekiConverter.ToWarekiYearMonth(targetDate);

        var rows = new List<ReportPrintRow>();

        // 4月の場合は前年度繰越を追加
        if (month == 4)
        {
            var carryover = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
            rows.Add(new ReportPrintRow
            {
                DateDisplay = "4/1",
                Summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
                Income = carryover ?? 0,
                Balance = carryover ?? 0,
                IsBold = true
            });
        }

        // 各履歴行
        foreach (var ledger in ledgers)
        {
            rows.Add(new ReportPrintRow
            {
                DateDisplay = $"{ledger.Date.Month}/{ledger.Date.Day}",
                Summary = ledger.Summary,
                Income = ledger.Income > 0 ? ledger.Income : null,
                Expense = ledger.Expense > 0 ? ledger.Expense : null,
                Balance = ledger.Balance,
                StaffName = ledger.StaffName,
                Note = ledger.Note
            });
        }

        var monthlyIncome = ledgers.Sum(l => l.Income);
        var monthlyExpense = ledgers.Sum(l => l.Expense);
        var monthEndBalance = ledgers.LastOrDefault()?.Balance ?? 0;

        var result = new ReportPrintData
        {
            CardType = card.CardType,
            CardNumber = card.CardNumber,
            Year = year,
            Month = month,
            WarekiYearMonth = warekiYearMonth,
            Rows = rows,
            MonthlyTotal = new ReportPrintTotal
            {
                Label = SummaryGenerator.GetMonthlySummary(month),
                Income = monthlyIncome,
                Expense = monthlyExpense,
                Balance = month == 3 ? null : monthEndBalance
            }
        };

        // 3月の場合は累計と次年度繰越を追加
        if (month == 3)
        {
            var fiscalYearStart = new DateTime(year, 4, 1);
            var fiscalYearEnd = new DateTime(year + 1, 3, 31);
            var yearlyLedgers = await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd);

            var yearlyIncome = yearlyLedgers.Sum(l => l.Income);
            var yearlyExpense = yearlyLedgers.Sum(l => l.Expense);

            result = result with
            {
                CumulativeTotal = new ReportPrintTotal
                {
                    Label = SummaryGenerator.GetCumulativeSummary(),
                    Income = yearlyIncome,
                    Expense = yearlyExpense,
                    Balance = monthEndBalance
                },
                CarryoverToNextYear = monthEndBalance
            };
        }

        return result;
    }

    /// <summary>
    /// 複数カードの帳票データを取得して結合したFlowDocumentを生成
    /// </summary>
    /// <param name="cardIdms">カードIDmのリスト</param>
    /// <param name="year">年</param>
    /// <param name="month">月</param>
    /// <returns>結合されたFlowDocument（各カードはページ区切りで分離）</returns>
    public async Task<FlowDocument?> CreateCombinedFlowDocumentAsync(
        IEnumerable<string> cardIdms,
        int year,
        int month)
    {
        var cardIdmList = cardIdms.ToList();
        if (cardIdmList.Count == 0)
        {
            return null;
        }

        // 最初のカードでドキュメントを作成
        var firstData = await GetReportDataAsync(cardIdmList[0], year, month);
        if (firstData == null)
        {
            return null;
        }

        var combinedDoc = CreateFlowDocument(firstData);

        // 2枚目以降のカードを追加
        for (int i = 1; i < cardIdmList.Count; i++)
        {
            var data = await GetReportDataAsync(cardIdmList[i], year, month);
            if (data == null)
            {
                continue;
            }

            // ページ区切りを追加
            var pageBreak = new Paragraph
            {
                BreakPageBefore = true
            };
            combinedDoc.Blocks.Add(pageBreak);

            // タイトル
            var titlePara = new Paragraph(new Run("物品出納簿"))
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };
            combinedDoc.Blocks.Add(titlePara);

            // ヘッダ情報
            var headerTable = CreateHeaderTable(data);
            combinedDoc.Blocks.Add(headerTable);

            // データテーブル
            var dataTable = CreateDataTable(data);
            combinedDoc.Blocks.Add(dataTable);
        }

        return combinedDoc;
    }

    /// <summary>
    /// 1ページあたりの推定データ行数（ヘッダー込みのページ）
    /// </summary>
    private const int RowsPerFirstPage = 18;

    /// <summary>
    /// 2ページ目以降の1ページあたりの推定データ行数
    /// </summary>
    private const int RowsPerSubsequentPage = 20;

    /// <summary>
    /// FlowDocumentを生成
    /// </summary>
    public FlowDocument CreateFlowDocument(ReportPrintData data)
    {
        var doc = new FlowDocument
        {
            PageWidth = 842,  // A4横 (約29.7cm)
            PageHeight = 595, // A4横 (約21cm)
            PagePadding = new Thickness(50),
            ColumnWidth = double.MaxValue,
            FontFamily = new FontFamily("Yu Gothic UI, Meiryo, MS Gothic"),
            FontSize = 11
        };

        // データ行 + 合計行の総数を計算
        var totalRows = data.Rows.Count + 1; // +1 は月計行
        if (data.CumulativeTotal != null) totalRows++;
        if (data.CarryoverToNextYear.HasValue) totalRows++;

        // 1ページに収まる場合は従来通り
        if (totalRows <= RowsPerFirstPage)
        {
            AddPageContent(doc, data, data.Rows, false, false);
        }
        else
        {
            // 複数ページに分割
            var remainingRows = new List<ReportPrintRow>(data.Rows);
            var isFirstPage = true;
            var pageIndex = 0;

            while (remainingRows.Count > 0 || pageIndex == 0)
            {
                var rowsForThisPage = isFirstPage ? RowsPerFirstPage : RowsPerSubsequentPage;

                // 最終ページかどうかを判定（合計行のスペースを考慮）
                var summaryRowCount = 1; // 月計
                if (data.CumulativeTotal != null) summaryRowCount++;
                if (data.CarryoverToNextYear.HasValue) summaryRowCount++;

                var isLastPage = remainingRows.Count <= rowsForThisPage - summaryRowCount;

                // このページに表示する行数を決定
                int takeCount;
                if (isLastPage)
                {
                    takeCount = remainingRows.Count;
                }
                else
                {
                    // 合計行のスペースは不要（最終ページでないため）
                    takeCount = Math.Min(remainingRows.Count, rowsForThisPage);
                }

                var pageRows = remainingRows.Take(takeCount).ToList();
                remainingRows = remainingRows.Skip(takeCount).ToList();

                // ページコンテンツを追加（2ページ目以降はページ区切り付き）
                AddPageContent(doc, data, pageRows, !isFirstPage, !isLastPage);

                isFirstPage = false;
                pageIndex++;

                // 残りの行がない場合でも、最終ページで合計行を追加
                if (remainingRows.Count == 0 && !isLastPage)
                {
                    break;
                }
            }
        }

        return doc;
    }

    /// <summary>
    /// 1ページ分のコンテンツを追加
    /// </summary>
    private void AddPageContent(
        FlowDocument doc,
        ReportPrintData data,
        List<ReportPrintRow> rows,
        bool addPageBreakBefore,
        bool hideSummary)
    {
        // タイトル
        var titlePara = new Paragraph(new Run("物品出納簿"))
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };

        // 2ページ目以降はタイトルの前でページ区切り
        if (addPageBreakBefore)
        {
            titlePara.BreakPageBefore = true;
        }

        doc.Blocks.Add(titlePara);

        // ヘッダ情報
        var headerTable = CreateHeaderTable(data);
        doc.Blocks.Add(headerTable);

        // データテーブル（このページ分のみ）
        var pageData = data with { Rows = rows };
        var dataTable = hideSummary
            ? CreateDataTableWithoutSummary(pageData)
            : CreateDataTable(pageData);
        doc.Blocks.Add(dataTable);
    }

    /// <summary>
    /// ヘッダテーブルを作成
    /// </summary>
    private Table CreateHeaderTable(ReportPrintData data)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 15)
        };

        // 列定義（比例幅を使用して用紙サイズに自動調整）
        // 元の比率: 80:150:80:100:80:150 = 640
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.9, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.25, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.9, GridUnitType.Star) });

        var rowGroup = new TableRowGroup();
        var row = new TableRow();

        row.Cells.Add(CreateHeaderCell("物品分類:", false));
        row.Cells.Add(CreateHeaderCell("雑品（金券類）", true));
        row.Cells.Add(CreateHeaderCell("品名:", false));
        row.Cells.Add(CreateHeaderCell(data.CardType, true));
        row.Cells.Add(CreateHeaderCell("規格:", false));
        row.Cells.Add(CreateHeaderCell(data.CardNumber, true));

        rowGroup.Rows.Add(row);

        var row2 = new TableRow();
        row2.Cells.Add(CreateHeaderCell("年月:", false));
        row2.Cells.Add(CreateHeaderCell(data.WarekiYearMonth, true));
        row2.Cells.Add(CreateHeaderCell("単位:", false));
        row2.Cells.Add(CreateHeaderCell("円", true));
        row2.Cells.Add(CreateHeaderCell("", false));
        row2.Cells.Add(CreateHeaderCell("", false));

        rowGroup.Rows.Add(row2);
        table.RowGroups.Add(rowGroup);

        return table;
    }

    /// <summary>
    /// ヘッダセルを作成
    /// </summary>
    private TableCell CreateHeaderCell(string text, bool isBold)
    {
        var cell = new TableCell(new Paragraph(new Run(text))
        {
            Margin = new Thickness(2)
        });

        if (isBold)
        {
            cell.FontWeight = FontWeights.Bold;
        }

        return cell;
    }

    /// <summary>
    /// データテーブルを作成
    /// </summary>
    private Table CreateDataTable(ReportPrintData data)
    {
        return CreateDataTableInternal(data, includeSummary: true);
    }

    /// <summary>
    /// 合計行を含まないデータテーブルを作成（複数ページの途中ページ用）
    /// </summary>
    private Table CreateDataTableWithoutSummary(ReportPrintData data)
    {
        return CreateDataTableInternal(data, includeSummary: false);
    }

    /// <summary>
    /// データテーブルを作成（内部実装）
    /// </summary>
    private Table CreateDataTableInternal(ReportPrintData data, bool includeSummary)
    {
        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1)
        };

        // 列定義（比例幅を使用して用紙サイズに自動調整）
        // 元の比率: 60:200:80:80:80:80:100 = 680
        // Star比率: 1:3.3:1.3:1.3:1.3:1.3:1.7 ≈ 11.2
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });     // 日付
        table.Columns.Add(new TableColumn { Width = new GridLength(3.3, GridUnitType.Star) });   // 摘要
        table.Columns.Add(new TableColumn { Width = new GridLength(1.3, GridUnitType.Star) });   // 受入
        table.Columns.Add(new TableColumn { Width = new GridLength(1.3, GridUnitType.Star) });   // 払出
        table.Columns.Add(new TableColumn { Width = new GridLength(1.3, GridUnitType.Star) });   // 残高
        table.Columns.Add(new TableColumn { Width = new GridLength(1.3, GridUnitType.Star) });   // 氏名
        table.Columns.Add(new TableColumn { Width = new GridLength(1.7, GridUnitType.Star) });   // 備考

        var rowGroup = new TableRowGroup();

        // ヘッダ行
        var headerRow = new TableRow { Background = Brushes.LightGray };
        headerRow.Cells.Add(CreateDataCell("出納日", true, TextAlignment.Center));
        headerRow.Cells.Add(CreateDataCell("摘要", true, TextAlignment.Center));
        headerRow.Cells.Add(CreateDataCell("受入金額", true, TextAlignment.Center));
        headerRow.Cells.Add(CreateDataCell("払出金額", true, TextAlignment.Center));
        headerRow.Cells.Add(CreateDataCell("残額", true, TextAlignment.Center));
        headerRow.Cells.Add(CreateDataCell("氏名", true, TextAlignment.Center));
        headerRow.Cells.Add(CreateDataCell("備考", true, TextAlignment.Center));
        rowGroup.Rows.Add(headerRow);

        // データ行
        foreach (var row in data.Rows)
        {
            var dataRow = new TableRow();
            dataRow.Cells.Add(CreateDataCell(row.DateDisplay, row.IsBold, TextAlignment.Center));
            dataRow.Cells.Add(CreateDataCell(row.Summary, row.IsBold, TextAlignment.Left));
            dataRow.Cells.Add(CreateDataCell(row.Income?.ToString("N0") ?? "", row.IsBold, TextAlignment.Right));
            dataRow.Cells.Add(CreateDataCell(row.Expense?.ToString("N0") ?? "", row.IsBold, TextAlignment.Right));
            dataRow.Cells.Add(CreateDataCell(row.Balance?.ToString("N0") ?? "", row.IsBold, TextAlignment.Right));
            dataRow.Cells.Add(CreateDataCell(row.StaffName ?? "", row.IsBold, TextAlignment.Left));
            dataRow.Cells.Add(CreateDataCell(row.Note ?? "", row.IsBold, TextAlignment.Left));
            rowGroup.Rows.Add(dataRow);
        }

        // 合計行を含める場合のみ追加
        if (includeSummary)
        {
            // 月計行
            var monthlyRow = new TableRow { Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)) };
            monthlyRow.Cells.Add(CreateDataCell("", true, TextAlignment.Center));
            monthlyRow.Cells.Add(CreateDataCell(data.MonthlyTotal.Label, true, TextAlignment.Left));
            monthlyRow.Cells.Add(CreateDataCell(data.MonthlyTotal.Income > 0 ? data.MonthlyTotal.Income.ToString("N0") : "", true, TextAlignment.Right));
            monthlyRow.Cells.Add(CreateDataCell(data.MonthlyTotal.Expense > 0 ? data.MonthlyTotal.Expense.ToString("N0") : "", true, TextAlignment.Right));
            monthlyRow.Cells.Add(CreateDataCell(data.MonthlyTotal.Balance?.ToString("N0") ?? "", true, TextAlignment.Right));
            monthlyRow.Cells.Add(CreateDataCell("", true, TextAlignment.Left));
            monthlyRow.Cells.Add(CreateDataCell("", true, TextAlignment.Left));
            rowGroup.Rows.Add(monthlyRow);

            // 累計行（3月のみ）
            if (data.CumulativeTotal != null)
            {
                var cumulativeRow = new TableRow { Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)) };
                cumulativeRow.Cells.Add(CreateDataCell("", true, TextAlignment.Center));
                cumulativeRow.Cells.Add(CreateDataCell(data.CumulativeTotal.Label, true, TextAlignment.Left));
                cumulativeRow.Cells.Add(CreateDataCell(data.CumulativeTotal.Income > 0 ? data.CumulativeTotal.Income.ToString("N0") : "", true, TextAlignment.Right));
                cumulativeRow.Cells.Add(CreateDataCell(data.CumulativeTotal.Expense > 0 ? data.CumulativeTotal.Expense.ToString("N0") : "", true, TextAlignment.Right));
                cumulativeRow.Cells.Add(CreateDataCell(data.CumulativeTotal.Balance?.ToString("N0") ?? "", true, TextAlignment.Right));
                cumulativeRow.Cells.Add(CreateDataCell("", true, TextAlignment.Left));
                cumulativeRow.Cells.Add(CreateDataCell("", true, TextAlignment.Left));
                rowGroup.Rows.Add(cumulativeRow);
            }

            // 次年度繰越行（3月のみ）
            if (data.CarryoverToNextYear.HasValue)
            {
                var carryoverRow = new TableRow { Background = new SolidColorBrush(Color.FromRgb(220, 220, 220)) };
                carryoverRow.Cells.Add(CreateDataCell("", true, TextAlignment.Center));
                carryoverRow.Cells.Add(CreateDataCell(SummaryGenerator.GetCarryoverToNextYearSummary(), true, TextAlignment.Left));
                carryoverRow.Cells.Add(CreateDataCell("", true, TextAlignment.Right));
                carryoverRow.Cells.Add(CreateDataCell(data.CarryoverToNextYear.Value.ToString("N0"), true, TextAlignment.Right));
                carryoverRow.Cells.Add(CreateDataCell("0", true, TextAlignment.Right));
                carryoverRow.Cells.Add(CreateDataCell("", true, TextAlignment.Left));
                carryoverRow.Cells.Add(CreateDataCell("", true, TextAlignment.Left));
                rowGroup.Rows.Add(carryoverRow);
            }
        }

        table.RowGroups.Add(rowGroup);
        return table;
    }

    /// <summary>
    /// データセルを作成
    /// </summary>
    private TableCell CreateDataCell(string text, bool isBold, TextAlignment alignment)
    {
        var para = new Paragraph(new Run(text))
        {
            TextAlignment = alignment,
            Margin = new Thickness(4, 2, 4, 2)
        };

        var cell = new TableCell(para)
        {
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(0.5)
        };

        if (isBold)
        {
            cell.FontWeight = FontWeights.Bold;
        }

        return cell;
    }

    /// <summary>
    /// 印刷を実行
    /// </summary>
    public bool Print(FlowDocument document, string documentName)
    {
        var printDialog = new PrintDialog();

        if (printDialog.ShowDialog() == true)
        {
            // ページ設定
            document.PageWidth = printDialog.PrintableAreaWidth;
            document.PageHeight = printDialog.PrintableAreaHeight;

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            printDialog.PrintDocument(paginator, documentName);

            return true;
        }

        return false;
    }

    /// <summary>
    /// 印刷設定付きで印刷を実行
    /// </summary>
    public bool PrintWithSettings(FlowDocument document, string documentName, PageOrientation orientation)
    {
        var printDialog = new PrintDialog();

        // 用紙方向を設定
        printDialog.PrintTicket.PageOrientation = orientation;

        if (printDialog.ShowDialog() == true)
        {
            // ページ設定
            document.PageWidth = printDialog.PrintableAreaWidth;
            document.PageHeight = printDialog.PrintableAreaHeight;

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            printDialog.PrintDocument(paginator, documentName);

            return true;
        }

        return false;
    }
}
