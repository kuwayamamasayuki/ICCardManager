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
    /// <summary>
    /// 既定のページ番号列（L 列 = 12）。Issue #1956 で列が引数になったため、
    /// シート作成側と呼び出し側で同じ値を使う。
    /// </summary>
    /// <remarks>
    /// 本クラスは既定レイアウトでのページ番号計算を検査する。設定を変えた場合の挙動は
    /// <c>ReportServicePageNumberColumnTests</c> が担当する。
    /// </remarks>
    private const int PageNumberColumn = 12;

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
            sheet.Cell(2, PageNumberColumn).Value = firstPageNumber.Value;
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

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet, PageNumberColumn);

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

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet, PageNumberColumn);

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

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet, PageNumberColumn);

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

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet, PageNumberColumn);

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

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet, PageNumberColumn);

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

        var result = ReportService.GetLastPageNumberFromWorksheet(sheet, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 4, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 6, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5, PageNumberColumn);

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
        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 1, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 3, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 13, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 5, PageNumberColumn);

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

        var result = ReportService.GetStartingPageNumberForMonth(workbook, card, 6, PageNumberColumn);

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
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 1, pageNumberColumn: PageNumberColumn);

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
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 2, pageNumberColumn: PageNumberColumn);

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
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 5, pageNumberColumn: PageNumberColumn);

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
        var result = ReportService.FindNearestPreviousMonthLastPage(workbook, fiscalMonthOrder, currentIndex: 2, pageNumberColumn: PageNumberColumn);

        result.Should().Be(0);
    }

    #endregion

    #region RenumberFollowingMonthSheets（Issue #2048）

    /// <summary>
    /// 帳票と同じレイアウト（1 ページ 22 行、改ページは前ページの最終行 22k の直後）で
    /// 頁番号を付けたシートを作る。ページ k（0 始まり）の頁番号セルは 2 + 22k 行目。
    /// </summary>
    private static IXLWorksheet CreateReportLikeSheet(XLWorkbook workbook, string sheetName, int firstPageNumber, int pageCount)
    {
        var sheet = workbook.AddWorksheet(sheetName);
        for (int k = 0; k < pageCount; k++)
        {
            if (k > 0)
            {
                sheet.PageSetup.AddHorizontalPageBreak(22 * k);
            }
            sheet.Cell(2 + 22 * k, PageNumberColumn).Value = firstPageNumber + k;
        }
        return sheet;
    }

    private static int PageNumberAt(IXLWorksheet sheet, int pageIndex) =>
        sheet.Cell(2 + 22 * pageIndex, PageNumberColumn).GetValue<int>();

    /// <summary>
    /// 作成した月より後の月だけを、継続ページも含めて月順に連番へ付け直す。前の月は動かさない。
    /// </summary>
    [Fact]
    public void RenumberFollowingMonthSheets_RenumbersLaterMonthsIncludingContinuationPages()
    {
        using var workbook = new XLWorkbook();
        // 4月・5月はどちらも「付け直せば別の値になる」番号にしておく。付け直しの開始位置を誤って
        // 4月や作成した月（5月）から始める実装だと、これらの表明が赤になる
        var april = CreateReportLikeSheet(workbook, "4月", firstPageNumber: 7, pageCount: 1);   // カードの開始頁(1)と異なる
        var may = CreateReportLikeSheet(workbook, "5月", firstPageNumber: 20, pageCount: 1);    // 作成した月（4月の続きではない）
        var june = CreateReportLikeSheet(workbook, "6月", firstPageNumber: 10, pageCount: 3);  // 旧番号のまま
        var july = CreateReportLikeSheet(workbook, "7月", firstPageNumber: 3, pageCount: 1);   // 重複
        var card = CreateCard(startingPageNumber: 1);

        ReportService.RenumberFollowingMonthSheets(workbook, card, 5, PageNumberColumn);

        PageNumberAt(april, 0).Should().Be(7, "作成した月より前の月は付け直さない");
        PageNumberAt(may, 0).Should().Be(20, "作成した月自体は付け直さない（作成時に決めた番号を正とする）");
        PageNumberAt(june, 0).Should().Be(21);
        PageNumberAt(june, 1).Should().Be(22);
        PageNumberAt(june, 2).Should().Be(23);
        PageNumberAt(july, 0).Should().Be(24, "付け直した 6月の最終ページに続く");
    }

    /// <summary>
    /// 年をまたぐ月順（12月 → 1月 → 3月）で付け直し、間の欠けた月（2月）は飛ばして続ける。
    /// </summary>
    [Fact]
    public void RenumberFollowingMonthSheets_FollowsFiscalMonthOrderAcrossYearEnd()
    {
        using var workbook = new XLWorkbook();
        // シートの並び順ではなく年度の月順で辿ることを確かめるため、意図的に逆順で追加する
        var march = CreateReportLikeSheet(workbook, "3月", firstPageNumber: 99, pageCount: 1);
        var january = CreateReportLikeSheet(workbook, "1月", firstPageNumber: 50, pageCount: 2);
        CreateReportLikeSheet(workbook, "12月", firstPageNumber: 20, pageCount: 1);
        var card = CreateCard(startingPageNumber: 1);

        ReportService.RenumberFollowingMonthSheets(workbook, card, 12, PageNumberColumn);

        PageNumberAt(january, 0).Should().Be(21);
        PageNumberAt(january, 1).Should().Be(22);
        PageNumberAt(march, 0).Should().Be(23);
    }

    /// <summary>
    /// 頁番号セルが空のシート（有効な頁情報を持たない）は書き込まず、探索と同じくスキップする。
    /// </summary>
    [Fact]
    public void RenumberFollowingMonthSheets_SheetWithoutPageInfo_IsLeftUntouchedAndSkipped()
    {
        using var workbook = new XLWorkbook();
        CreateReportLikeSheet(workbook, "4月", firstPageNumber: 7, pageCount: 1);
        var may = workbook.AddWorksheet("5月");
        var june = CreateReportLikeSheet(workbook, "6月", firstPageNumber: 30, pageCount: 1);
        var card = CreateCard(startingPageNumber: 1);

        ReportService.RenumberFollowingMonthSheets(workbook, card, 4, PageNumberColumn);

        may.Cell(2, PageNumberColumn).IsEmpty().Should().BeTrue("頁情報を持たないシートへ番号を書き込まない");
        PageNumberAt(june, 0).Should().Be(8, "空の 5月を飛ばして 4月に続く");
    }

    /// <summary>
    /// 頁番号の入っていない位置の改ページ（Excel で手作業により足された等）では、番号を数えるだけで
    /// その位置のセルは書き換えない。明細行の備考欄を上書きしないため。
    /// </summary>
    [Fact]
    public void RenumberFollowingMonthSheets_BreakWithoutPageNumberCell_CountsButDoesNotOverwrite()
    {
        using var workbook = new XLWorkbook();
        CreateReportLikeSheet(workbook, "4月", firstPageNumber: 1, pageCount: 1);
        var may = CreateReportLikeSheet(workbook, "5月", firstPageNumber: 9, pageCount: 2);
        may.PageSetup.AddHorizontalPageBreak(10);          // 手作業で足された改ページ（明細行の途中）
        may.Cell(12, PageNumberColumn).Value = "備考の文字列";
        var card = CreateCard(startingPageNumber: 1);

        ReportService.RenumberFollowingMonthSheets(workbook, card, 4, PageNumberColumn);

        PageNumberAt(may, 0).Should().Be(2);
        may.Cell(12, PageNumberColumn).GetString().Should().Be("備考の文字列", "頁番号でないセルは上書きしない");
        PageNumberAt(may, 1).Should().Be(4, "改ページ数で数える規則（GetLastPageNumberFromWorksheet）と揃える");
    }

    /// <summary>
    /// 年度最終月（3月）や不正な月では何もしない。
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(13)]
    public void RenumberFollowingMonthSheets_NoFollowingMonth_DoesNothing(int month)
    {
        using var workbook = new XLWorkbook();
        var april = CreateReportLikeSheet(workbook, "4月", firstPageNumber: 40, pageCount: 1);
        var card = CreateCard(startingPageNumber: 1);

        ReportService.RenumberFollowingMonthSheets(workbook, card, month, PageNumberColumn);

        PageNumberAt(april, 0).Should().Be(40);
    }

    #endregion
}
