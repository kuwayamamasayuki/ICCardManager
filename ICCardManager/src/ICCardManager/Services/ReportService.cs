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
        /// 月次帳票を作成
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <param name="outputPath">出力先パス</param>
        /// <returns>作成結果（成功/失敗とエラーメッセージ）</returns>
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
                var ledgers = (await _ledgerRepository.GetByMonthAsync(cardIdm, year, month))
                    .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()) // 貸出中レコードを除外
                    .OrderBy(l => l.Date)
                    .ThenBy(l => l.Id)
                    .ToList();

                // テンプレートを開く
                using var workbook = new XLWorkbook(templatePath);
                var worksheet = workbook.Worksheets.First();

                // ヘッダ情報を設定
                SetHeaderInfo(worksheet, card);

                // データを出力
                var startRow = 5; // データ開始行（テンプレートに依存: 新テンプレートでは5行目から）
                var currentRow = startRow;

                // 繰越行を追加
                if (month == 4)
                {
                    // 4月の場合は前年度繰越のみ（月繰越は行わない）
                    var carryover = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
                    currentRow = WriteFiscalYearCarryoverRow(worksheet, currentRow, carryover ?? 0, year);
                }
                else
                {
                    // 4月以外は前月繰越を追加
                    var previousMonthBalance = await GetPreviousMonthBalanceAsync(cardIdm, year, month);
                    currentRow = WriteMonthlyCarryoverRow(worksheet, currentRow, previousMonthBalance, year, month);
                }

                // 各履歴行を出力
                foreach (var ledger in ledgers)
                {
                    currentRow = WriteDataRow(worksheet, currentRow, ledger);
                }

                // 月計を出力
                var monthlyIncome = ledgers.Sum(l => l.Income);
                var monthlyExpense = ledgers.Sum(l => l.Expense);
                var monthEndBalance = ledgers.LastOrDefault()?.Balance ?? 0;

                // 月計行（残額欄は空欄、0も表示）
                currentRow = WriteMonthlyTotalRow(worksheet, currentRow, month, monthlyIncome, monthlyExpense);

                // 累計行を追加（全月で出力）
                // 年度の範囲を計算（4月～翌年3月）
                var fiscalYearStartYear = month >= 4 ? year : year - 1;
                var fiscalYearStart = new DateTime(fiscalYearStartYear, 4, 1);
                var fiscalYearEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
                var yearlyLedgers = (await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd))
                    .OrderBy(l => l.Date)
                    .ThenBy(l => l.Id)
                    .ToList();

                var yearlyIncome = yearlyLedgers.Sum(l => l.Income);
                var yearlyExpense = yearlyLedgers.Sum(l => l.Expense);
                var currentBalance = yearlyLedgers.LastOrDefault()?.Balance ?? monthEndBalance;

                currentRow = WriteCumulativeRow(worksheet, currentRow, yearlyIncome, yearlyExpense, currentBalance);

                // 3月の場合は次年度繰越を追加
                if (month == 3)
                {
                    WriteCarryoverToNextYearRow(worksheet, currentRow, currentBalance);
                }

                // ファイルを保存
                workbook.SaveAs(outputPath);
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
                var fileName = $"物品出納簿_{card.CardType}_{card.CardNumber}_{year}年{month}月.xlsx";
                var outputPath = Path.Combine(outputFolder, fileName);

                var result = await CreateMonthlyReportAsync(cardIdm, year, month, outputPath);
                results.Add((cardIdm, cardName, result));
            }

            return new BatchReportGenerationResult(results);
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
            var carryoverDate = new DateTime(year, month, 1);
            worksheet.Cell(row, 1).Value = WarekiConverter.ToWareki(carryoverDate); // 出納年月日 (A列)
            worksheet.Cell(row, 2).Value = SummaryGenerator.GetCarryoverFromPreviousMonthSummary(previousMonth); // 摘要 (B-D列)
            worksheet.Cell(row, 5).Value = balance; // 受入金額 (E列)
            worksheet.Cell(row, 6).Value = "";      // 払出金額 (F列)
            worksheet.Cell(row, 7).Value = balance; // 残額 (G列)

            // 罫線を適用
            ApplyDataRowBorder(worksheet, row);

            return row + 1;
        }

        /// <summary>
        /// 前月の残高を取得
        /// </summary>
        private async Task<int> GetPreviousMonthBalanceAsync(string cardIdm, int year, int month)
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
            var previousLedgers = (await _ledgerRepository.GetByMonthAsync(cardIdm, previousYear, previousMonth))
                .Where(l => l.Summary != SummaryGenerator.GetLendingSummary())
                .OrderBy(l => l.Date)
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
            return carryover ?? 0;
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
    }
}
