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
        /// 成功した件数
        /// </summary>
        public int SuccessCount => Results.Count(r => r.Result.Success);

        /// <summary>
        /// 失敗した件数
        /// </summary>
        public int FailureCount => Results.Count(r => !r.Result.Success);

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

        public ReportService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
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
                    templatePath = TemplateResolver.ResolveTemplatePath();
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

                // 履歴を取得
                // Issue #478: 同一日ではチャージ（Income > 0）を利用より先に表示
                var ledgers = (await _ledgerRepository.GetByMonthAsync(cardIdm, year, month))
                    .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()) // 貸出中レコードを除外
                    .OrderBy(l => l.Date)
                    .ThenByDescending(l => l.Income)
                    .ThenBy(l => l.Id)
                    .ToList();

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

                    // ヘッダ情報を設定
                    SetHeaderInfo(worksheet, card);

                    // Issue #457: ページ設定（印刷時に1-4行目をヘッダーとして各ページに繰り返す）
                    ConfigurePageSetup(worksheet);

                    // Issue #457: データ出力（5～16行に内容を記載、それを超える場合は改ページ）
                    const int DataStartRow = 5;      // データ開始行
                    const int RowsPerPage = 12;      // 1ページあたりの最大データ行数（5～16行目）
                    var currentRow = DataStartRow;
                    var rowsOnCurrentPage = 0;

                    // 繰越行を追加（新規購入カードの場合は繰越行を出力しない）
                    if (month == 4)
                    {
                        // 4月の場合は前年度繰越のみ（月繰越は行わない）
                        var carryover = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
                        // 前年度のデータがある場合のみ繰越行を出力
                        if (carryover.HasValue)
                        {
                            // Issue #457: 改ページチェック
                            (currentRow, rowsOnCurrentPage) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);
                            currentRow = WriteFiscalYearCarryoverRow(worksheet, currentRow, carryover.Value, year);
                            rowsOnCurrentPage++;
                        }
                    }
                    else
                    {
                        // 4月以外は前月繰越を追加
                        var previousMonthBalance = await GetPreviousMonthBalanceAsync(cardIdm, year, month);
                        // 過去のデータがある場合のみ繰越行を出力
                        if (previousMonthBalance.HasValue)
                        {
                            // Issue #457: 改ページチェック
                            (currentRow, rowsOnCurrentPage) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);
                            currentRow = WriteMonthlyCarryoverRow(worksheet, currentRow, previousMonthBalance.Value, year, month);
                            rowsOnCurrentPage++;
                        }
                    }

                    // 各履歴行を出力
                    foreach (var ledger in ledgers)
                    {
                        // Issue #457: 改ページチェック
                        (currentRow, rowsOnCurrentPage) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);
                        currentRow = WriteDataRow(worksheet, currentRow, ledger);
                        rowsOnCurrentPage++;
                    }

                    // 月計を出力
                    var monthlyIncome = ledgers.Sum(l => l.Income);
                    var monthlyExpense = ledgers.Sum(l => l.Expense);
                    var monthEndBalance = ledgers.LastOrDefault()?.Balance ?? 0;

                    // Issue #457: 改ページチェック
                    (currentRow, rowsOnCurrentPage) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);
                    // 月計行（残額欄は空欄、0も表示）
                    currentRow = WriteMonthlyTotalRow(worksheet, currentRow, month, monthlyIncome, monthlyExpense);
                    rowsOnCurrentPage++;

                    // 累計行を追加（全月で出力）
                    // 年度の範囲を計算（4月～翌年3月）
                    var fiscalYearStartYear = month >= 4 ? year : year - 1;
                    var fiscalYearStart = new DateTime(fiscalYearStartYear, 4, 1);
                    var fiscalYearEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
                    // Issue #478: 同一日ではチャージ（Income > 0）を利用より先に表示（一貫性のため）
                    var yearlyLedgers = (await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd))
                        .OrderBy(l => l.Date)
                        .ThenByDescending(l => l.Income)
                        .ThenBy(l => l.Id)
                        .ToList();

                    var yearlyIncome = yearlyLedgers.Sum(l => l.Income);
                    var yearlyExpense = yearlyLedgers.Sum(l => l.Expense);
                    var currentBalance = yearlyLedgers.LastOrDefault()?.Balance ?? monthEndBalance;

                    // Issue #457: 改ページチェック
                    (currentRow, rowsOnCurrentPage) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);
                    currentRow = WriteCumulativeRow(worksheet, currentRow, yearlyIncome, yearlyExpense, currentBalance);
                    rowsOnCurrentPage++;

                    // 3月の場合は次年度繰越を追加
                    if (month == 3)
                    {
                        // Issue #457: 改ページチェック
                        (currentRow, rowsOnCurrentPage) = CheckAndInsertPageBreak(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);
                        WriteCarryoverToNextYearRow(worksheet, currentRow, currentBalance);
                        currentRow++;
                        rowsOnCurrentPage++;
                    }

                    // Issue #457: 最終ページの空白行に罫線を引く
                    FillEmptyRowsWithBorders(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);

                    // Issue #457: 印刷範囲を設定（全データを含む）
                    SetPrintArea(worksheet, currentRow, rowsOnCurrentPage, RowsPerPage);

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
            var notesRange = source.Range(17, 1, 22, 12);
            notesRange.CopyTo(target.Cell(17, 1));

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

            // 行の高さをコピー（17〜22行目）
            for (int row = 17; row <= 22; row++)
            {
                target.Row(row).Height = source.Row(row).Height;
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
        /// 月次帳票を作成（後方互換性のためのラッパー）
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <param name="outputPath">出力先パス</param>
        /// <returns>成功した場合true</returns>
        [Obsolete("CreateMonthlyReportAsyncを使用してください。エラーメッセージが取得できます。")]
        public async Task<bool> CreateMonthlyReportAsyncLegacy(string cardIdm, int year, int month, string outputPath)
        {
            var result = await CreateMonthlyReportAsync(cardIdm, year, month, outputPath);
            return result.Success;
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
            if (!TemplateResolver.TemplateExists())
            {
                try
                {
                    TemplateResolver.ResolveTemplatePath();
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
        private void SetHeaderInfo(IXLWorksheet worksheet, IcCard card)
        {
            // 2行目のヘッダ情報を設定（新テンプレートのセル位置に合わせる）
            worksheet.Cell("B2").Value = "雑品（金券類）";   // 物品の分類の値（固定）
            worksheet.Cell("E2").Value = card.CardType;      // 品名の値
            worksheet.Cell("H2").Value = card.CardNumber;    // 規格の値
            worksheet.Cell("J2").Value = "円";               // 単位の値（固定）

            // ヘッダ行のフォントサイズを調整して1行に収める
            AdjustHeaderRowFontSize(worksheet);
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
            // Issue #478: 同一日ではチャージ（Income > 0）を利用より先に表示（一貫性のため）
            var previousLedgers = (await _ledgerRepository.GetByMonthAsync(cardIdm, previousYear, previousMonth))
                .Where(l => l.Summary != SummaryGenerator.GetLendingSummary())
                .OrderBy(l => l.Date)
                .ThenByDescending(l => l.Income)
                .ThenBy(l => l.Id)
                .ToList();

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
        /// - 残額は常に空欄
        /// - 上下に太線罫線を追加
        /// </remarks>
        private int WriteMonthlyTotalRow(
            IXLWorksheet worksheet, int row, int month,
            int income, int expense)
        {
            // 列配置: A=出納年月日, B-D=摘要(結合), E=受入金額, F=払出金額, G=残額, H=氏名, I-L=備考(結合)
            worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）(A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetMonthlySummary(month); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = income;   // 受入金額 (E列) - 0も表示
            worksheet.Cell(row, 6).Value = expense;  // 払出金額 (F列) - 0も表示
            worksheet.Cell(row, 7).Value = "";       // 残額（常に空欄）(G列)

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

            // 繰越行にスタイルを適用
            var range = worksheet.Range(row, 1, row, 12);
            range.Style.Font.Bold = true;

            // 罫線を適用
            ApplyDataRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// データ行に罫線を適用し、セルを結合
        /// </summary>
        private void ApplyDataRowBorder(IXLWorksheet worksheet, int row)
        {
            // 行の高さを30に設定
            worksheet.Row(row).Height = 30;

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
        /// </summary>
        /// <param name="worksheet">ワークシート</param>
        /// <param name="currentRow">現在の行番号</param>
        /// <param name="rowsOnCurrentPage">現在のページに書かれた行数</param>
        /// <param name="rowsPerPage">1ページあたりの最大行数</param>
        /// <returns>更新された（currentRow, rowsOnCurrentPage）のタプル</returns>
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
        private static (int currentRow, int rowsOnCurrentPage) CheckAndInsertPageBreak(
            IXLWorksheet worksheet, int currentRow, int rowsOnCurrentPage, int rowsPerPage)
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

                // データの開始行（ヘッダーの後）
                var newDataStartRow = newPageStartRow + HeaderRows;

                return (newDataStartRow, 0);
            }
            return (currentRow, rowsOnCurrentPage);
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
