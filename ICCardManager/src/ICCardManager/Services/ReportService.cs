using ClosedXML.Excel;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services;

/// <summary>
/// 月次帳票作成サービス
/// </summary>
public class ReportService
{
    private readonly ICardRepository _cardRepository;
    private readonly ILedgerRepository _ledgerRepository;

    /// <summary>
    /// テンプレートファイルのパス
    /// </summary>
    private string TemplatePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Resources", "Templates", "物品出納簿テンプレート.xlsx");

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
    public async Task<bool> CreateMonthlyReportAsync(string cardIdm, int year, int month, string outputPath)
    {
        try
        {
            // カード情報を取得
            var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
            if (card == null)
            {
                return false;
            }

            // 履歴を取得
            var ledgers = (await _ledgerRepository.GetByMonthAsync(cardIdm, year, month))
                .Where(l => l.Summary != SummaryGenerator.GetLendingSummary()) // 貸出中レコードを除外
                .OrderBy(l => l.Date)
                .ThenBy(l => l.Id)
                .ToList();

            // テンプレートを開く
            using var workbook = new XLWorkbook(TemplatePath);
            var worksheet = workbook.Worksheets.First();

            // ヘッダ情報を設定
            SetHeaderInfo(worksheet, card, year, month);

            // データを出力
            var startRow = 7; // データ開始行（テンプレートに依存）
            var currentRow = startRow;

            // 4月の場合は前年度繰越を追加
            if (month == 4)
            {
                var carryover = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
                currentRow = WriteCarryoverRow(worksheet, currentRow, carryover ?? 0);
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

            currentRow = WriteMonthlyTotalRow(worksheet, currentRow, month, monthlyIncome, monthlyExpense, monthEndBalance, month == 3);

            // 3月の場合は累計と次年度繰越を追加
            if (month == 3)
            {
                // 年度の累計を計算
                var fiscalYearStart = new DateTime(year, 4, 1);
                var fiscalYearEnd = new DateTime(year + 1, 3, 31);
                var yearlyLedgers = await _ledgerRepository.GetByDateRangeAsync(cardIdm, fiscalYearStart, fiscalYearEnd);

                var yearlyIncome = yearlyLedgers.Sum(l => l.Income);
                var yearlyExpense = yearlyLedgers.Sum(l => l.Expense);

                currentRow = WriteCumulativeRow(worksheet, currentRow, yearlyIncome, yearlyExpense, monthEndBalance);
                WriteCarryoverToNextYearRow(worksheet, currentRow, monthEndBalance);
            }

            // ファイルを保存
            workbook.SaveAs(outputPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 複数カードの月次帳票を一括作成
    /// </summary>
    /// <param name="cardIdms">対象カードIDmのリスト</param>
    /// <param name="year">年</param>
    /// <param name="month">月</param>
    /// <param name="outputFolder">出力先フォルダ</param>
    public async Task<List<string>> CreateMonthlyReportsAsync(
        IEnumerable<string> cardIdms, int year, int month, string outputFolder)
    {
        var createdFiles = new List<string>();
        Directory.CreateDirectory(outputFolder);

        foreach (var cardIdm in cardIdms)
        {
            var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
            if (card == null)
            {
                continue;
            }

            var fileName = $"物品出納簿_{card.CardType}_{card.CardNumber}_{year}年{month}月.xlsx";
            var outputPath = Path.Combine(outputFolder, fileName);

            if (await CreateMonthlyReportAsync(cardIdm, year, month, outputPath))
            {
                createdFiles.Add(outputPath);
            }
        }

        return createdFiles;
    }

    /// <summary>
    /// ヘッダ情報を設定
    /// </summary>
    private void SetHeaderInfo(IXLWorksheet worksheet, IcCard card, int year, int month)
    {
        var targetDate = new DateTime(year, month, 1);
        var warekiYearMonth = WarekiConverter.ToWarekiYearMonth(targetDate);

        // テンプレートのセル位置に依存（実際の位置は要調整）
        worksheet.Cell("B2").Value = "雑品（金券類）";  // 物品の分類
        worksheet.Cell("D2").Value = card.CardType;      // 品名
        worksheet.Cell("F2").Value = card.CardNumber;    // 規格
        worksheet.Cell("H2").Value = "円";               // 単位
        worksheet.Cell("B3").Value = warekiYearMonth;    // 年月
    }

    /// <summary>
    /// 前年度繰越行を出力
    /// </summary>
    private int WriteCarryoverRow(IXLWorksheet worksheet, int row, int balance)
    {
        worksheet.Cell(row, 1).Value = "4/1"; // 出納年月日
        worksheet.Cell(row, 2).Value = SummaryGenerator.GetCarryoverFromPreviousYearSummary(); // 摘要
        worksheet.Cell(row, 3).Value = balance; // 受入金額
        worksheet.Cell(row, 4).Value = "";      // 払出金額
        worksheet.Cell(row, 5).Value = balance; // 残額

        return row + 1;
    }

    /// <summary>
    /// データ行を出力
    /// </summary>
    private int WriteDataRow(IXLWorksheet worksheet, int row, Ledger ledger)
    {
        var dateStr = $"{ledger.Date.Month}/{ledger.Date.Day}";

        worksheet.Cell(row, 1).Value = dateStr;           // 出納年月日
        worksheet.Cell(row, 2).Value = ledger.Summary;    // 摘要
        worksheet.Cell(row, 3).Value = ledger.Income > 0 ? ledger.Income : (object)"";  // 受入金額
        worksheet.Cell(row, 4).Value = ledger.Expense > 0 ? ledger.Expense : (object)""; // 払出金額
        worksheet.Cell(row, 5).Value = ledger.Balance;    // 残額
        worksheet.Cell(row, 6).Value = ledger.StaffName;  // 氏名
        worksheet.Cell(row, 7).Value = ledger.Note;       // 備考

        return row + 1;
    }

    /// <summary>
    /// 月計行を出力
    /// </summary>
    private int WriteMonthlyTotalRow(
        IXLWorksheet worksheet, int row, int month,
        int income, int expense, int balance, bool isMarch)
    {
        worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）
        worksheet.Cell(row, 2).Value = SummaryGenerator.GetMonthlySummary(month); // 摘要
        worksheet.Cell(row, 3).Value = income > 0 ? income : (object)"";   // 受入金額
        worksheet.Cell(row, 4).Value = expense > 0 ? expense : (object)""; // 払出金額
        worksheet.Cell(row, 5).Value = isMarch ? "" : balance; // 残額（3月は空欄）

        // 月計行にスタイルを適用
        var range = worksheet.Range(row, 1, row, 7);
        range.Style.Font.Bold = true;

        return row + 1;
    }

    /// <summary>
    /// 累計行を出力
    /// </summary>
    private int WriteCumulativeRow(
        IXLWorksheet worksheet, int row,
        int income, int expense, int balance)
    {
        worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）
        worksheet.Cell(row, 2).Value = SummaryGenerator.GetCumulativeSummary(); // 摘要
        worksheet.Cell(row, 3).Value = income > 0 ? income : (object)"";   // 受入金額
        worksheet.Cell(row, 4).Value = expense > 0 ? expense : (object)""; // 払出金額
        worksheet.Cell(row, 5).Value = balance; // 残額

        // 累計行にスタイルを適用
        var range = worksheet.Range(row, 1, row, 7);
        range.Style.Font.Bold = true;

        return row + 1;
    }

    /// <summary>
    /// 次年度繰越行を出力
    /// </summary>
    private int WriteCarryoverToNextYearRow(IXLWorksheet worksheet, int row, int balance)
    {
        worksheet.Cell(row, 1).Value = "";  // 出納年月日（空欄）
        worksheet.Cell(row, 2).Value = SummaryGenerator.GetCarryoverToNextYearSummary(); // 摘要
        worksheet.Cell(row, 3).Value = "";  // 受入金額
        worksheet.Cell(row, 4).Value = balance; // 払出金額
        worksheet.Cell(row, 5).Value = 0;   // 残額

        // 繰越行にスタイルを適用
        var range = worksheet.Range(row, 1, row, 7);
        range.Style.Font.Bold = true;

        return row + 1;
    }
}
