using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using ClosedXML.Excel;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

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

        public ReportService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            ISettingsRepository settingsRepository)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _settingsRepository = settingsRepository;
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

                // カード情報を取得
                var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
                if (card == null)
                {
                    return ReportGenerationResult.FailureResult(
                        "カード情報が見つかりません",
                        $"指定されたカード（IDm: {cardIdm}）は登録されていません。");
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

                // 前月末残高を取得（履歴の並び替えにも使用）
                int? precedingBalance = null;
                if (month == 4)
                {
                    precedingBalance = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
                }
                else
                {
                    precedingBalance = await GetPreviousMonthBalanceAsync(cardIdm, year, month);
                }

                // 履歴を取得
                // Issue #784: 残高チェーンに基づいて同一日内の時系列順を復元
                var ledgers = LedgerOrderHelper.ReorderByBalanceChain(
                    (await _ledgerRepository.GetByMonthAsync(cardIdm, year, month))
                        .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()),
                    precedingBalance);

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

                    // Issue #457: データ出力（5～16行に内容を記載、それを超える場合は改ページ）
                    const int DataStartRow = 5;      // データ開始行
                    const int RowsPerPage = 12;      // 1ページあたりの最大データ行数（5～16行目）
                    var currentRow = DataStartRow;
                    var rowsOnCurrentPage = 0;

                    // 繰越行を追加（新規購入カードの場合は繰越行を出力しない）
                    // precedingBalanceは既に上部で取得済み
                    if (month == 4)
                    {
                        // 4月の場合は前年度繰越のみ（月繰越は行わない）
                        // 前年度のデータがある場合のみ繰越行を出力
                        if (precedingBalance.HasValue)
                        {
                            // Issue #457: 改ページチェック
                            (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                            currentRow = WriteFiscalYearCarryoverRow(worksheet, currentRow, precedingBalance.Value, year);
                            rowsOnCurrentPage++;
                        }
                    }
                    else
                    {
                        // 4月以外は前月繰越を追加
                        // 過去のデータがある場合のみ繰越行を出力
                        if (precedingBalance.HasValue)
                        {
                            // Issue #457: 改ページチェック
                            (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                            currentRow = WriteMonthlyCarryoverRow(worksheet, currentRow, precedingBalance.Value, year, month);
                            rowsOnCurrentPage++;
                        }
                    }

                    // 各履歴行を出力
                    foreach (var ledger in ledgers)
                    {
                        // Issue #457: 改ページチェック
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                        currentRow = WriteDataRow(worksheet, currentRow, ledger);
                        rowsOnCurrentPage++;
                    }

                    // 月計を出力
                    var monthlyIncome = ledgers.Sum(l => l.Income);
                    var monthlyExpense = ledgers.Sum(l => l.Expense);
                    var monthEndBalance = ledgers.LastOrDefault()?.Balance ?? 0;

                    // 累計データを計算（4月の月計残額表示にも使用）
                    // 年度の範囲を計算（4月～翌年3月）
                    var fiscalYearStartYear = month >= 4 ? year : year - 1;
                    var fiscalYearStart = new DateTime(fiscalYearStartYear, 4, 1);
                    var fiscalYearEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
                    // Issue #784: 残高チェーンに基づいて時系列順を復元
                    var yearlyLedgers = LedgerOrderHelper.ReorderByBalanceChain(
                        await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd));

                    var yearlyIncome = yearlyLedgers.Sum(l => l.Income);
                    var yearlyExpense = yearlyLedgers.Sum(l => l.Expense);
                    var currentBalance = yearlyLedgers.LastOrDefault()?.Balance ?? monthEndBalance;

                    // Issue #457: 改ページチェック
                    (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);

                    if (month == 4)
                    {
                        // Issue #813: 4月は月計と累計が同額のため累計行を省略し、月計行に残額を表示
                        // データがない場合は前年度繰越額をフォールバックとして使用
                        var aprilBalance = yearlyLedgers.Any()
                            ? currentBalance
                            : (precedingBalance ?? 0);
                        currentRow = WriteMonthlyTotalRow(worksheet, currentRow, month, monthlyIncome, monthlyExpense, aprilBalance);
                        rowsOnCurrentPage++;
                    }
                    else
                    {
                        // 月計行（残額欄は空欄、0も表示）
                        currentRow = WriteMonthlyTotalRow(worksheet, currentRow, month, monthlyIncome, monthlyExpense);
                        rowsOnCurrentPage++;

                        // 累計行を追加（5月～3月で出力）
                        // Issue #457: 改ページチェック
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                        currentRow = WriteCumulativeRow(worksheet, currentRow, yearlyIncome, yearlyExpense, currentBalance);
                        rowsOnCurrentPage++;
                    }

                    // 3月の場合は次年度繰越を追加
                    if (month == 3)
                    {
                        // Issue #457: 改ページチェック
                        (currentRow, rowsOnCurrentPage, currentPageNumber) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage, currentPageNumber);
                        WriteCarryoverToNextYearRow(worksheet, currentRow, currentBalance);
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
            return month >= 4 ? year : year - 1;
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
            return $"物品出納簿_{cardType}_{cardNumber}_{fiscalYear}年度.xlsx";
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
            worksheet.Cell(row2, 2).Value = "雑品（金券類）";   // B列: 物品の分類の値（固定）
            worksheet.Cell(row2, 5).Value = card.CardType;      // E列: 品名の値
            worksheet.Cell(row2, 8).Value = card.CardNumber;    // H列: 規格の値
            worksheet.Cell(row2, 10).Value = "円";              // J列: 単位の値（固定）

            // Issue #510: ページ番号を設定
            if (pageNumber.HasValue)
            {
                worksheet.Cell(row2, 12).Value = pageNumber.Value;  // L列: 頁の値
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
        /// CheckAndInsertPageBreak は改ページごとに AddHorizontalPageBreak と SetPageNumber を呼ぶため、
        /// 「1ページ目のページ番号 + 改ページ数 = 最終ページ番号」が成り立つ。
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
        /// 前月のシートが存在する場合はその最終ページ番号+1、
        /// 存在しない場合は card.StartingPageNumber を使用する。
        /// 年度内の月順序に従い、直近で存在するシートまで遡って検索する。
        /// </remarks>
        internal static int GetStartingPageNumberForMonth(XLWorkbook workbook, IcCard card, int month)
        {
            // 年度内の月順序（4月=先頭, 3月=末尾）
            var fiscalMonthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
            var currentIndex = Array.IndexOf(fiscalMonthOrder, month);

            // 4月（年度最初の月）または不正な月 → StartingPageNumber をそのまま使用
            if (currentIndex <= 0)
                return card.StartingPageNumber;

            // 直前の月から逆順に、存在するシートを探す
            for (int i = currentIndex - 1; i >= 0; i--)
            {
                var prevMonthName = $"{fiscalMonthOrder[i]}月";
                if (workbook.Worksheets.TryGetWorksheet(prevMonthName, out var prevSheet))
                {
                    var lastPage = GetLastPageNumberFromWorksheet(prevSheet);
                    if (lastPage > 0)
                        return lastPage + 1;
                }
            }

            // どの月のシートも存在しない → StartingPageNumber を使用
            return card.StartingPageNumber;
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
        /// 前年度繰越行を出力（4月用）
        /// </summary>
        private int WriteFiscalYearCarryoverRow(IXLWorksheet worksheet, int row, int balance, int year)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            var carryoverDate = new DateTime(year, 4, 1);
            worksheet.Cell(row, 1).Value = WarekiConverter.ToWareki(carryoverDate); // 出納年月日 (A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetCarryoverFromPreviousYearSummary(); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = balance; // 受入金額 (E列)
            worksheet.Cell(row, 6).Value = "";      // 払出金額 (F列)
            worksheet.Cell(row, 7).Value = balance; // 残額 (G列)

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            var incomeCell = worksheet.Cell(row, 5);
            var balanceCell = worksheet.Cell(row, 7);
            incomeCell.Style.NumberFormat.Format = "#,##0";  // 会計形式（3桁カンマ区切り）
            balanceCell.Style.NumberFormat.Format = "#,##0";

            // 罫線を適用
            ApplyDataRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 前月繰越行を出力（4月以外用）
        /// </summary>
        private int WriteMonthlyCarryoverRow(IXLWorksheet worksheet, int row, int balance, int year, int month)
        {
            // 前月の月番号を計算
            var previousMonth = month == 1 ? 12 : month - 1;

            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            // Issue #481: 月次繰越の受入欄は記載しない（年度繰越のみ受入欄に記載）
            var carryoverDate = new DateTime(year, month, 1);
            worksheet.Cell(row, 1).Value = WarekiConverter.ToWareki(carryoverDate); // 出納年月日 (A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetCarryoverFromPreviousMonthSummary(previousMonth); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = "";      // 受入金額 (E列) - 月次繰越は空欄
            worksheet.Cell(row, 6).Value = "";      // 払出金額 (F列)
            worksheet.Cell(row, 7).Value = balance; // 残額 (G列)

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            var balanceCell = worksheet.Cell(row, 7);
            balanceCell.Style.NumberFormat.Format = "#,##0";  // 会計形式（3桁カンマ区切り）

            // 繰越テキスト（B列）を中央揃え・14ptに設定
            var summaryCell = worksheet.Cell(row, 2);
            summaryCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            summaryCell.Style.Font.FontSize = 14;

            // 罫線を適用
            ApplyDataRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 前月の残高を取得
        /// </summary>
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
            // 過去のデータがない場合はnullを返す（新規購入カードの場合）
            return carryover;
        }

        /// <summary>
        /// データ行を出力
        /// </summary>
        private int WriteDataRow(IXLWorksheet worksheet, int row, Ledger ledger)
        {
            var dateStr = WarekiConverter.ToWareki(ledger.Date);

            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = dateStr;           // 出納年月日 (A列)
            worksheet.Cell(row, 2).Value = ledger.Summary;    // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = ledger.Income > 0 ? ledger.Income : Blank.Value;  // 受入金額 (E列)
            worksheet.Cell(row, 6).Value = ledger.Expense > 0 ? ledger.Expense : Blank.Value; // 払出金額 (F列)
            worksheet.Cell(row, 7).Value = ledger.Balance;    // 残額 (G列)
            worksheet.Cell(row, 8).Value = ledger.StaffName;  // 氏名 (H列)
            worksheet.Cell(row, 9).Value = ledger.Note;       // 備考 (I-L列)

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            var incomeCell = worksheet.Cell(row, 5);
            var expenseCell = worksheet.Cell(row, 6);
            var balanceCell = worksheet.Cell(row, 7);
            incomeCell.Style.NumberFormat.Format = "#,##0";  // 会計形式（3桁カンマ区切り）
            expenseCell.Style.NumberFormat.Format = "#,##0";
            balanceCell.Style.NumberFormat.Format = "#,##0";

            // 罫線を適用
            ApplyDataRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 月計行を出力
        /// </summary>
        /// <remarks>
        /// Issue #451対応:
        /// - 受入金額・払出金額は0も表示（空欄にしない）
        /// - 残額は通常空欄（Issue #813: 4月のみ累計行省略のため残額を表示）
        /// - 上下に太線罫線を追加
        /// </remarks>
        private int WriteMonthlyTotalRow(
            IXLWorksheet worksheet, int row, int month,
            int income, int expense, int? balance = null)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）(A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetMonthlySummary(month); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = income;   // 受入金額 (E列) - 0も表示
            worksheet.Cell(row, 6).Value = expense;  // 払出金額 (F列) - 0も表示

            // Issue #813: 4月は累計行を省略するため、月計行に残額を表示
            if (balance.HasValue)
            {
                worksheet.Cell(row, 7).Value = balance.Value;
                worksheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            }
            else
            {
                worksheet.Cell(row, 7).Value = "";  // 残額（空欄）(G列)
            }

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            var incomeCell = worksheet.Cell(row, 5);
            var expenseCell = worksheet.Cell(row, 6);
            incomeCell.Style.NumberFormat.Format = "#,##0";  // 会計形式（3桁カンマ区切り）
            expenseCell.Style.NumberFormat.Format = "#,##0";

            // 月計行にスタイルを適用
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Font.Bold = true;

            // 月計テキスト（B列）を中央揃え・14ptに設定
            var summaryCell = worksheet.Cell(row, 2);
            summaryCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            summaryCell.Style.Font.FontSize = 14;

            // 罫線を適用（月計行は上下を太線に）
            ApplySummaryRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 累計行を出力
        /// </summary>
        /// <remarks>
        /// Issue #451対応:
        /// - 受入金額・払出金額は0も表示（空欄にしない）
        /// - 上下に太線罫線を追加
        /// - 累計テキストを中央揃え・14ptに設定
        /// </remarks>
        private int WriteCumulativeRow(
            IXLWorksheet worksheet, int row,
            int income, int expense, int balance)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）(A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetCumulativeSummary(); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = income;   // 受入金額 (E列) - 0も表示
            worksheet.Cell(row, 6).Value = expense;  // 払出金額 (F列) - 0も表示
            worksheet.Cell(row, 7).Value = balance; // 残額 (G列)

            // Issue #509: 金額セルの表示形式を明示的に数値に設定
            // テンプレートのセル書式が「文字列」になっている場合に備えて
            var incomeCell = worksheet.Cell(row, 5);
            var expenseCell = worksheet.Cell(row, 6);
            var balanceCell = worksheet.Cell(row, 7);
            incomeCell.Style.NumberFormat.Format = "#,##0";  // 会計形式（3桁カンマ区切り）
            expenseCell.Style.NumberFormat.Format = "#,##0";
            balanceCell.Style.NumberFormat.Format = "#,##0";

            // 累計行にスタイルを適用
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Font.Bold = true;

            // 累計テキスト（B列）を中央揃え・14ptに設定
            var summaryCell = worksheet.Cell(row, 2);
            summaryCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            summaryCell.Style.Font.FontSize = 14;

            // 罫線を適用（累計行は上下を太線に）
            ApplySummaryRowBorder(worksheet, row);

            return row + 1;
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
        {
            // 行の高さを30に設定
            worksheet.Row(row).Height = 30;

            // Issue #591: 既存ファイル上書き時に前回の太字書式が残る場合があるため、
            // データ行では太字を明示的にリセットする
            var fullRange = worksheet.Range(row, 1, row, 12);
            fullRange.Style.Font.Bold = false;

            // E〜G列（受入金額、払出金額、残額）のフォントサイズを14ptに設定
            // 最大金額20,000円（6文字）を考慮し、列幅10に収まるサイズ
            var amountRange = worksheet.Range(row, 5, row, 7);
            amountRange.Style.Font.FontSize = 14;

            // B列からD列を結合（摘要）
            var summaryRange = worksheet.Range(row, 2, row, 4);
            summaryRange.Merge();
            summaryRange.Style.Alignment.WrapText = true; // 折り返して全体を表示

            // I列からL列を結合（備考）
            var noteRange = worksheet.Range(row, 9, row, 12);
            noteRange.Merge();
            noteRange.Style.Alignment.WrapText = true; // 折り返して全体を表示

            // A列（出納年月日）のフォントサイズを14ptに設定し、中央寄せ
            // 文字が大きすぎる場合に備えて「縮小して全体を表示する」を設定
            var dateCell = worksheet.Cell(row, 1);
            dateCell.Style.Font.FontSize = 14;
            dateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            dateCell.Style.Alignment.ShrinkToFit = true;

            // H列（氏名）を中央寄せ
            worksheet.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // A列からL列まで罫線を適用
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            // 両端（A列左側、L列右側）は太線で表示
            worksheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            worksheet.Cell(row, 12).Style.Border.RightBorder = XLBorderStyleValues.Medium;

            // 行全体を上下中央揃えに設定
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        /// <summary>
        /// 月計・累計行に罫線を適用し、セルを結合
        /// </summary>
        /// <remarks>
        /// Issue #451対応:
        /// 月計・累計行の上下罫線を太線（Medium）にして視覚的に区切りを明確化。
        /// 会計マニュアルの「月計・累計欄の上下線は朱線又は太線を用いること」に対応。
        /// </remarks>
        private void ApplySummaryRowBorder(IXLWorksheet worksheet, int row)
        {
            // 行の高さを30に設定
            worksheet.Row(row).Height = 30;

            // E〜G列（受入金額、払出金額、残額）のフォントサイズを14ptに設定
            var amountRange = worksheet.Range(row, 5, row, 7);
            amountRange.Style.Font.FontSize = 14;

            // B列からD列を結合（摘要）
            var summaryRange = worksheet.Range(row, 2, row, 4);
            summaryRange.Merge();
            summaryRange.Style.Alignment.WrapText = true;

            // I列からL列を結合（備考）
            var noteRange = worksheet.Range(row, 9, row, 12);
            noteRange.Merge();
            noteRange.Style.Alignment.WrapText = true;

            // A列（出納年月日）のフォントサイズを14ptに設定し、中央寄せ
            var dateCell = worksheet.Cell(row, 1);
            dateCell.Style.Font.FontSize = 14;
            dateCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            dateCell.Style.Alignment.ShrinkToFit = true;

            // H列（氏名）を中央寄せ
            worksheet.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // A列からL列まで罫線を適用（内側は細線）
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            // 月計・累計行は上下を太線に
            range.Style.Border.TopBorder = XLBorderStyleValues.Medium;
            range.Style.Border.BottomBorder = XLBorderStyleValues.Medium;

            // 両端（A列左側、L列右側）は太線で表示
            worksheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            worksheet.Cell(row, 12).Style.Border.RightBorder = XLBorderStyleValues.Medium;

            // 行全体を上下中央揃えに設定
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

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
        {
            // 行の高さを30に設定
            worksheet.Row(row).Height = 30;

            // Issue #591: 既存ファイル上書き時に残った太字書式をリセット
            var fullRange = worksheet.Range(row, 1, row, 12);
            fullRange.Style.Font.Bold = false;

            // B列からD列を結合（摘要）
            var summaryRange = worksheet.Range(row, 2, row, 4);
            summaryRange.Merge();

            // I列からL列を結合（備考）
            var noteRange = worksheet.Range(row, 9, row, 12);
            noteRange.Merge();

            // A列からL列まで罫線を適用
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            // 両端（A列左側、L列右側）は太線で表示
            worksheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            worksheet.Cell(row, 12).Style.Border.RightBorder = XLBorderStyleValues.Medium;
        }

        /// <summary>
        /// Issue #457: ワークシートの印刷設定を行う
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        private static void ConfigurePageSetup(IXLWorksheet worksheet)
        {
            // 用紙サイズ: A4
            worksheet.PageSetup.PaperSize = XLPaperSize.A4Paper;

            // 印刷の向き: 横
            worksheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;

            // マージンの設定（単位: インチ）
            worksheet.PageSetup.Margins.Top = 0.5;
            worksheet.PageSetup.Margins.Bottom = 0.5;
            worksheet.PageSetup.Margins.Left = 0.5;
            worksheet.PageSetup.Margins.Right = 0.5;

            // 注: 印刷タイトル（SetRowsToRepeatAtTop）は使用しない
            // 各ページにヘッダーをコピーするため不要
        }

        /// <summary>
        /// Issue #457: 印刷範囲を設定
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号（最後に書いた行の次）</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        private static void SetPrintArea(IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage)
        {
            const int NotesRows = 6;    // 備考欄の行数
            const int RowsPerPageTotal = 22;  // 1ページの総行数

            // 最終行を計算
            int lastRow;
            if (rowsOnCurrentPage == 0)
            {
                // 改ページ直後（データがまだ書かれていない）
                // 前のページの備考欄の最終行
                lastRow = currentRow - 1;
            }
            else
            {
                // 現在のページにデータがある場合
                // 現在のページの備考欄の最終行を計算
                // データエリアの最終行 = currentRow - 1
                // 備考欄の最終行 = データエリアの最終行 + NotesRows + (rowsPerPage - rowsOnCurrentPage)
                var dataAreaEndRow = currentRow - 1;
                var remainingDataRows = rowsPerPage - rowsOnCurrentPage;
                lastRow = dataAreaEndRow + remainingDataRows + NotesRows;
            }

            // 1ページ目のみの場合は22行目まで
            if (lastRow < RowsPerPageTotal)
            {
                lastRow = RowsPerPageTotal;
            }

            // 印刷範囲を設定（A1からL列の最終行まで）
            worksheet.PageSetup.PrintAreas.Clear();
            worksheet.PageSetup.PrintAreas.Add(1, 1, lastRow, 12);
        }
    }
}
