using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using ClosedXML.Excel;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Microsoft.Extensions.Options;

namespace ICCardManager.Services
{
/// <summary>
    /// 帳票作成結果
    /// </summary>
    public class ReportGenerationResult
    {
        /// <summary>
        /// 成功フラグ
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// スキップフラグ（新規購入より前の月など、作成対象外の場合）
        /// </summary>
        public bool Skipped { get; set; }

        /// <summary>
        /// エラーメッセージ（失敗時）
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 詳細エラーメッセージ（失敗時）
        /// </summary>
        public string DetailedErrorMessage { get; set; }

        /// <summary>
        /// 出力ファイルパス（成功時）
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// 成功結果を作成
        /// </summary>
        public static ReportGenerationResult SuccessResult(string outputPath) => new()
        {
            Success = true,
            OutputPath = outputPath
        };

        /// <summary>
        /// 失敗結果を作成
        /// </summary>
        public static ReportGenerationResult FailureResult(string message, string detailedMessage = null) => new()
        {
            Success = false,
            ErrorMessage = message,
            DetailedErrorMessage = detailedMessage
        };

        /// <summary>
        /// スキップ結果を作成（新規購入より前の月など）
        /// </summary>
        public static ReportGenerationResult SkippedResult(string reason) => new()
        {
            Success = true, // エラーではないのでSuccessはtrue
            Skipped = true,
            ErrorMessage = reason
        };
    }

    /// <summary>
    /// 一括帳票作成結果
    /// </summary>
    public class BatchReportGenerationResult
    {
        /// <summary>
        /// 個別の作成結果
        /// </summary>
        public IReadOnlyList<(string CardIdm, string CardName, ReportGenerationResult Result)> Results { get; }

        /// <summary>
        /// テンプレートエラーメッセージ（テンプレートが見つからない場合）
        /// </summary>
        public string TemplateErrorMessage { get; set; }

        /// <summary>
        /// ディレクトリエラーメッセージ（出力先フォルダの作成に失敗した場合）
        /// </summary>
        public string DirectoryErrorMessage { get; set; }

        /// <summary>
        /// テンプレートが見つからなかった
        /// </summary>
        public bool IsTemplateError => TemplateErrorMessage != null;

        /// <summary>
        /// ディレクトリ作成エラーがあった
        /// </summary>
        public bool IsDirectoryError => DirectoryErrorMessage != null;

        /// <summary>
        /// 成功した件数（スキップを除く）
        /// </summary>
        public int SuccessCount => Results.Count(r => r.Result.Success && !r.Result.Skipped);

        /// <summary>
        /// 失敗した件数
        /// </summary>
        public int FailureCount => Results.Count(r => !r.Result.Success);

        /// <summary>
        /// スキップした件数（新規購入より前の月など）
        /// </summary>
        public int SkippedCount => Results.Count(r => r.Result.Skipped);

        /// <summary>
        /// 全件成功したか
        /// </summary>
        public bool AllSuccess => !IsTemplateError && !IsDirectoryError && Results.All(r => r.Result.Success);

        /// <summary>
        /// 成功したファイルパスの一覧
        /// </summary>
        public IReadOnlyList<string> SuccessfulFiles => Results
            .Where(r => r.Result.Success && r.Result.OutputPath != null)
            .Select(r => r.Result.OutputPath!)
            .ToList()
            .AsReadOnly();

        public BatchReportGenerationResult(IEnumerable<(string CardIdm, string CardName, ReportGenerationResult Result)> results)
        {
            Results = results.ToList().AsReadOnly();
        }

        private BatchReportGenerationResult()
        {
            Results = Array.Empty<(string, string, ReportGenerationResult)>();
        }

        /// <summary>
        /// テンプレートが見つからない場合の結果を作成
        /// </summary>
        public static BatchReportGenerationResult TemplateNotFound(string detailedMessage) => new()
        {
            TemplateErrorMessage = detailedMessage
        };

        /// <summary>
        /// 出力先フォルダの作成に失敗した場合の結果を作成
        /// </summary>
        public static BatchReportGenerationResult DirectoryCreationFailed(string detailedMessage) => new()
        {
            DirectoryErrorMessage = detailedMessage
        };

        /// <summary>
        /// 結果サマリーを取得
        /// </summary>
        public string GetSummary()
        {
            if (IsTemplateError)
            {
                return $"テンプレートエラー: {TemplateErrorMessage}";
            }

            if (IsDirectoryError)
            {
                return $"フォルダエラー: {DirectoryErrorMessage}";
            }

            if (AllSuccess)
            {
                return $"{SuccessCount}件の帳票を作成しました。";
            }

            return $"{SuccessCount}件成功、{FailureCount}件失敗しました。";
        }
    }

    /// <summary>
    /// 月次帳票作成サービス
    /// </summary>
    public class ReportService
    {
        private readonly ICardRepository _cardRepository;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly ISettingsRepository _settingsRepository;
        private readonly IReportDataBuilder _reportDataBuilder;
        private readonly OrganizationOptions _orgOptions;

