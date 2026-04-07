using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// ReportService の internal static ページ番号計算メソッドの直接単体テスト。
/// </summary>
/// <remarks>
/// 既存テスト（ReportServiceTests）は <c>CreateMonthlyReportAsync</c> を経由した統合テストで
/// L2 セル値を検証する遠回り方式だった。本テストは <c>GetLastPageNumberFromWorksheet</c> と
/// <c>GetStartingPageNumberForMonth</c> を直接呼び、エッジケース（L2 空、月=13、
/// 直近月だけシートが存在等）を高速にカバーする。
/// </remarks>
public class ReportServicePageNumberTests
{
    /// <summary>テスト用にL2セルにページ番号を設定し、必要数の改ページを追加したワークシートを作る</summary>
    private static IXLWorksheet CreateSheetWithPageInfo(
        XLWorkbook workbook,
        string sheetName,
        int? firstPageNumber,
        int pageBreakCount = 0)
    {
        var sheet = workbook.AddWorksheet(sheetName);
        if (firstPageNumber.HasValue)
        {
            sheet.Cell(2, 12).Value = firstPageNumber.Value;
        }
        for (int i = 0; i < pageBreakCount; i++)
        {
            // 異なる行で改ページを追加（同一行を2度追加するとカウントされない）
            sheet.PageSetup.AddHorizontalPageBreak(10 + i * 20);
        }
        return sheet;
    }

    private static IcCard CreateCard(int startingPageNumber = 1) =>
        new IcCard
        {
            CardIdm = "0102030405060708",
            CardType = "はやかけん",
            CardNumber = "H001",
            StartingPageNumber = startingPageNumber,
        };

    #region GetLastPageNumberFromWorksheet

