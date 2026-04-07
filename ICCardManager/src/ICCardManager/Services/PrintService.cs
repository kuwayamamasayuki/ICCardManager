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
using ICCardManager.Models;
using Microsoft.Extensions.Options;
// Issue #1023: ReportRow, ReportTotal は Models/ReportRow.cs で定義

namespace ICCardManager.Services
{
/// <summary>
    /// 帳票印刷用データ（プレビュー画面で使用）
    /// </summary>
    /// <remarks>
    /// Issue #1023: 行データ・合計データは共通モデル（ReportRow/ReportTotal）を使用。
    /// カード情報・和暦年月などプレビュー固有の情報はこのクラスに保持。
    /// </remarks>
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
        public List<ReportRow> Rows { get; set; } = new();

        /// <summary>月計</summary>
        public ReportTotal MonthlyTotal { get; set; } = new();

        /// <summary>累計</summary>
        public ReportTotal CumulativeTotal { get; set; }

        /// <summary>次年度繰越（3月のみ）</summary>
        public int? CarryoverToNextYear { get; set; }
    }

    /// <summary>
    /// 印刷サービス
    /// </summary>
    public class PrintService
    {
        private readonly IReportDataBuilder _reportDataBuilder;
        private readonly OrganizationOptions _orgOptions;

        public PrintService(
            IReportDataBuilder reportDataBuilder,
            IOptions<OrganizationOptions> orgOptions = null)
        {
            _reportDataBuilder = reportDataBuilder;
            _orgOptions = orgOptions?.Value ?? new OrganizationOptions();
        }

        /// <summary>
        /// 帳票データを取得
        /// </summary>
        public async Task<ReportPrintData?> GetReportDataAsync(string cardIdm, int year, int month)
        {
            // Issue #841: データ準備を共通化されたReportDataBuilderに委譲
            var data = await _reportDataBuilder.BuildAsync(cardIdm, year, month);
            if (data == null)
            {
                return null;
            }

            var targetDate = new DateTime(year, month, 1);
            var warekiYearMonth = WarekiConverter.ToWarekiYearMonth(targetDate);

            // Issue #1023: MonthlyReportData → 行データの変換を ReportRowBuilder に委譲
            var rowSet = ReportRowBuilder.Build(data);

            var result = new ReportPrintData
            {
                CardType = data.Card.CardType,
                CardNumber = data.Card.CardNumber,
                Year = year,
                Month = month,
                WarekiYearMonth = warekiYearMonth,
                Rows = rowSet.DataRows,
                MonthlyTotal = rowSet.MonthlyTotal,
                CumulativeTotal = rowSet.CumulativeTotal,
                CarryoverToNextYear = rowSet.CarryoverToNextYear
            };

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
                var titlePara = new Paragraph(new Run(_orgOptions.ReportLayout.TitleText))
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
        /// <remarks>
        /// 純粋関数（インスタンス状態に依存しない）。テスト容易性のため internal static として公開。
        /// </remarks>
        internal static double GetHeaderTotalHeight()
        {
            return TitleHeight + CardInfoTableHeight + ColumnHeaderHeight + TableBorderHeight;
        }

        /// <summary>
        /// データ行の高さを取得（摘要欄の文字数に基づく）
        /// </summary>
        /// <remarks>
        /// 純粋関数。摘要が空または1行に収まる場合は <see cref="DataRowHeight"/>、
        /// 折り返す場合は <see cref="DataRowHeightDouble"/> を返す。
        /// </remarks>
        internal static double GetDataRowHeight(ReportRow row, bool isLandscape)
        {
            if (string.IsNullOrEmpty(row.Summary))
                return DataRowHeight;

            var maxChars = isLandscape ? SummaryCharsLandscape : SummaryCharsPortrait;
            return (row.Summary?.Length ?? 0) <= maxChars ? DataRowHeight : DataRowHeightDouble;
        }

        /// <summary>
        /// データ領域の利用可能な高さを計算
        /// </summary>
        /// <remarks>純粋関数。</remarks>
        internal static double GetAvailableDataHeight(double pageHeight)
        {
            // ページ高さ - 上下余白 - ヘッダー部分
            return pageHeight - (PagePaddingSize * 2) - GetHeaderTotalHeight();
        }

        /// <summary>
        /// 行をページごとにグループ化（高さを積み上げて改ページ位置を決定）
        /// </summary>
        /// <remarks>
        /// 純粋関数。ページ寸法・行データ・合計行数を入力として、改ページ位置を確定する。
        /// インスタンス状態に依存しない。
        /// </remarks>
        internal static List<List<ReportRow>> GroupRowsByPage(
            List<ReportRow> rows,
            double pageWidth,
            double pageHeight,
            int summaryRowCount,
            bool isFirstCard)
        {
            var isLandscape = pageWidth > pageHeight;
            var availableHeight = GetAvailableDataHeight(pageHeight);
            var summaryTotalHeight = summaryRowCount * SummaryRowHeight;

            var pages = new List<List<ReportRow>>();
            var currentPage = new List<ReportRow>();
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
                    currentPage = new List<ReportRow>();
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
            List<ReportRow> rows,
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
            var titlePara = new Paragraph(new Run(_orgOptions.ReportLayout.TitleText))
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

            // 設計書に合わせて1行に収める
            // 物品の分類: 雑品（金券類）  品名: はやかけん  規格: H001  単位: 円
            row.Cells.Add(CreateHeaderCell("物品分類:", false));
            row.Cells.Add(CreateHeaderCell(_orgOptions.ReportLayout.ClassificationText, true));
            row.Cells.Add(CreateHeaderCell("品名:", false));
            row.Cells.Add(CreateHeaderCell(data.CardType, true));
            row.Cells.Add(CreateHeaderCell("規格:", false));
            row.Cells.Add(CreateHeaderCell($"{data.CardNumber}  単位: {_orgOptions.ReportLayout.UnitText}", true));

            rowGroup.Rows.Add(row);
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
        /// データテーブルの行種別（背景色の判定に使用）
        /// </summary>
        internal enum PrintRowKind
        {
            Header,
            Data,
            MonthlyTotal,
            CumulativeTotal,
            CarryoverToNextYear,
        }

        /// <summary>
        /// 帳票テーブルの1行を表す純粋データ（UI 知識を持たない）。
        /// </summary>
        /// <remarks>
        /// <see cref="BuildPrintTableRows"/> が出力し、<see cref="CreateDataTableInternal"/>
        /// が WPF <see cref="TableRow"/> に変換する。
        /// </remarks>
        internal sealed class PrintTableRowDescriptor
        {
            /// <summary>7列のセル文字列（出納日, 摘要, 受入金額, 払出金額, 残額, 氏名, 備考）</summary>
            public IReadOnlyList<string> Cells { get; }

            /// <summary>太字フラグ（合計行・繰越行・データ行で個別指定可）</summary>
            public bool IsBold { get; }

            /// <summary>行種別</summary>
            public PrintRowKind Kind { get; }

            public PrintTableRowDescriptor(IReadOnlyList<string> cells, bool isBold, PrintRowKind kind)
            {
                Cells = cells;
                IsBold = isBold;
                Kind = kind;
            }
        }

        /// <summary>
        /// 帳票テーブルの行データを純粋関数として組み立てる。
        /// </summary>
        /// <remarks>
        /// 副作用なし、WPF依存なし。<see cref="CreateDataTableInternal"/> から呼ばれ、
        /// 各 descriptor は1対1で <see cref="TableRow"/> に変換される。
        /// 出力順: ヘッダ → データ行 → (includeSummary時) 月計 → (累計あれば) 累計 → (繰越あれば) 次年度繰越
        /// </remarks>
        internal static IReadOnlyList<PrintTableRowDescriptor> BuildPrintTableRows(
            ReportPrintData data, bool includeSummary)
        {
            var rows = new List<PrintTableRowDescriptor>();

            // ヘッダ行
            rows.Add(new PrintTableRowDescriptor(
                new[] { "出納日", "摘要", "受入金額", "払出金額", "残額", "氏名", "備考" },
                isBold: true,
                kind: PrintRowKind.Header));

            // データ行
            foreach (var row in data.Rows)
            {
                rows.Add(new PrintTableRowDescriptor(
                    new[]
                    {
                        row.DateDisplay,
                        row.Summary,
                        row.Income?.ToString("N0") ?? "",
                        row.Expense?.ToString("N0") ?? "",
                        row.Balance?.ToString("N0") ?? "",
                        row.StaffName ?? "",
                        row.Note ?? "",
                    },
                    isBold: row.IsBold,
                    kind: PrintRowKind.Data));
            }

            if (!includeSummary)
            {
                return rows;
            }

            // 月計行（Issue #842: 0 も表示）
            rows.Add(new PrintTableRowDescriptor(
                new[]
                {
                    "",
                    data.MonthlyTotal.Label,
                    data.MonthlyTotal.Income.ToString("N0"),
                    data.MonthlyTotal.Expense.ToString("N0"),
                    data.MonthlyTotal.Balance?.ToString("N0") ?? "",
                    "",
                    "",
                },
                isBold: true,
                kind: PrintRowKind.MonthlyTotal));

            // 累計行
            if (data.CumulativeTotal != null)
            {
                rows.Add(new PrintTableRowDescriptor(
                    new[]
                    {
                        "",
                        data.CumulativeTotal.Label,
                        data.CumulativeTotal.Income.ToString("N0"),
                        data.CumulativeTotal.Expense.ToString("N0"),
                        data.CumulativeTotal.Balance?.ToString("N0") ?? "",
                        "",
                        "",
                    },
                    isBold: true,
                    kind: PrintRowKind.CumulativeTotal));
            }

            // 次年度繰越行（3月のみ）
            if (data.CarryoverToNextYear.HasValue)
            {
                rows.Add(new PrintTableRowDescriptor(
                    new[]
                    {
                        "",
                        SummaryGenerator.GetCarryoverToNextYearSummary(),
                        "",
                        data.CarryoverToNextYear.Value.ToString("N0"),
                        "0",
                        "",
                        "",
                    },
                    isBold: true,
                    kind: PrintRowKind.CarryoverToNextYear));
            }

            return rows;
        }

        // 列ごとのテキスト整列（純粋関数からは UI 知識を排除しているため WPF 側に保持）
        private static readonly TextAlignment[] CellAlignments =
        {
            TextAlignment.Center, // 出納日
            TextAlignment.Left,   // 摘要
            TextAlignment.Right,  // 受入金額
            TextAlignment.Right,  // 払出金額
            TextAlignment.Right,  // 残額
            TextAlignment.Left,   // 氏名
            TextAlignment.Left,   // 備考
        };

        /// <summary>
        /// 行種別から WPF 背景ブラシを取得
        /// </summary>
        private static Brush GetRowBackground(PrintRowKind kind)
        {
            switch (kind)
            {
                case PrintRowKind.Header:
                    return Brushes.LightGray;
                case PrintRowKind.MonthlyTotal:
                    return new SolidColorBrush(Color.FromRgb(240, 240, 240));
                case PrintRowKind.CumulativeTotal:
                    return new SolidColorBrush(Color.FromRgb(230, 230, 230));
                case PrintRowKind.CarryoverToNextYear:
                    return new SolidColorBrush(Color.FromRgb(220, 220, 220));
                case PrintRowKind.Data:
                default:
                    return null;
            }
        }

        /// <summary>
        /// データテーブルを作成（内部実装）
        /// </summary>
        /// <remarks>
        /// 純粋データ構築は <see cref="BuildPrintTableRows"/> に委譲し、
        /// このメソッドは WPF <see cref="Table"/> オブジェクトの組み立てに専念する。
        /// </remarks>
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

            foreach (var descriptor in BuildPrintTableRows(data, includeSummary))
            {
                var tableRow = new TableRow();
                var background = GetRowBackground(descriptor.Kind);
                if (background != null)
                {
                    tableRow.Background = background;
                }

                for (int i = 0; i < descriptor.Cells.Count; i++)
                {
                    tableRow.Cells.Add(CreateDataCell(descriptor.Cells[i], descriptor.IsBold, CellAlignments[i]));
                }

                rowGroup.Rows.Add(tableRow);
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
