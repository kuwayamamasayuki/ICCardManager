using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
/// <summary>
    /// 帳票印刷用データ
    /// </summary>
    public class ReportPrintData
    {
        /// <summary>カード種別</summary>
        public string CardType { get; set; } = string.Empty;

        /// <summary>管理番号</summary>
        public string CardNumber { get; set; } = string.Empty;

        /// <summary>年</summary>
        public int Year { get; set; }

        /// <summary>月</summary>
        public int Month { get; set; }

        /// <summary>和暦年月</summary>
        public string WarekiYearMonth { get; set; } = string.Empty;

        /// <summary>履歴データ</summary>
        public List<ReportPrintRow> Rows { get; set; } = new();

        /// <summary>月計</summary>
        public ReportPrintTotal MonthlyTotal { get; set; } = new();

        /// <summary>累計（3月のみ）</summary>
        public ReportPrintTotal? CumulativeTotal { get; set; }

        /// <summary>次年度繰越（3月のみ）</summary>
        public int? CarryoverToNextYear { get; set; }
    }

    /// <summary>
    /// 帳票印刷用行データ
    /// </summary>
    public class ReportPrintRow
    {
        /// <summary>日付表示</summary>
        public string DateDisplay { get; set; } = string.Empty;

        /// <summary>摘要</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>受入金額</summary>
        public int? Income { get; set; }

        /// <summary>払出金額</summary>
        public int? Expense { get; set; }

        /// <summary>残額</summary>
        public int? Balance { get; set; }

        /// <summary>利用者</summary>
        public string StaffName { get; set; }

        /// <summary>備考</summary>
        public string Note { get; set; }

        /// <summary>太字表示するか</summary>
        public bool IsBold { get; set; }
    }

    /// <summary>
    /// 帳票印刷用合計データ
    /// </summary>
    public class ReportPrintTotal
    {
        /// <summary>ラベル</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>受入合計</summary>
        public int Income { get; set; }

        /// <summary>払出合計</summary>
        public int Expense { get; set; }

        /// <summary>残高</summary>
        public int? Balance { get; set; }
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

                // .NET Framework 4.8ではclassにwith式が使えないためプロパティを直接代入
                result.CumulativeTotal = new ReportPrintTotal
                {
                    Label = SummaryGenerator.GetCumulativeSummary(),
                    Income = yearlyIncome,
                    Expense = yearlyExpense,
                    Balance = monthEndBalance
                };
                result.CarryoverToNextYear = monthEndBalance;
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
        public async Task<FlowDocument> CreateCombinedFlowDocumentAsync(
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
        /// 複数カードの帳票データからFlowDocumentを生成（用紙方向指定）
        /// </summary>
        /// <param name="dataList">帳票データのリスト</param>
        /// <param name="orientation">用紙方向</param>
        /// <returns>結合されたFlowDocument</returns>
        public FlowDocument CreateFlowDocumentForMultipleCards(
            List<ReportPrintData> dataList,
            PageOrientation orientation)
        {
            if (dataList.Count == 0)
            {
                return new FlowDocument();
            }

            // 最初のカードでドキュメントを作成
            var combinedDoc = CreateFlowDocument(dataList[0], orientation);

            // 用紙サイズを取得
            double pageWidth = orientation == PageOrientation.Landscape ? 842 : 595;
            double pageHeight = orientation == PageOrientation.Landscape ? 595 : 842;

            // 2枚目以降のカードを追加
            for (int i = 1; i < dataList.Count; i++)
            {
                var data = dataList[i];

                // 合計行の数を計算
                var summaryRowCount = 1;
                if (data.CumulativeTotal != null) summaryRowCount++;
                if (data.CarryoverToNextYear.HasValue) summaryRowCount++;

                // コンテンツの高さに基づいてページをグループ化
                var pageGroups = GroupRowsByPage(data.Rows, pageWidth, pageHeight, summaryRowCount, false);

                // 1ページに収まる場合
                if (pageGroups.Count <= 1)
                {
                    AddPageContent(combinedDoc, data, data.Rows, true, false);
                }
                else
                {
                    // 複数ページに分割
                    for (int j = 0; j < pageGroups.Count; j++)
                    {
                        var isLastPage = (j == pageGroups.Count - 1);
                        var pageRows = pageGroups[j];

                        // このカードの全ページでページ区切りが必要（2枚目以降のカードなので）
                        AddPageContent(combinedDoc, data, pageRows, true, !isLastPage);
                    }
                }
            }

            return combinedDoc;
        }

        // ページレイアウト定数
        private const double PagePaddingSize = 50;        // ページ余白（上下左右）

        // ヘッダー部分の高さ（実測値ベース、少し余裕を持たせる）
        private const double TitleHeight = 45;            // タイトル「物品出納簿」(FontSize18行高さ24 + Margin20 + 余裕)
        private const double CardInfoTableHeight = 58;    // カード情報テーブル（2行×14pt + パディング + Margin15 + 余裕）
        private const double ColumnHeaderHeight = 25;     // データテーブルの列ヘッダー行（セルパディング含む）
        private const double TableBorderHeight = 2;       // データテーブル上下の罫線

        // データ行の高さ（実測値ベース）
        private const double DataRowHeight = 22;          // 1行データ（FontSize11 + パディング + 罫線）
        private const double DataRowHeightDouble = 38;    // 2行データ（摘要が折り返す場合）

        // 合計行の高さ
        private const double SummaryRowHeight = 22;       // 月計/累計/繰越行

        // 摘要欄の1行あたり文字数（実測値：セル幅÷フォントサイズ）
        private const int SummaryCharsLandscape = 20;     // 横向き時（幅211pt / 11pt）
        private const int SummaryCharsPortrait = 12;      // 縦向き時（幅138pt / 11pt）

        /// <summary>
        /// ヘッダー部分の合計高さを取得（タイトル + カード情報 + 列ヘッダー + テーブル罫線）
        /// </summary>
        private double GetHeaderTotalHeight()
        {
            return TitleHeight + CardInfoTableHeight + ColumnHeaderHeight + TableBorderHeight;
        }

        /// <summary>
        /// データ行の高さを取得（摘要欄の文字数に基づく）
        /// </summary>
        private double GetDataRowHeight(ReportPrintRow row, bool isLandscape)
        {
            if (string.IsNullOrEmpty(row.Summary))
                return DataRowHeight;

            var maxChars = isLandscape ? SummaryCharsLandscape : SummaryCharsPortrait;
            return row.Summary.Length <= maxChars ? DataRowHeight : DataRowHeightDouble;
        }

        /// <summary>
        /// データ領域の利用可能な高さを計算
        /// </summary>
        private double GetAvailableDataHeight(double pageHeight)
        {
            // ページ高さ - 上下余白 - ヘッダー部分
            return pageHeight - (PagePaddingSize * 2) - GetHeaderTotalHeight();
        }

        /// <summary>
        /// 行をページごとにグループ化（高さを積み上げて改ページ位置を決定）
        /// </summary>
        private List<List<ReportPrintRow>> GroupRowsByPage(
            List<ReportPrintRow> rows,
            double pageWidth,
            double pageHeight,
            int summaryRowCount,
            bool isFirstCard)
        {
            var isLandscape = pageWidth > pageHeight;
            var availableHeight = GetAvailableDataHeight(pageHeight);
            var summaryTotalHeight = summaryRowCount * SummaryRowHeight;

            var pages = new List<List<ReportPrintRow>>();
            var currentPage = new List<ReportPrintRow>();
            double accumulatedHeight = 0;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var rowHeight = GetDataRowHeight(row, isLandscape);

                // この行を追加した後の残り行の合計高さを計算
                double remainingRowsHeight = 0;
                for (int j = i + 1; j < rows.Count; j++)
                {
                    remainingRowsHeight += GetDataRowHeight(rows[j], isLandscape);
                }

                // 残り全部を入れても収まるか？（最終ページ判定）
                var canFitAll = accumulatedHeight + rowHeight + remainingRowsHeight + summaryTotalHeight <= availableHeight;

                // 最終ページでない場合は合計行のスペースは不要
                var spaceForSummary = canFitAll ? summaryTotalHeight : 0;

                // この行を追加すると溢れるか？
                if (accumulatedHeight + rowHeight + spaceForSummary > availableHeight && currentPage.Count > 0)
                {
                    // 現在のページを確定して新しいページを開始
                    pages.Add(currentPage);
                    currentPage = new List<ReportPrintRow>();
                    accumulatedHeight = 0;
                }

                currentPage.Add(row);
                accumulatedHeight += rowHeight;
            }

            if (currentPage.Count > 0)
            {
                pages.Add(currentPage);
            }

            return pages;
        }

        /// <summary>
        /// FlowDocumentを生成（横向きデフォルト）
        /// </summary>
        public FlowDocument CreateFlowDocument(ReportPrintData data)
        {
            return CreateFlowDocument(data, PageOrientation.Landscape);
        }

        /// <summary>
        /// FlowDocumentを生成（用紙方向指定）
        /// </summary>
        public FlowDocument CreateFlowDocument(ReportPrintData data, PageOrientation orientation)
        {
            // 用紙方向に応じたページサイズを設定
            double pageWidth, pageHeight;

            if (orientation == PageOrientation.Landscape)
            {
                pageWidth = 842;   // A4横
                pageHeight = 595;
            }
            else
            {
                pageWidth = 595;   // A4縦
                pageHeight = 842;
            }

            var doc = new FlowDocument
            {
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                PagePadding = new Thickness(PagePaddingSize),
                ColumnWidth = double.MaxValue,
                FontFamily = new FontFamily("Yu Gothic UI, Meiryo, MS Gothic"),
                FontSize = 11
            };

            // 合計行の数を計算
            var summaryRowCount = 1; // 月計
            if (data.CumulativeTotal != null) summaryRowCount++;
            if (data.CarryoverToNextYear.HasValue) summaryRowCount++;

            // コンテンツの高さに基づいてページをグループ化
            var pageGroups = GroupRowsByPage(data.Rows, pageWidth, pageHeight, summaryRowCount, true);

            // 1ページに収まる場合
            if (pageGroups.Count <= 1)
            {
                AddPageContent(doc, data, data.Rows, false, false);
            }
            else
            {
                // 複数ページに分割
                for (int i = 0; i < pageGroups.Count; i++)
                {
                    var isFirstPage = (i == 0);
                    var isLastPage = (i == pageGroups.Count - 1);
                    var pageRows = pageGroups[i];

                    // ページコンテンツを追加（2ページ目以降はページ区切り付き）
                    AddPageContent(doc, data, pageRows, !isFirstPage, !isLastPage);
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
            // Sectionでページコンテンツを囲む（FlowDocumentの自動分割を制御）
            var section = new Section();

            // 2ページ目以降はセクションの前でページ区切り
            if (addPageBreakBefore)
            {
                section.BreakPageBefore = true;
            }

            // タイトル
            var titlePara = new Paragraph(new Run("物品出納簿"))
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };
            section.Blocks.Add(titlePara);

            // ヘッダ情報
            var headerTable = CreateHeaderTable(data);
            section.Blocks.Add(headerTable);

            // データテーブル（このページ分のみ）
            // .NET Framework 4.8ではclassにwith式が使えないため手動でコピー
            var pageData = new ReportPrintData
            {
                CardType = data.CardType,
                CardNumber = data.CardNumber,
                Year = data.Year,
                Month = data.Month,
                WarekiYearMonth = data.WarekiYearMonth,
                Rows = rows,
                MonthlyTotal = data.MonthlyTotal,
                CumulativeTotal = data.CumulativeTotal,
                CarryoverToNextYear = data.CarryoverToNextYear
            };
            var dataTable = hideSummary
                ? CreateDataTableWithoutSummary(pageData)
                : CreateDataTable(pageData);
            section.Blocks.Add(dataTable);

            doc.Blocks.Add(section);
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
}