    /// <summary>
    /// L2 セルが空の場合は 0 を返す
    /// </summary>
    [Fact]
    public void GetLastPageNumberFromWorksheet_EmptyL2_ReturnsZero()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("4月");

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet);

        result.Should().Be(0);
    }

    /// <summary>
    /// L2 セルが int に変換できない値（文字列）の場合は 0 を返す
    /// </summary>
    [Fact]
    public void GetLastPageNumberFromWorksheet_NonIntegerL2_ReturnsZero()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("4月");
        sheet.Cell(2, 12).Value = "abc";

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet);

        result.Should().Be(0);
    }

    /// <summary>
    /// L2=5、改ページ0個 → 5（1ページのみ）
    /// </summary>
    [Fact]
    public void GetLastPageNumberFromWorksheet_NoPageBreaks_ReturnsFirstPageNumber()
    {
        using var workbook = new XLWorkbook();
        var sheet = CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 5, pageBreakCount: 0);

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet);

        result.Should().Be(5);
    }

    /// <summary>
    /// L2=5、改ページ1個 → 6（2ページ目までで終了）
    /// </summary>
    [Fact]
    public void GetLastPageNumberFromWorksheet_OnePageBreak_ReturnsFirstPlusOne()
    {
        using var workbook = new XLWorkbook();
        var sheet = CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 5, pageBreakCount: 1);

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet);

        result.Should().Be(6);
    }

    /// <summary>
    /// L2=10、改ページ3個 → 13（4ページ目まで）
    /// </summary>
    [Fact]
    public void GetLastPageNumberFromWorksheet_MultiplePageBreaks_AddsCount()
    {
        using var workbook = new XLWorkbook();
        var sheet = CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 10, pageBreakCount: 3);

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet);

        result.Should().Be(13);
    }

    /// <summary>
    /// L2=1（最小値）、改ページ0個 → 1
    /// </summary>
    [Fact]
    public void GetLastPageNumberFromWorksheet_FirstPageOne_ReturnsOne()
    {
        using var workbook = new XLWorkbook();
        var sheet = CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 1);

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet);

        result.Should().Be(1);
    }

    #endregion

    #region GetStartingPageNumberForMonth

    /// <summary>
    /// 4月（年度最初の月）→ card.StartingPageNumber をそのまま返す（前月探索なし）
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_April_ReturnsCardStartingPageNumber()
    {
        using var workbook = new XLWorkbook();
        var card = CreateCard(startingPageNumber: 5);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 4);

        result.Should().Be(5);
    }

    /// <summary>
    /// 4月シートが存在し、L2=5、改ページなし → 5月の開始ページは 6
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_MayWithAprilSheet_ReturnsAprilLastPlusOne()
    {
        using var workbook = new XLWorkbook();
        CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 5);
        var card = CreateCard(startingPageNumber: 5);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5);

        result.Should().Be(6);
    }

    /// <summary>
    /// 4月複数ページ、L2=5+改ページ1個（最終ページ=6） → 5月の開始は 7
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_MayWithMultiPageApril_ContinuesCorrectly()
    {
        using var workbook = new XLWorkbook();
        CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 5, pageBreakCount: 1);
        var card = CreateCard(startingPageNumber: 5);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5);

        result.Should().Be(7);
    }

    /// <summary>
    /// 6月で、間の5月シートが存在しない・4月シートのみ存在 →
    /// 4月まで遡って継続する（L2=5なら6月開始は6）
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_SkipsMissingMonth_FallsBackToEarlier()
    {
        using var workbook = new XLWorkbook();
        CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 5);
        var card = CreateCard(startingPageNumber: 5);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 6);

        result.Should().Be(6);
    }

    /// <summary>
    /// どの前月シートも存在しない場合（5月で4月なし）→ card.StartingPageNumber を返す
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_NoPreviousSheets_FallsBackToStartingPageNumber()
    {
        using var workbook = new XLWorkbook();
        var card = CreateCard(startingPageNumber: 7);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5);

        result.Should().Be(7);
    }

    /// <summary>
    /// 1月（年度後半）で、12月・11月・…・4月のうち最も近い既存シート（例: 9月）から継続
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_JanuaryWithSeptemberOnly_UsesSeptember()
    {
        using var workbook = new XLWorkbook();
        // 4月～8月、10月以降の月にはシートを置かず、9月だけ作成
        CreateSheetWithPageInfo(workbook, "9月", firstPageNumber: 20, pageBreakCount: 1);
        var card = CreateCard(startingPageNumber: 1);

        // 1月の前月候補（年度月順序の逆）: 12月→11月→10月→9月... と探索
        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 1);

        // 9月の最終ページ = 20+1 = 21、よって1月開始は 22
        result.Should().Be(22);
    }

    /// <summary>
    /// 3月（年度最終月）で、2月シートが存在 → 2月の最終ページ+1
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_MarchWithFebruarySheet_ContinuesFromFebruary()
    {
        using var workbook = new XLWorkbook();
        CreateSheetWithPageInfo(workbook, "2月", firstPageNumber: 30, pageBreakCount: 2);
        var card = CreateCard(startingPageNumber: 1);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 3);

        // 2月最終ページ = 30+2 = 32、3月開始 = 33
        result.Should().Be(33);
    }

    /// <summary>
    /// 不正な月（13月）→ card.StartingPageNumber を返す
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_InvalidMonth_FallsBackToStartingPageNumber()
    {
        using var workbook = new XLWorkbook();
        CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 99);
        var card = CreateCard(startingPageNumber: 3);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 13);

        result.Should().Be(3);
    }

    /// <summary>
    /// 前月シートは存在するが L2 が空（GetLastPageNumberFromWorksheet=0）→
    /// さらに過去のシートを探索する。すべて空ならば card.StartingPageNumber を返す。
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_PreviousSheetWithEmptyL2_KeepsLooking()
    {
        using var workbook = new XLWorkbook();
        // 4月シートはあるが L2 は空
        workbook.AddWorksheet("4月");
        var card = CreateCard(startingPageNumber: 9);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5);

        // 4月の lastPage=0 → ループ継続 → これ以上前月なし → StartingPageNumber=9
        result.Should().Be(9);
    }

    /// <summary>
    /// 直近の前月（5月）にL2=空、その前（4月）に有効なL2 → 4月から継続
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_NearestSheetEmpty_FallsBackToOlder()
    {
        using var workbook = new XLWorkbook();
        // 4月: 有効
        CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 10);
        // 5月: シートはあるがL2空 → lastPage=0 でスキップされる
        workbook.AddWorksheet("5月");
        var card = CreateCard(startingPageNumber: 1);

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 6);

        // 6月→5月(空,スキップ)→4月(=10) → 6月開始は 11
        result.Should().Be(11);
    }

    #endregion

    #region FindNearestPreviousMonthLastPage（Issue #1197）

    /// <summary>
    /// Issue #1197: 抽出されたヘルパーメソッドに対する直接単体テスト。
    /// 4月始まり3月終わりの月順序配列を渡し、直近の有効前月シートが見つかれば
    /// その最終ページ番号を返すこと。
    /// </summary>
    [Fact]
    public void FindNearestPreviousMonthLastPage_PreviousSheetExists_ReturnsLastPage()
    {
        using var workbook = new XLWorkbook();
        CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 5, pageBreakCount: 2);
        var fiscalMonthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
        // 5月（インデックス1）から直前を探す
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 1);

        // 4月の最終ページ = 5+2 = 7
        result.Should().Be(7);
    }

    /// <summary>
    /// Issue #1197: ヘルパーは L2 空のシートをスキップしてさらに過去を探索する
    /// （これが本ヘルパーの中核責務）。
    /// </summary>
    [Fact]
    public void FindNearestPreviousMonthLastPage_SkipsEmptyL2AndFindsOlder()
    {
        using var workbook = new XLWorkbook();
        // 4月: 有効
        CreateSheetWithPageInfo(workbook, "4月", firstPageNumber: 10);
        // 5月: シートはあるが L2 空 → スキップ対象
        workbook.AddWorksheet("5月");
        var fiscalMonthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
        // 6月（インデックス2）から探索 → 5月スキップ → 4月に到達
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 2);

        result.Should().Be(10);
    }

    /// <summary>
    /// Issue #1197: どの前月シートも存在しない場合は 0 を返す
    /// </summary>
    [Fact]
    public void FindNearestPreviousMonthLastPage_NoPreviousSheets_ReturnsZero()
    {
        using var workbook = new XLWorkbook();
        var fiscalMonthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 5);

        result.Should().Be(0);
    }

    /// <summary>
    /// Issue #1197: 前月シートはあるが全て L2 空の場合は 0 を返す
    /// （フォールバック判定の境界）
    /// </summary>
    [Fact]
    public void FindNearestPreviousMonthLastPage_AllPreviousSheetsHaveEmptyL2_ReturnsZero()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("4月");
        workbook.AddWorksheet("5月");
        var fiscalMonthOrder = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 2);

        result.Should().Be(0);
    }

    #endregion
}