        public ReportService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            ISettingsRepository settingsRepository,
            IReportDataBuilder reportDataBuilder,
            IOptions<OrganizationOptions> orgOptions = null)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _settingsRepository = settingsRepository;
            _reportDataBuilder = reportDataBuilder;
            _orgOptions = orgOptions?.Value ?? new OrganizationOptions();
        }

        /// <summary>
        /// 月次帳票を作成（年度ファイルの該当月シートを作成/更新）
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <param name="outputPath">出力先パス（年度ファイル）</param>
        /// <returns>作成結果（成功/失敗とエラーメッセージ）</returns>
        /// <remarks>
        /// Issue #477対応: 年度ごとに1つのExcelファイルを作成し、月ごとにワークシートを分ける。
        /// 上書き時は当該月のワークシートのみ修正する。
        /// </remarks>
        public async Task<ReportGenerationResult> CreateMonthlyReportAsync(string cardIdm, int year, int month, string outputPath)
        {
            string templatePath = null;

            try
            {
                // テンプレートパスを解決
                try
                {
                    var settings = _settingsRepository.GetAppSettings();
                    templatePath = TemplateResolver.ResolveTemplatePath(settings.DepartmentType);
                }
                catch (TemplateNotFoundException ex)
                {
                    return ReportGenerationResult.FailureResult(
                        "テンプレートファイルが見つかりません",
                        ex.GetDetailedMessage());
                }

                // Issue #501: 新規購入より前の月はスキップ
                var purchaseDate = await _ledgerRepository.GetPurchaseDateAsync(cardIdm);
                if (purchaseDate.HasValue)
                {
                    var requestedMonth = new DateTime(year, month, 1);
                    var purchaseMonth = new DateTime(purchaseDate.Value.Year, purchaseDate.Value.Month, 1);
                    if (requestedMonth < purchaseMonth)
                    {
                        return ReportGenerationResult.SkippedResult(
                            $"新規購入（{purchaseDate.Value:yyyy/MM}）より前の月です");
                    }
                }

                // Issue #841: データ準備を共通化されたReportDataBuilderに委譲
                var data = await _reportDataBuilder.BuildAsync(cardIdm, year, month);
                if (data == null)
                {
                    return ReportGenerationResult.FailureResult(
                        "カード情報が見つかりません",
                        $"指定されたカード（IDm: {cardIdm}）は登録されていません。");
                }

                var card = data.Card;

                // Issue #477: 既存ファイルがあれば開く、なければテンプレートから新規作成
                XLWorkbook workbook;
                bool isExistingFile = File.Exists(outputPath);

                if (isExistingFile)
                {
                    workbook = new XLWorkbook(outputPath);
                }
                else
                {
                    workbook = new XLWorkbook(templatePath);
                }

                using (workbook)
                {
                    // シート名を決定（月名）
                    var sheetName = GetMonthSheetName(month);

                    // Issue #477: 該当月のシートを取得または作成
                    IXLWorksheet worksheet;

                    if (workbook.Worksheets.TryGetWorksheet(sheetName, out worksheet))
                    {
                        // 既存シートがある場合はデータ部分をクリア
                        ClearWorksheetData(worksheet);

                        // Issue #531: 既存シートにもテンプレートのページ設定を適用
                        using var templateWorkbook = new XLWorkbook(templatePath);
                        var templateSheet = templateWorkbook.Worksheets.First();
                        CopyPageSetup(templateSheet, worksheet);

                        // Issue #637: 備考欄（17-22行目）をテンプレートから復元する
                        // ClearWorksheetDataで5行目以降がクリアされるため、備考欄のセルデータも消える
                        CopyNotesSection(templateSheet, worksheet);
                    }
                    else if (isExistingFile)
                    {
                        // 既存ファイルに新しいシートを追加（テンプレートからコピー）
                        using var templateWorkbook = new XLWorkbook(templatePath);
                        var templateSheet = templateWorkbook.Worksheets.First();

                        // テンプレートシートをコピーして追加
                        worksheet = workbook.Worksheets.Add(sheetName);
                        CopyWorksheetFormat(templateSheet, worksheet);
                    }
                    else
                    {
                        // 新規ファイルの場合、テンプレートの最初のシートをリネーム
                        worksheet = workbook.Worksheets.First();
                        worksheet.Name = sheetName;
                    }

                    // シートを月順に並び替え
                    ReorderWorksheetsByMonth(workbook);

                    // Issue #809: 前月シートの最終ページ番号を考慮してページ番号を決定
                    var currentPageNumber = GetStartingPageNumberForMonth(workbook, card, month);

                    // ヘッダ情報を設定（Issue #510: ページ番号も設定）
                    SetHeaderInfo(worksheet, card, currentPageNumber);

                    // Issue #457: ページ設定（印刷時に1-4行目をヘッダーとして各ページに繰り返す）
                    ConfigurePageSetup(worksheet);

                    // Issue #858: 表示倍率を100%に統一
                    // テンプレートの最初のシートを直接使う場合、テンプレートの表示倍率が引き継がれるため
                    worksheet.SheetView.ZoomScale = 100;

                    // Issue #457: データ出力（5～16行に内容を記載、それを超える場合は改ページ）
                    const int DataStartRow = 5;      // データ開始行
                    const int RowsPerPage = 12;      // 1ページあたりの最大データ行数（5～16行目）
                    var currentRow = DataStartRow;
                    var rowsOnCurrentPage = 0;

                    // Issue #1023: MonthlyReportData → 行データの変換を ReportRowBuilder に委譲
                    var rowSet = ReportRowBuilder.Build(data);

                    // 繰越行 + 各履歴行を出力
                    foreach (var row in rowSet.DataRows)
                    {
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                        currentRow = WriteReportRow(worksheet, currentRow, row);
                        rowsOnCurrentPage++;
                    }

                    // 月計行
                    (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                    currentRow = WriteMonthlyTotalRow(worksheet, currentRow,
                        rowSet.MonthlyTotal);
                    rowsOnCurrentPage++;

                    // 累計行
                    if (rowSet.CumulativeTotal != null)
                    {
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                        currentRow = WriteCumulativeRow(worksheet, currentRow,
                            rowSet.CumulativeTotal);
                        rowsOnCurrentPage++;
                    }

                    // 3月の場合は次年度繰越を追加
                    if (rowSet.CarryoverToNextYear.HasValue)
                    {
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                        WriteCarryoverToNextYearRow(worksheet, currentRow, rowSet.CarryoverToNextYear.Value);
                        currentRow++;
                        rowsOnCurrentPage++;
                    }

                    // Issue #457: 最終ページの空白行に罫線を引く
                    FillEmptyRowsWithBorders(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);

                    // Issue #457: 印刷範囲を設定（全データを含む）
                    SetPrintArea(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);

                    // Issue #752: ドキュメントプロパティの日時を更新
                    // ClosedXMLはSaveAs時にCreated/Modifiedを自動更新しないため、明示的に設定する
                    var now = DateTime.Now;
                    if (!isExistingFile)
                    {
                        workbook.Properties.Created = now;
                    }
                    workbook.Properties.Modified = now;

                    // ファイルを保存
                    workbook.SaveAs(outputPath);
                }

                return ReportGenerationResult.SuccessResult(outputPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ReportGenerationResult.FailureResult(
                    "ファイルの保存に失敗しました",
                    $"出力先フォルダへのアクセス権限がありません。別のフォルダを指定するか、管理者に連絡してください。\n\n詳細: {ex.Message}");
            }
            catch (DirectoryNotFoundException ex)
            {
                return ReportGenerationResult.FailureResult(
                    "ファイルの保存に失敗しました",
                    $"出力先フォルダが見つかりません。フォルダのパスを確認してください。\n\n詳細: {ex.Message}");
            }
            catch (IOException ex)
            {
                return ReportGenerationResult.FailureResult(
                    "ファイルの保存に失敗しました",
                    $"出力先ファイルに書き込めません。ファイルが他のアプリケーションで開かれている可能性があります。\n\n詳細: {ex.Message}");
            }
            catch (Exception ex)
            {
                return ReportGenerationResult.FailureResult(
                    "帳票の作成に失敗しました",
                    $"予期しないエラーが発生しました。\n\n詳細: {ex.Message}");
            }
        }

        /// <summary>
        /// 月に対応するシート名を取得
        /// </summary>
        private static string GetMonthSheetName(int month)
        {
            return $"{month}月";
        }

        /// <summary>
        /// ワークシートのデータ部分をクリア（5行目以降）
        /// </summary>
        private static void ClearWorksheetData(IXLWorksheet worksheet)
        {
            // 5行目から使用されている最終行までをクリア
            var lastRowUsed = worksheet.LastRowUsed()?.RowNumber() ?? 4;
            if (lastRowUsed >= 5)
            {
                var rangeToDelete = worksheet.Range(5, 1, lastRowUsed, 12);
                rangeToDelete.Clear();
            }
        }

        /// <summary>
        /// テンプレートシートのフォーマットを新しいシートにコピー
        /// </summary>
        private static void CopyWorksheetFormat(IXLWorksheet source, IXLWorksheet target)
        {
            // 1〜4行目（ヘッダ部分）をコピー
            var headerRange = source.Range(1, 1, 4, 12);
            headerRange.CopyTo(target.Cell(1, 1));

            // 17〜22行目（備考欄/フッタ部分）をコピー
            CopyNotesSection(source, target);

            // 列幅をコピー
            for (int col = 1; col <= 12; col++)
            {
                target.Column(col).Width = source.Column(col).Width;
            }

            // 行の高さをコピー（1〜4行目）
            for (int row = 1; row <= 4; row++)
            {
                target.Row(row).Height = source.Row(row).Height;
            }

            // Issue #531: ページ設定（フッター/ヘッダー含む）をコピー
            CopyPageSetup(source, target);
        }

        /// <summary>
        /// Issue #531: ページ設定（フッター/ヘッダー含む）をコピー
        /// </summary>
        /// <remarks>
        /// テンプレートに設定されているフッター、ヘッダー、その他のページ設定を
        /// 新しいワークシートにコピーします。
        /// </remarks>
        private static void CopyPageSetup(IXLWorksheet source, IXLWorksheet target)
        {
            var sourceSetup = source.PageSetup;
            var targetSetup = target.PageSetup;

            // 基本ページ設定
            targetSetup.PaperSize = sourceSetup.PaperSize;
            targetSetup.PageOrientation = sourceSetup.PageOrientation;

            // マージン
            targetSetup.Margins.Top = sourceSetup.Margins.Top;
            targetSetup.Margins.Bottom = sourceSetup.Margins.Bottom;
            targetSetup.Margins.Left = sourceSetup.Margins.Left;
            targetSetup.Margins.Right = sourceSetup.Margins.Right;
            targetSetup.Margins.Header = sourceSetup.Margins.Header;
            targetSetup.Margins.Footer = sourceSetup.Margins.Footer;

            // フッター設定をコピー（全ページ共通の設定をコピー）
            CopyHeaderFooterItem(sourceSetup.Footer.Left, targetSetup.Footer.Left);
            CopyHeaderFooterItem(sourceSetup.Footer.Center, targetSetup.Footer.Center);
            CopyHeaderFooterItem(sourceSetup.Footer.Right, targetSetup.Footer.Right);

            // ヘッダー設定をコピー
            CopyHeaderFooterItem(sourceSetup.Header.Left, targetSetup.Header.Left);
            CopyHeaderFooterItem(sourceSetup.Header.Center, targetSetup.Header.Center);
            CopyHeaderFooterItem(sourceSetup.Header.Right, targetSetup.Header.Right);

            // その他のページ設定
            targetSetup.CenterHorizontally = sourceSetup.CenterHorizontally;
            targetSetup.CenterVertically = sourceSetup.CenterVertically;
            targetSetup.BlackAndWhite = sourceSetup.BlackAndWhite;
            targetSetup.DraftQuality = sourceSetup.DraftQuality;
            targetSetup.ShowGridlines = sourceSetup.ShowGridlines;
            targetSetup.ShowRowAndColumnHeadings = sourceSetup.ShowRowAndColumnHeadings;
            targetSetup.FirstPageNumber = sourceSetup.FirstPageNumber;
            targetSetup.HorizontalDpi = sourceSetup.HorizontalDpi;
            targetSetup.VerticalDpi = sourceSetup.VerticalDpi;
            targetSetup.PageOrder = sourceSetup.PageOrder;

            // スケール設定
            if (sourceSetup.PagesWide > 0 || sourceSetup.PagesTall > 0)
            {
                targetSetup.PagesWide = sourceSetup.PagesWide;
                targetSetup.PagesTall = sourceSetup.PagesTall;
            }
            else
            {
                targetSetup.Scale = sourceSetup.Scale;
            }
        }

        /// <summary>
        /// Issue #637: テンプレートから備考欄（17-22行目）を復元する
        /// </summary>
        /// <remarks>
        /// ClearWorksheetDataは5行目以降をすべてクリアするため、
        /// 備考欄（17-22行目）のセルデータも消えてしまう。
        /// テンプレートから備考欄を復元する必要がある。
        /// また、改ページ処理（CopyNotesToNewPage）はこの17-22行目を
        /// コピー元として使用するため、ここが空だと2ページ目以降の備考欄も消える。
        /// </remarks>
        private static void CopyNotesSection(IXLWorksheet source, IXLWorksheet target)
        {
            var notesRange = source.Range(17, 1, 22, 12);
            notesRange.CopyTo(target.Cell(17, 1));

            // 行の高さもコピー
            for (int row = 17; row <= 22; row++)
            {
                target.Row(row).Height = source.Row(row).Height;
            }
        }

        /// <summary>
        /// Issue #531: ヘッダー/フッターアイテムをコピー
        /// </summary>
        private static void CopyHeaderFooterItem(IXLHFItem source, IXLHFItem target)
        {
            target.Clear();

            // 各ページ種別の設定をコピー
            var occurrences = new[] { XLHFOccurrence.AllPages, XLHFOccurrence.FirstPage, XLHFOccurrence.OddPages, XLHFOccurrence.EvenPages };

            foreach (var occurrence in occurrences)
            {
                var text = source.GetText(occurrence);
                if (!string.IsNullOrEmpty(text))
                {
                    target.AddText(text, occurrence);
                }
            }
        }

        /// <summary>
        /// ワークシートを月順（4月〜3月）に並び替え
        /// </summary>
        private static void ReorderWorksheetsByMonth(XLWorkbook workbook)
        {
            // 月の順序（4月が最初、3月が最後）
            var monthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };

            var sheets = workbook.Worksheets.ToList();
            var position = 1;

            foreach (var month in monthOrder)
            {
                var sheetName = GetMonthSheetName(month);
                var sheet = sheets.FirstOrDefault(s => s.Name == sheetName);
                if (sheet != null)
                {
                    sheet.Position = position;
                    position++;
                }
            }
        }

        /// <summary>
        /// 複数カードの月次帳票を一括作成
        /// </summary>
        /// <param name="cardIdms">対象カードIDmのリスト</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <param name="outputFolder">出力先フォルダ</param>
        /// <returns>一括作成結果</returns>
        public async Task<BatchReportGenerationResult> CreateMonthlyReportsAsync(
            IEnumerable<string> cardIdms, int year, int month, string outputFolder)
        {
            var results = new List<(string CardIdm, string CardName, ReportGenerationResult Result)>();

            // テンプレートの存在確認を先に行う
            var batchSettings = _settingsRepository.GetAppSettings();
            if (!TemplateResolver.TemplateExists(batchSettings.DepartmentType))
            {
                try
                {
                    TemplateResolver.ResolveTemplatePath(batchSettings.DepartmentType);
                }
                catch (TemplateNotFoundException ex)
                {
                    return BatchReportGenerationResult.TemplateNotFound(ex.GetDetailedMessage());
                }
            }

            try
            {
                Directory.CreateDirectory(outputFolder);
            }
            catch (UnauthorizedAccessException ex)
            {
                return BatchReportGenerationResult.DirectoryCreationFailed(
                    $"出力先フォルダへのアクセス権限がありません。別のフォルダを指定するか、管理者に連絡してください。\n\n詳細: {ex.Message}");
            }
            catch (IOException ex)
            {
                return BatchReportGenerationResult.DirectoryCreationFailed(
                    $"出力先フォルダの作成に失敗しました。パスを確認してください。\n\n詳細: {ex.Message}");
            }
            catch (Exception ex)
            {
                return BatchReportGenerationResult.DirectoryCreationFailed(
                    $"出力先フォルダの作成中に予期しないエラーが発生しました。\n\n詳細: {ex.Message}");
            }

            foreach (var cardIdm in cardIdms)
            {
                var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
                if (card == null)
                {
                    results.Add((cardIdm, null, ReportGenerationResult.FailureResult(
                        "カード情報が見つかりません",
                        $"指定されたカード（IDm: {cardIdm}）は登録されていません。")));
                    continue;
                }

                var cardName = $"{card.CardType} {card.CardNumber}";
                // Issue #477: 年度ファイル名に変更
                var fiscalYear = GetFiscalYear(year, month);
                var fileName = GetFiscalYearFileName(card.CardType, card.CardNumber, fiscalYear);
                var outputPath = Path.Combine(outputFolder, fileName);

                var result = await CreateMonthlyReportAsync(cardIdm, year, month, outputPath);
                results.Add((cardIdm, cardName, result));
            }

            return new BatchReportGenerationResult(results);
        }

        /// <summary>
        /// 年度を計算（4月〜翌3月が同一年度）
        /// </summary>
        /// <param name="year">西暦年</param>
        /// <param name="month">月</param>
        /// <returns>年度（例: 2024年4月〜2025年3月 → 2024）</returns>
        public static int GetFiscalYear(int year, int month)
        {
            return FiscalYearHelper.GetFiscalYear(year, month);
        }

        /// <summary>
        /// 年度ファイル名を生成
        /// </summary>
        /// <param name="cardType">カード種別</param>
        /// <param name="cardNumber">カード番号</param>
        /// <param name="fiscalYear">年度</param>
        /// <returns>ファイル名（例: 物品出納簿_はやかけん_H001_2024年度.xlsx）</returns>
        public static string GetFiscalYearFileName(string cardType, string cardNumber, int fiscalYear)
        {
            return string.Format(
                new OrganizationOptions().ReportLayout.FileNameFormat,
                cardType, cardNumber, fiscalYear);
        }

        /// <summary>
        /// ヘッダ情報を設定
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="card">カード情報</param>
        /// <param name="pageNumber">ページ番号（省略時は設定しない）</param>
        /// <param name="headerStartRow">ヘッダーの開始行（デフォルトは1）</param>
        private void SetHeaderInfo(IXLWorksheet worksheet, IcCard card, int? pageNumber = null, int headerStartRow = 1)
        {
            // ヘッダ情報を設定（指定された開始行からの相対位置）
            var row2 = headerStartRow + 1;  // 2行目（テンプレートでは2行目にヘッダー情報）
            worksheet.Cell(row2, _orgOptions.TemplateMapping.ClassificationColumn).Value = _orgOptions.ReportLayout.ClassificationText;
            worksheet.Cell(row2, _orgOptions.TemplateMapping.CardTypeColumn).Value = card.CardType;
            worksheet.Cell(row2, _orgOptions.TemplateMapping.CardNumberColumn).Value = card.CardNumber;
            worksheet.Cell(row2, _orgOptions.TemplateMapping.UnitColumn).Value = _orgOptions.ReportLayout.UnitText;

            // Issue #510: ページ番号を設定
            if (pageNumber.HasValue)
            {
                worksheet.Cell(row2, _orgOptions.TemplateMapping.PageNumberColumn).Value = pageNumber.Value;
            }

            // ヘッダ行のフォントサイズを調整して1行に収める（1ページ目のみ）
            if (headerStartRow == 1)
            {
                AdjustHeaderRowFontSize(worksheet);
            }
        }

        /// <summary>
        /// ページ番号を設定（Issue #510）
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="headerStartRow">ヘッダーの開始行</param>
        /// <param name="pageNumber">ページ番号</param>
        private static void SetPageNumber(IXLWorksheet worksheet, int headerStartRow, int pageNumber)
        {
            var row2 = headerStartRow + 1;  // ヘッダー情報は開始行+1
            worksheet.Cell(row2, 12).Value = pageNumber;  // L列: 頁の値
        }

        /// <summary>
        /// ワークシート内の最終ページ番号を取得（Issue #809）
        /// </summary>
        /// <remarks>
        /// <para>
        /// CheckAndInsertPageBreak は改ページごとに AddHorizontalPageBreak と SetPageNumber を呼ぶため、
        /// 「1ページ目のページ番号 + 改ページ数 = 最終ページ番号」が成り立つ。
        /// </para>
        /// <para>
        /// L2 セル（1ページ目のページ番号）が空または整数として読めない場合は <c>0</c> を返す。
        /// 0 は「ページ情報を持たない（無効な）シート」を示すセンチネル値であり、呼び出し側
        /// （<see cref="FindNearestPreviousMonthLastPage"/>）はこの 0 を見て当該シートをスキップする。
        /// </para>
        /// </remarks>
        internal static int GetLastPageNumberFromWorksheet(IXLWorksheet worksheet)
        {
            var firstPageCell = worksheet.Cell(2, 12);  // L2セル: 1ページ目のページ番号
            if (firstPageCell.IsEmpty())
                return 0;

            if (!firstPageCell.TryGetValue<int>(out var firstPageNumber))
                return 0;

            var pageBreakCount = worksheet.PageSetup.RowBreaks.Count;
            return firstPageNumber + pageBreakCount;
        }

        /// <summary>
        /// 月の開始ページ番号を算出（Issue #809）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 直近の前月シートが存在し、かつそのシートが有効なページ番号情報を持つ場合は
        /// その最終ページ番号+1 を返す。それ以外（前月シートなし、もしくはあっても L2 が
        /// 空/非整数）の場合は <see cref="IcCard.StartingPageNumber"/> を使用する。
        /// </para>
        /// <para>
        /// 「直近の前月シートを探す」処理は <see cref="FindNearestPreviousMonthLastPage"/> に
        /// 委譲しており、L2 が空/非整数のシートはスキップしてさらに過去のシートを探索する
        /// 振る舞いはそちらに集約されている。
        /// </para>
        /// </remarks>
        internal static int GetStartingPageNumberForMonth(XLWorkbook workbook, IcCard card, int month)
        {
            // 年度内の月順序（4月=先頭, 3月=末尾）
            var fiscalMonthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
            var currentIndex = Array.IndexOf(fiscalMonthOrder, month);

            // 4月（年度最初の月）または不正な月 → StartingPageNumber をそのまま使用
            if (currentIndex <= 0)
                return card.StartingPageNumber;

            var nearestPreviousLastPage = FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex);
            return nearestPreviousLastPage > 0
                ? nearestPreviousLastPage + 1
                : card.StartingPageNumber;
        }

        /// <summary>
        /// 直近で「有効なページ情報を持つ前月シート」を逆順検索し、その最終ページ番号を返す。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 年度内の月順序（4月→5月→…→3月）を基準に、現在月の1つ前から先頭（4月）に向かって
        /// シートを探索する。シートが存在しても <see cref="GetLastPageNumberFromWorksheet"/> が
        /// 0 を返す場合（L2 が空または非整数の異常状態）はそのシートをスキップしてさらに過去の
        /// シートを探索する。
        /// </para>
        /// <para>
        /// この「L2 空シートのスキップ」は本メソッドの中核的な責務であり、メソッド名と本コメントで
        /// 明示している。Issue #1197: 以前は <see cref="GetStartingPageNumberForMonth"/> 内に
        /// 直接ループが書かれており、L2 空シートをスキップする動作が暗黙の前提として埋もれていた。
        /// </para>
        /// </remarks>
        /// <param name="workbook">対象ワークブック</param>
        /// <param name="fiscalMonthOrder">年度内の月順序配列（4月始まり3月終わり）</param>
        /// <param name="currentIndex">現在月の <paramref name="fiscalMonthOrder"/> 内インデックス</param>
        /// <returns>
        /// 直近の有効な前月シートの最終ページ番号。
        /// どの前月シートも存在しないか、すべて L2 が空/非整数の場合は 0。
        /// </returns>
        internal static int FindNearestPreviousMonthLastPage(
            XLWorkbook workbook, int[] fiscalMonthOrder, int currentIndex)
        {
            for (int i = currentIndex - 1; i >= 0; i--)
            {
                var prevMonthName = $"{fiscalMonthOrder[i]}月";
                if (workbook.Worksheets.TryGetWorksheet(prevMonthName, out var prevSheet))
                {
                    var lastPage = GetLastPageNumberFromWorksheet(prevSheet);
                    if (lastPage > 0)
                        return lastPage;
                }
            }
            return 0;
        }

        /// <summary>
        /// ヘッダ行のフォントサイズを調整
        /// </summary>
        /// <remarks>
        /// 物品分類～単位：円までのヘッダ部分（2行目）を1行に収めるため、
        /// フォントサイズを小さくして調整します。
        /// </remarks>
        private void AdjustHeaderRowFontSize(IXLWorksheet worksheet)
        {
            // 2行目（物品分類～単位：円）のフォントサイズを9ptに設定
            const double headerFontSize = 9;

            // A2～L2の範囲のフォントサイズを調整
            var headerRange = worksheet.Range("A2:L2");
            headerRange.Style.Font.FontSize = headerFontSize;
        }

        /// <summary>
        /// ReportRow（繰越行・データ行）を出力
        /// </summary>
        /// <remarks>
        /// Issue #1023: WriteFiscalYearCarryoverRow, WriteMonthlyCarryoverRow, WriteDataRow を統合。
        /// 行種別（繰越/データ）に関わらず、ReportRow の値をそのまま出力する。
        /// データ差異（4月の受入金額あり/なし等）は ReportRowBuilder が解決済み。
        /// </remarks>
        private int WriteReportRow(IXLWorksheet worksheet, int row, ReportRow reportRow)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = reportRow.DateDisplay;  // 出納年月日 (A列)
            worksheet.Cell(row, 2).Value = reportRow.Summary;      // 摘要 (B-D列)

            // 受入金額 (E列)
            if (reportRow.Income.HasValue)
            {
                worksheet.Cell(row, 5).Value = reportRow.Income.Value;
                worksheet.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 5).Value = "";
            }

            // 払出金額 (F列)
            if (reportRow.Expense.HasValue)
            {
                worksheet.Cell(row, 6).Value = reportRow.Expense.Value;
                worksheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 6).Value = "";
            }

            // 残額 (G列)
            if (reportRow.Balance.HasValue)
            {
                worksheet.Cell(row, 7).Value = reportRow.Balance.Value;
                worksheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 7).Value = "";
            }

            // 氏名 (H列) / 備考 (I-L列)
            worksheet.Cell(row, 8).Value = reportRow.StaffName ?? "";
            worksheet.Cell(row, 9).Value = reportRow.Note ?? "";

            // 罫線を適用
            ApplyDataRowBorder(worksheet, row);

            // 繰越行: 摘要を中央揃え
            if (reportRow.RowType == ReportRowType.Carryover)
            {
                var summaryCell = worksheet.Cell(row, 2);
                summaryCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                summaryCell.Style.Font.FontSize = 14;
            }
            else
            {
                // Issue #946: 摘要欄のフォントサイズを文字列長に応じて調整
                // ApplyDataRowBorder後に設定して14ptの一括設定を上書きする
                var summaryCell = worksheet.Cell(row, 2);
                summaryCell.Style.Font.FontSize = GetSummaryFontSize(reportRow.Summary);

                // Issue #980: 備考欄のフォントサイズを文字列長に応じて調整
                var noteCell = worksheet.Cell(row, 9);
                noteCell.Style.Font.FontSize = GetNoteFontSize(reportRow.Note);
            }

            return row + 1;
        }

        /// <summary>
        /// 摘要欄のフォントサイズを文字列の長さに応じて決定
        /// </summary>
        /// <remarks>
        /// Issue #946: B-D列結合セルの幅に収まるよう、文字数が多いほどフォントサイズを小さくする。
        /// </remarks>
        /// <param name="summary">摘要文字列</param>
        /// <returns>適切なフォントサイズ（ポイント）</returns>
        internal static double GetSummaryFontSize(string summary)
        {
            var length = summary?.Length ?? 0;

            if (length < 15)  return 14;
            if (length < 32)  return 12;
            if (length < 38)  return 10;
            if (length < 93)  return 8;
            if (length < 108) return 7;
            return 6;
        }

        /// <summary>
        /// 備考欄のフォントサイズを文字列の長さに応じて決定
        /// </summary>
        /// <remarks>
        /// Issue #980: I-L列結合セル（4列、摘要のB-D列3列より約1.33倍広い）の幅に収まるよう、
        /// 文字数が多いほどフォントサイズを小さくする。
        /// しきい値は摘要欄（GetSummaryFontSize）の約1.33倍。
        /// </remarks>
        /// <param name="note">備考文字列</param>
        /// <returns>適切なフォントサイズ（ポイント）</returns>
        internal static double GetNoteFontSize(string note)
        {
            var length = note?.Length ?? 0;

            if (length < 20)  return 14;
            if (length < 43)  return 12;
            if (length < 51)  return 10;
            if (length < 124) return 8;
            if (length < 144) return 7;
            return 6;
        }

        /// <summary>
        /// 月計行を出力
        /// </summary>
        /// <remarks>
        /// Issue #451対応: 受入金額・払出金額は0も表示（空欄にしない）
        /// Issue #813: 4月のみ累計行省略のため残額を表示
        /// Issue #1023: ReportTotal を受け取るように変更
        /// </remarks>
        private int WriteMonthlyTotalRow(IXLWorksheet worksheet, int row, ReportTotal total)
        {
            WriteTotalRowCore(worksheet, row, total);

            // 罫線を適用（月計行は上下を太線に）
            ApplySummaryRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 累計行を出力
        /// </summary>
        /// <remarks>
        /// Issue #451対応: 受入金額・払出金額は0も表示（空欄にしない）
        /// Issue #1023: ReportTotal を受け取るように変更
        /// </remarks>
        private int WriteCumulativeRow(IXLWorksheet worksheet, int row, ReportTotal total)
        {
            WriteTotalRowCore(worksheet, row, total);

            // 罫線を適用（累計行は上下を太線に）
            ApplySummaryRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 月計行・累計行の共通出力処理
        /// </summary>
        private void WriteTotalRowCore(IXLWorksheet worksheet, int row, ReportTotal total)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = "";           // 出納年月日（空欄）(A列)
            worksheet.Cell(row, 2).Value = total.Label;  // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = total.Income;  // 受入金額 (E列) - 0も表示
            worksheet.Cell(row, 6).Value = total.Expense; // 払出金額 (F列) - 0も表示

            // Issue #813: 残額が設定されている場合のみ表示（4月の月計、すべての累計）
            if (total.Balance.HasValue)
            {
                worksheet.Cell(row, 7).Value = total.Balance.Value;
                worksheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 7).Value = "";  // 残額（空欄）(G列)
            }

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            worksheet.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            worksheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0";

            // 合計行にスタイルを適用
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Font.Bold = true;

            // ラベル（B列）を中央揃え・14ptに設定
            var summaryCell = worksheet.Cell(row, 2);
            summaryCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            summaryCell.Style.Font.FontSize = 14;
        }

        /// <summary>
        /// 次年度繰越行を出力
        /// </summary>
        private int WriteCarryoverToNextYearRow(IXLWorksheet worksheet, int row, int balance)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）(A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetCarryoverToNextYearSummary(); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = "";  // 受入金額 (E列)
            worksheet.Cell(row, 6).Value = balance; // 払出金額 (F列)
            worksheet.Cell(row, 7).Value = 0;   // 残額 (G列)

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            var expenseCell = worksheet.Cell(row, 6);
            var balanceCell = worksheet.Cell(row, 7);
            expenseCell.Style.NumberFormat.Format = "#,##0";  // 会計形式（3桁カンマ区切り）
            balanceCell.Style.NumberFormat.Format = "#,##0";

            // 罫線を適用（ApplyDataRowBorderでBold=falseにリセットされるため、先に呼ぶ）
            ApplyDataRowBorder(worksheet, row);

            // 繰越行にスタイルを適用（ApplyDataRowBorder後に設定して上書き）
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Font.Bold = true;

            return row + 1;
        }

        /// <summary>
        /// データ行に罫線を適用し、セルを結合
        /// </summary>
        private void ApplyDataRowBorder(IXLWorksheet worksheet, int row)
            => ExcelStyleFormatter.ApplyDataRowBorder(worksheet, row);

        /// <summary>
        /// 月計・累計行に罫線を適用し、セルを結合
        /// </summary>
        /// <remarks>
        /// Issue #451対応:
        /// 月計・累計行の上下罫線を太線（Medium）にして視覚的に区切りを明確化。
        /// 会計マニュアルの「月計・累計欄の上下線は朱線又は太線を用いること」に対応。
        /// </remarks>
        private void ApplySummaryRowBorder(IXLWorksheet worksheet, int row)
            => ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, row);

        /// <summary>
        /// Issue #457: 改ページが必要かチェックし、必要なら挿入する
        /// Issue #510: ページ番号のトラッキングを追加
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        /// <param name="currentPageNumber">現在のページ番号（省略時はページ番号を設定しない）</param>
        /// <returns>更新された（currentRow, rowsOnCurrentPage, newPageNumber）のタプル</returns>
        /// <remarks>
        /// テンプレート構造（1ページ = 22行）:
        /// - 1-4行: ヘッダー（4行）
        /// - 5-16行: データエリア（12行）
        /// - 17-22行: 備考欄（6行）
        ///
        /// 12行を超えるデータがある場合:
        /// - 新しいページ（23行目～）にヘッダーと備考欄をコピー
        /// - データは新しいページのデータエリア（ヘッダーの後）に書き込む
        /// </remarks>
        private static (int currentRow, int rowsOnCurrentPage, int pageNumber) CheckAndInsertPageBreak(
            IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage, int currentPageNumber)
        {
            const int HeaderRows = 4;   // ヘッダーの行数（1-4行目）
            const int NotesRows = 6;    // 備考欄の行数（17-22行目）

            if (rowsOnCurrentPage >= rowsPerPage)
            {
                // 新しいページの開始行 = 現在の行 + 備考欄の行数
                // 例: currentRow=17 → newPageStartRow=23
                //     currentRow=39 → newPageStartRow=45
                var newPageStartRow = currentRow + NotesRows;

                // ヘッダー（1-4行目）を新しいページにコピー
                CopyHeaderToNewPage(worksheet, newPageStartRow);

                // 備考欄（17-22行目）を新しいページにコピー
                // 備考欄の開始行 = 新しいページの開始行 + ヘッダー + データエリア
                var notesTargetRow = newPageStartRow + HeaderRows + rowsPerPage;
                CopyNotesToNewPage(worksheet, notesTargetRow);

                // 新しいページの改ページを挿入
                // AddHorizontalPageBreak(row) は row の直前に改ページを挿入
                // 前のページの最終行（備考欄の最終行）の直後に改ページを入れるため、newPageStartRowを指定
                worksheet.PageSetup.AddHorizontalPageBreak(newPageStartRow - 1);

                // Issue #510: 新しいページにページ番号を設定
                var newPageNumber = currentPageNumber + 1;
                SetPageNumber(worksheet, newPageStartRow, newPageNumber);

                // データの開始行（ヘッダーの後）
                var newDataStartRow = newPageStartRow + HeaderRows;

                return (newDataStartRow, 0, newPageNumber);
            }
            return (currentRow, rowsOnCurrentPage, currentPageNumber);
        }

        /// <summary>
        /// Issue #457: ヘッダー（1-4行目）を新しいページにコピー
        /// </summary>
        private static void CopyHeaderToNewPage(IXLWorksheet worksheet, int targetStartRow)
        {
            // 1-4行目の内容を新しいページにコピー
            var sourceRange = worksheet.Range(1, 1, 4, 12);
            sourceRange.CopyTo(worksheet.Cell(targetStartRow, 1));

            // 行の高さもコピー
            for (int i = 0; i < 4; i++)
            {
                worksheet.Row(targetStartRow + i).Height = worksheet.Row(1 + i).Height;
            }
        }

        /// <summary>
        /// Issue #457: 備考欄（17-22行目）を新しいページにコピー
        /// </summary>
        private static void CopyNotesToNewPage(IXLWorksheet worksheet, int targetStartRow)
        {
            // 17-22行目の内容を新しいページにコピー
            var sourceRange = worksheet.Range(17, 1, 22, 12);
            sourceRange.CopyTo(worksheet.Cell(targetStartRow, 1));

            // 行の高さもコピー
            for (int i = 0; i < 6; i++)
            {
                worksheet.Row(targetStartRow + i).Height = worksheet.Row(17 + i).Height;
            }
        }

        /// <summary>
        /// Issue #457: 最終ページの空白行に罫線を引く
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号（最後に書いた行の次）</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        private static void FillEmptyRowsWithBorders(IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage)
        {
            // 最終ページに空白行がある場合、罫線を引く
            if (rowsOnCurrentPage > 0 && rowsOnCurrentPage < rowsPerPage)
            {
                var emptyRowsCount = rowsPerPage - rowsOnCurrentPage;
                for (int i = 0; i < emptyRowsCount; i++)
                {
                    var row = currentRow + i;
                    ApplyEmptyRowBorder(worksheet, row);
                }
            }
        }

        /// <summary>
        /// Issue #457: 空白行に罫線を適用
        /// </summary>
        private static void ApplyEmptyRowBorder(IXLWorksheet worksheet, int row)
            => ExcelStyleFormatter.ApplyEmptyRowBorder(worksheet, row);

        /// <summary>
        /// Issue #457: ワークシートの印刷設定を行う
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        private static void ConfigurePageSetup(IXLWorksheet worksheet)
            => ExcelStyleFormatter.ConfigurePageSetup(worksheet);

        /// <summary>
        /// Issue #457: 印刷範囲を設定
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号（最後に書いた行の次）</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        private static void SetPrintArea(IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage)
            => ExcelStyleFormatter.SetPrintArea(worksheet, currentRow, rowsOnCurrentPage, rowsPerPage);
    }
}
