using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// PrintService の internal static ページネーション計算メソッドの単体テスト。
/// PR #1192 で internal static 化された 4 メソッドを直接検証する。
/// </summary>
/// <remarks>
/// 既存の PrintServiceTests は <c>GetReportDataAsync</c>（データ層）のみをカバーしており、
/// FlowDocument 描画前のページ分割計算は完全に未テストだった。
/// 本テストはレイアウト定数（ヘッダー高さ、行高さ、余白）とページネーション計算の
/// 振る舞いを直接固定する。
/// </remarks>
public class PrintServicePaginationTests
{
    // PrintService 内の private const と整合する値
    // （変更時はテストも同期更新が必要）
    private const double ExpectedHeaderTotalHeight = 45 + 58 + 25 + 2;  // 130
    private const double ExpectedPagePadding = 50;
    private const double ExpectedDataRowHeight = 22;
    private const double ExpectedDataRowHeightDouble = 38;
    private const double ExpectedSummaryRowHeight = 22;
    private const double ExpectedTripleRowHeight = 54;   // 22 + 16×2（折り返し1行ごとに +16）

    // A4 の DIP（1/96 インチ）寸法。210mm / 25.4 × 96 ≈ 793.7、297mm / 25.4 × 96 ≈ 1122.5
    // Issue #2047: 以前はポイント値（595×842）を「A4 実寸」として使っていた
    private const double A4LongEdgeDip = 1122.5;
    private const double A4ShortEdgeDip = 793.7;

    // A4 実寸での摘要欄の 1 行あたり全角文字数の見積もり（DIP）
    // 横: (1122.5 - 余白100 - 外枠2) × 3.3/11.2 - セル内余白9 ≈ 291.7、禁則の余裕 1 文字(11)を引いて 280.7 → 25 文字
    // 縦: (793.7 - 100 - 2) × 3.3/11.2 - 9 ≈ 194.8、- 11 = 183.8 → 16 文字
    private const int LandscapeSummaryMaxChars = 25;
    private const int PortraitSummaryMaxChars = 16;

    private static ReportRow Row(string summary = "", ReportRowType type = ReportRowType.Data) =>
        new ReportRow { Summary = summary, RowType = type };

    #region GetHeaderTotalHeight

    /// <summary>
    /// ヘッダー合計高さ = タイトル(45) + カード情報(58) + 列ヘッダー(25) + 罫線(2) = 130
    /// </summary>
    [Fact]
    public void GetHeaderTotalHeight_ReturnsExpectedSum()
    {
        PrintService.GetHeaderTotalHeight().Should().Be(ExpectedHeaderTotalHeight);
    }

    #endregion

    #region GetAvailableDataHeight

    /// <summary>
    /// 利用可能高さ = ページ高さ - 上下余白(100) - ヘッダー高さ(130)
    /// </summary>
    [Fact]
    public void GetAvailableDataHeight_SubtractsMarginsAndHeader()
    {
        const double pageHeight = 600;
        var expected = pageHeight - (ExpectedPagePadding * 2) - ExpectedHeaderTotalHeight; // 600 - 100 - 130 = 370

        PrintService.GetAvailableDataHeight(pageHeight).Should().Be(expected);
    }

    /// <summary>
    /// ページ高さがヘッダー＋余白より小さい場合、戻り値は負になる（境界条件の動作固定）
    /// </summary>
    [Fact]
    public void GetAvailableDataHeight_TinyPage_CanReturnNegative()
    {
        // 200 - 100 - 130 = -30
        PrintService.GetAvailableDataHeight(200).Should().Be(-30);
    }

    #endregion

    #region GetDataRowHeight

    /// <summary>
    /// 摘要が空 → 1行高さ
    /// </summary>
    [Fact]
    public void GetDataRowHeight_EmptySummary_ReturnsSingleHeight()
    {
        var row = Row(summary: "");

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 摘要 null → 1行高さ
    /// </summary>
    [Fact]
    public void GetDataRowHeight_NullSummary_ReturnsSingleHeight()
    {
        var row = new ReportRow { Summary = null };

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 横向き（A4 実寸）: 摘要欄の上限ちょうどは1行に収まる
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Landscape_MaxChars_ReturnsSingleHeight()
    {
        var row = Row(summary: new string('あ', LandscapeSummaryMaxChars));

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 横向き（A4 実寸）: 上限＋1文字 → 2行高さ（境界の1文字超過）
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Landscape_MaxCharsPlusOne_ReturnsDoubleHeight()
    {
        var row = Row(summary: new string('あ', LandscapeSummaryMaxChars + 1));

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedDataRowHeightDouble);
    }

    /// <summary>
    /// 縦向き（A4 実寸）: 摘要欄の上限ちょうどは1行に収まる
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Portrait_MaxChars_ReturnsSingleHeight()
    {
        var row = Row(summary: new string('あ', PortraitSummaryMaxChars));

        PrintService.GetDataRowHeight(row, A4ShortEdgeDip).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 縦向き（A4 実寸）: 上限＋1文字 → 2行高さ
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Portrait_MaxCharsPlusOne_ReturnsDoubleHeight()
    {
        var row = Row(summary: new string('あ', PortraitSummaryMaxChars + 1));

        PrintService.GetDataRowHeight(row, A4ShortEdgeDip).Should().Be(ExpectedDataRowHeightDouble);
    }

    /// <summary>
    /// Issue #2047（コードレビュー）: 見積もりは禁則処理で行末の文字が送られる分として各行 1 文字の余裕を取る。
    /// 実際の文字幅（横 291.7 ＝ 26 文字）にちょうど収まる摘要も 2 行分として数える
    /// </summary>
    [Fact]
    public void GetDataRowHeight_ReservesOneCharPerLineForLineBreakingRules()
    {
        var row = Row(summary: new string('あ', 26));

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedDataRowHeightDouble);
    }

    /// <summary>
    /// 同じ20文字でも、横向きでは1行・縦向きでは2行になる（ページ幅で分岐）
    /// </summary>
    [Fact]
    public void GetDataRowHeight_PageWidthDictatesWrapping()
    {
        var row = Row(summary: new string('あ', 20));

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedDataRowHeight);
        PrintService.GetDataRowHeight(row, A4ShortEdgeDip).Should().Be(ExpectedDataRowHeightDouble);
    }

    /// <summary>
    /// Issue #2047: 摘要が2行分の上限を超える長さなら、3行分として数える
    /// （旧実装は「1行 or 2行」しか返さなかった）
    /// </summary>
    [Fact]
    public void GetDataRowHeight_SummaryBeyondTwoLines_ReturnsTripleHeight()
    {
        var row = Row(summary: new string('あ', (LandscapeSummaryMaxChars * 2) + 1));

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedTripleRowHeight);
    }

    /// <summary>
    /// Issue #2047: 残高不足時の備考は、摘要が短くても備考欄で3行に折り返すため3行分として数える
    /// （旧実装は摘要の文字数しか見ず1行分としていた）
    /// </summary>
    [Fact]
    public void GetDataRowHeight_LongNoteWithShortSummary_CountsNoteWrapping()
    {
        var row = new ReportRow
        {
            Summary = "鉄道（博多～天神）",
            StaffName = "博多 花子",
            Note = "支払額210円のうち不足額140円は現金で支払（旅費支給）",
        };

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedTripleRowHeight);
    }

    /// <summary>
    /// Issue #2047: 氏名欄は狭いため、縦向きでは「外N名」付きの氏名が折り返す。
    /// 横向きでは同じ氏名が1行に収まる（折り返しを常に数える実装を検出する対の表明）
    /// </summary>
    [Fact]
    public void GetDataRowHeight_StaffNameWrapsOnlyInNarrowColumn()
    {
        var row = new ReportRow { Summary = "鉄道（博多～天神）", StaffName = "博多 花子 外１名" };

        PrintService.GetDataRowHeight(row, A4LongEdgeDip).Should().Be(ExpectedDataRowHeight);
        PrintService.GetDataRowHeight(row, A4ShortEdgeDip).Should().Be(ExpectedDataRowHeightDouble);
    }

    #endregion

    #region Issue #2047: ページ寸法・列幅・折り返し行数

    /// <summary>
    /// Issue #2047: A4 のページ寸法は DIP（1/96 インチ）で返す。ポイント値（842×595）ではない
    /// </summary>
    [Fact]
    public void GetPageSize_ReturnsA4InDip()
    {
        var landscape = PrintService.GetPageSize(System.Printing.PageOrientation.Landscape);
        var portrait = PrintService.GetPageSize(System.Printing.PageOrientation.Portrait);

        landscape.Width.Should().BeApproximately(A4LongEdgeDip, 0.1);
        landscape.Height.Should().BeApproximately(A4ShortEdgeDip, 0.1);
        portrait.Width.Should().BeApproximately(A4ShortEdgeDip, 0.1);
        portrait.Height.Should().BeApproximately(A4LongEdgeDip, 0.1);
    }

    /// <summary>
    /// 列ごとの文字幅は、テーブル幅（ページ幅 − 余白100 − 外枠2）を Star 比率で配分し、
    /// セル内余白（段落マージン8 ＋ 罫線1）を引いた値。全列を足し戻すとテーブル幅になる
    /// </summary>
    [Fact]
    public void GetColumnTextWidth_AllColumnsSumToTableWidth()
    {
        const double pageWidth = 1000;
        const int columnCount = 7;
        const double cellInset = 9;

        var total = Enumerable.Range(0, columnCount)
            .Sum(i => PrintService.GetColumnTextWidth(pageWidth, i) + cellInset);

        total.Should().BeApproximately(pageWidth - 100 - 2, 0.001);
        // 摘要欄（3.3/11.2）は備考欄（1.7/11.2）より広い
        PrintService.GetColumnTextWidth(pageWidth, 1)
            .Should().BeGreaterThan(PrintService.GetColumnTextWidth(pageWidth, 6));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("あいうえお", 1)]      // 55 ≤ 60
    [InlineData("あいうえおか", 2)]    // 66 > 60
    [InlineData("あ\nい", 2)]          // 改行は強制改行
    [InlineData("あ\r\nい", 2)]        // CRLF も 1 回の改行
    [InlineData("1234567890", 2)]      // 半角 6.6 × 10 = 66 > 60
    [InlineData("123456789", 1)]       // 半角 6.6 × 9 = 59.4 ≤ 60
    public void EstimateLineCount_WrapsByWidth(string text, int expected)
    {
        // 幅 60 DIP ＝ 全角 5 文字（11 × 5 = 55）＋ 端数
        PrintService.EstimateLineCount(text, textWidth: 60).Should().Be(expected);
    }

    /// <summary>
    /// 1 文字も置けない幅でも無限に改行せず、1 文字 1 行として数える
    /// </summary>
    [Fact]
    public void EstimateLineCount_WidthNarrowerThanOneChar_OneLinePerChar()
    {
        PrintService.EstimateLineCount("あいう", textWidth: 5).Should().Be(3);
    }

    #endregion

    #region GroupRowsByPage

    /// <summary>
    /// 空リスト → 空のページリスト
    /// </summary>
    [Fact]
    public void GroupRowsByPage_EmptyRows_ReturnsEmpty()
    {
        var pages = PrintService.GroupRowsByPage(
            new List<ReportRow>(),
            pageWidth: 800, pageHeight: 600,
            summaryRowCount: 0);

        pages.Should().BeEmpty();
    }

    /// <summary>
    /// 利用可能高さに余裕があり、全行＋合計行が1ページに収まる → 1ページに集約
    /// </summary>
    [Fact]
    public void GroupRowsByPage_AllRowsFitInOnePage_ReturnsSinglePage()
    {
        // pageHeight=600 → available=370。 行5件×22 + 合計2件×22 = 154 → 1ページ
        var rows = Enumerable.Range(0, 5).Select(i => Row($"行{i}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 600,
            summaryRowCount: 2);

        pages.Should().HaveCount(1);
        pages[0].Should().HaveCount(5);
    }

    /// <summary>
    /// 行数が多くて1ページに収まらない場合、2ページ以上に分割される
    /// </summary>
    [Fact]
    public void GroupRowsByPage_TooManyRows_SplitsIntoMultiplePages()
    {
        // pageHeight=400 → available=170。 1行22pt → 1ページに7行ちょっと入る計算
        // 30行入れて確実に複数ページにする
        var rows = Enumerable.Range(0, 30).Select(i => Row($"行{i}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 400,
            summaryRowCount: 2);

        pages.Count.Should().BeGreaterThan(1);
        // すべての行が漏れなく含まれること
        pages.SelectMany(p => p).Should().HaveCount(30);
        // 各ページに少なくとも1行は入る
        pages.Should().OnlyContain(p => p.Count > 0);
    }

    /// <summary>
    /// 分割された各ページの行は元の順序を保つ
    /// </summary>
    [Fact]
    public void GroupRowsByPage_PreservesOriginalOrder()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 400,
            summaryRowCount: 2);

        var flattened = pages.SelectMany(p => p).Select(r => r.Summary).ToList();
        flattened.Should().Equal(rows.Select(r => r.Summary));
    }

    /// <summary>
    /// 横向きでは収まる文字列が、縦向きでは折り返して2倍の高さになる →
    /// ページ分割数が増える
    /// </summary>
    [Fact]
    public void GroupRowsByPage_PortraitMayProduceMorePagesThanLandscape()
    {
        // 15文字: 横向きは1行、縦向きは2行
        var rows = Enumerable.Range(0, 12)
            .Select(_ => Row(new string('あ', 15)))
            .ToList();

        // 横向き: 800x600
        var landscapePages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 600,
            summaryRowCount: 0);

        // 縦向き: 600x800（同じ面積）
        var portraitPages = PrintService.GroupRowsByPage(
            rows, pageWidth: 600, pageHeight: 800,
            summaryRowCount: 0);

        // 縦向きの方が行高さが大きいため、ページ数は同じか多くなる
        portraitPages.Count.Should().BeGreaterThanOrEqualTo(landscapePages.Count);
        // 両方とも全行を保持
        landscapePages.SelectMany(p => p).Should().HaveCount(12);
        portraitPages.SelectMany(p => p).Should().HaveCount(12);
    }

    /// <summary>
    /// 合計行数（summaryRowCount）が増えると、最終ページに必要な余白が増えて
    /// 場合によってはページ分割が早まる
    /// </summary>
    [Fact]
    public void GroupRowsByPage_HigherSummaryRowCount_CanForceEarlierBreak()
    {
        // ちょうど境界の高さに調整: 行7件でほぼ満杯になるケース
        // pageHeight=294 → available=64 → 行2行+合計0行で収まる、
        //                  合計を増やすと収まらなくなるケースを構築
        var rows = Enumerable.Range(0, 4).Select(i => Row($"行{i}")).ToList();

        // 合計行0個: 全4行が1ページに
        var pagesNoSummary = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 320,
            summaryRowCount: 0);

        // 合計行5個: 合計分110pt確保 → 1ページに収まる行数が減る
        var pagesManySummary = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 320,
            summaryRowCount: 5);

        // 全行は両方で保持
        pagesNoSummary.SelectMany(p => p).Should().HaveCount(4);
        pagesManySummary.SelectMany(p => p).Should().HaveCount(4);
        // 合計行が多い方が、ページ数は同じか多い
        pagesManySummary.Count.Should().BeGreaterThanOrEqualTo(pagesNoSummary.Count);
    }

    /// <summary>
    /// 1行だけの場合は必ず1ページに入る（空でない最終ページが返る）
    /// </summary>
    [Fact]
    public void GroupRowsByPage_SingleRow_AlwaysFits()
    {
        var rows = new List<ReportRow> { Row("単一行") };

        var pages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 600,
            summaryRowCount: 0);

        pages.Should().HaveCount(1);
        pages[0].Should().ContainSingle();
    }

    /// <summary>
    /// 縦長の摘要（折り返し2行）が混在する場合も、行は欠落せず分割される
    /// </summary>
    [Fact]
    public void GroupRowsByPage_MixedRowHeights_NoRowsLost()
    {
        var rows = new List<ReportRow>
        {
            Row("短"),
            Row(new string('あ', 25)), // 横向きでも折り返す
            Row("短"),
            Row(new string('あ', 25)),
            Row("短"),
        };

        var pages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 400,
            summaryRowCount: 1);

        pages.SelectMany(p => p).Should().HaveCount(5);
    }

    #endregion

    #region Issue #1262: 実寸A4と端数行・合計行配置のテスト

    // A4 用紙の実寸（DIP）。Issue #2047 でポイント値（842×595）から是正
    private const double A4LandscapeWidth = A4LongEdgeDip;
    private const double A4LandscapeHeight = A4ShortEdgeDip;
    private const double A4PortraitWidth = A4ShortEdgeDip;
    private const double A4PortraitHeight = A4LongEdgeDip;

    /// <summary>
    /// Issue #1262: A4 横向き実寸で 30 行（短い摘要）のデータは 2 ページに分割されること。
    /// 利用可能高さ = 793.7-100-130 = 563.7, 短い行22 → 1ページ25行。
    /// 30行なら 25+5 で 2 ページ分割する。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Landscape_30Rows_FitsInTwoPages()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 2); // 月計 + 累計

        pages.Count.Should().Be(2,
            "A4横向きで30行（短摘要）は2ページに分割される");
        pages.SelectMany(p => p).Should().HaveCount(30);
        pages.Should().OnlyContain(p => p.Count > 0);
    }

    /// <summary>
    /// Issue #1262: A4 横向き実寸で 45 行の場合も全行が分割・保持されること。
    /// ページ数は pagination 実装に依存するが、行の欠落・重複がないことが契約。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Landscape_45Rows_PreservesAllRowsAcrossPages()
    {
        var rows = Enumerable.Range(0, 45).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 2);

        // 45行が複数ページに分割される
        pages.Count.Should().BeGreaterThan(1);
        // 合計45行が過不足なく保持
        pages.SelectMany(p => p).Should().HaveCount(45);
        // 行順序が保持される
        pages.SelectMany(p => p).Select(r => r.Summary)
            .Should().Equal(rows.Select(r => r.Summary));
    }

    /// <summary>
    /// Issue #1262: 最終ページの端数行処理。
    /// n ページ目より少ないデータしかない場合、最終ページは空にならず、
    /// 残り行がすべて末尾ページに収まる。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_LastPage_ContainsRemainderNotEmpty()
    {
        // 中程度のページ高でページあたり約7行入るケース。行25で複数ページに跨る。
        var rows = Enumerable.Range(0, 25).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: 800, pageHeight: 400,
            summaryRowCount: 2);

        pages.Count.Should().BeGreaterThan(1);
        // 最終ページは空であってはならない
        pages.Last().Should().NotBeEmpty("最終ページに端数行が配置される");
        // 最終ページの行数は最大容量以下（オーバーフローしない）
        pages.Last().Count.Should().BeLessThanOrEqualTo(pages.First().Count,
            "最終ページは1ページ目以下の行数（端数になる）");
        // 最終ページの行が入力末尾と一致
        pages.Last().Last().Summary.Should().Be(rows.Last().Summary,
            "入力末尾の行は最終ページ末尾に配置される");
    }

    /// <summary>
    /// Issue #1262: ページオーバーフロー時、合計行（月計・累計・繰越）は
    /// データ行の最終ページに一緒に配置される意図で、GroupRowsByPage の
    /// summaryRowCount 分の余白は必ず最終ページに確保される。
    /// </summary>
    /// <remarks>
    /// 合計行自体は GroupRowsByPage の戻り値に含まれず CreateFlowDocument 側で
    /// 最終ページに描画される。本テストは「最終ページが summary 用スペース確保で
    /// 行数を抑えられる」ことを検証する（つまり summary が次ページに押し出されない）。
    /// </remarks>
    [Fact]
    public void GroupRowsByPage_LastPage_ReservesSpaceForSummaryRows()
    {
        // 境界構成: ページ容量ギリギリの行数を用意し、summary スペースの
        // 有無で最終ページ行数が減ることを検証する
        var rows = Enumerable.Range(0, 20).Select(i => Row($"行{i:D2}")).ToList();

        // summary なし（月計なし相当）
        var pagesNoSummary = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 300,
            summaryRowCount: 0);

        // summary 3行（月計+累計+繰越）を最終ページに確保
        var pagesWithSummary = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 300,
            summaryRowCount: 3);

        // 両方とも20行を保持
        pagesNoSummary.SelectMany(p => p).Should().HaveCount(20);
        pagesWithSummary.SelectMany(p => p).Should().HaveCount(20);

        // summary 確保分の高さが必要な構成ではページ数が同じか多くなる
        pagesWithSummary.Count.Should().BeGreaterThanOrEqualTo(pagesNoSummary.Count,
            "summaryRowCount が増えると最終ページに必要な余白が増え、ページ分割が早まる");
    }

    /// <summary>
    /// Issue #1262: A4 縦向き実寸のページ容量。
    /// 利用可能高さ = 1122.5-100-130 = 892.5, 短い行22 → 1ページ40行程度 + 合計。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Portrait_HasLargerPerPageCapacityThanLandscape()
    {
        // 同じ30行を横向き・縦向きで分割
        var rows = Enumerable.Range(0, 30).Select(i => Row($"行{i:D2}")).ToList();

        var landscapePages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth, pageHeight: A4LandscapeHeight,
            summaryRowCount: 2);

        var portraitPages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4PortraitWidth, pageHeight: A4PortraitHeight,
            summaryRowCount: 2);

        // 縦向きは1ページあたり行数が多いため、ページ数は少ないか同じ
        portraitPages.Count.Should().BeLessThanOrEqualTo(landscapePages.Count,
            "A4縦向きはデータ領域が縦に長いため、1ページあたりの行数が多く、ページ数が少なくなる");
        // 両方とも全行保持
        landscapePages.SelectMany(p => p).Should().HaveCount(30);
        portraitPages.SelectMany(p => p).Should().HaveCount(30);
    }

    /// <summary>
    /// Issue #1262: 各ページのヘッダ（月/年/カード名）は毎ページ表示される設計上、
    /// ページネーションでは各ページで一定のヘッダ高（GetHeaderTotalHeight）が
    /// 消費されるため、利用可能行高が「ヘッダ分だけ減る」。
    /// </summary>
    /// <remarks>
    /// 各ページ頭のヘッダ表示そのものは FlowDocument / WPF 層の構造確認が必要で
    /// 単体テスト範囲外だが、「ページネーションがヘッダ高を必ず差し引いている」
    /// ことは GetAvailableDataHeight 経由で検証できる。
    /// </remarks>
    [Fact]
    public void GroupRowsByPage_EveryPage_ReservesSpaceForHeader()
    {
        // ページ高 = ヘッダ＋余白と同じにすると、データ領域は ほぼ 0 になる
        // GetHeaderTotalHeight=130, PagePadding=100 → 230pt がヘッダ+余白
        var nearZeroDataHeight = 230.0 + 22.0;  // データ1行だけ入るサイズ

        var rows = Enumerable.Range(0, 5).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: nearZeroDataHeight,
            summaryRowCount: 0);

        // 1ページあたり1行しか入らないため、5行なら5ページに分割される
        pages.Should().HaveCount(5,
            "ヘッダと余白で利用可能領域が1行分しかないと、5行は5ページに分割される");
        // 各ページ1行ずつ
        pages.Should().OnlyContain(p => p.Count == 1);
    }

    /// <summary>
    /// Issue #1262: summary 行数が用紙容量を超える極端なケースでも、
    /// データ行は欠落せず正しく分割される（防御的チェック）。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_ExcessiveSummaryCount_DoesNotLoseDataRows()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Row($"行{i}")).ToList();

        // summaryRowCount=100 → summary 分だけで 2200pt 必要（ページ容量超過）
        var pages = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 600,
            summaryRowCount: 100);

        // 10行は欠落なく分割される
        pages.SelectMany(p => p).Should().HaveCount(10);
        // 各ページに少なくとも1行は入る
        pages.Should().OnlyContain(p => p.Count > 0);
    }

    #endregion

    #region Issue #1810: 最終ページの合計行スペース確保

    /// <summary>
    /// Issue #1810: A4横向き実寸で25行（短摘要）＋合計1行のとき、本文だけなら
    /// 1ページに収まる（25×22=550 ≤ 563.7）が合計行を含めると収まらない
    /// （550+22=572 > 563.7）。最終行の判定で合計行の高さが常に予約され、
    /// 2ページに分割されること。（Issue #2047 で寸法を DIP へ是正し行数を更新）
    /// </summary>
    /// <remarks>
    /// 修正前は canFitAll=false → spaceForSummary=0 となり1ページに確定し、
    /// CreateFlowDocument が pageGroups.Count &lt;= 1 と判定 → FlowDocument の
    /// 自動送りで月計・累計行だけがタイトル・列ヘッダーのない次ページに孤立していた。
    /// </remarks>
    [Fact]
    public void GroupRowsByPage_A4Landscape_25RowsWithOneSummary_SplitsIntoTwoPages()
    {
        var rows = Enumerable.Range(0, 25).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 1); // 月計のみ

        pages.Should().HaveCount(2,
            "本文25行は収まるが合計行が収まらないため、最終行を合計行と一緒に次ページへ送る");
        pages[0].Should().HaveCount(24);
        pages[1].Should().HaveCount(1);
        pages.SelectMany(p => p).Select(r => r.Summary)
            .Should().Equal(rows.Select(r => r.Summary));
    }

    /// <summary>
    /// Issue #1810: 合計行が2行（月計＋累計）の場合は24行でも溢れる
    /// （24×22=528, 528+44=572 > 563.7）。最終行が合計行と一緒に次ページへ送られること。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Landscape_24RowsWithTwoSummaries_SplitsIntoTwoPages()
    {
        var rows = Enumerable.Range(0, 24).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 2); // 月計 + 累計

        pages.Should().HaveCount(2,
            "本文24行＋合計2行は1ページに収まらないため2ページに分割される");
        pages[0].Should().HaveCount(23);
        pages[1].Should().HaveCount(1);
    }

    /// <summary>
    /// Issue #1810: 本文＋合計行がちょうど収まる場合（24×22+22=550 ≤ 563.7）は
    /// 従来どおり1ページに収まること（過剰分割しない）。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Landscape_24RowsWithOneSummary_StaysSinglePage()
    {
        var rows = Enumerable.Range(0, 24).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 1);

        pages.Should().HaveCount(1, "本文24行＋合計1行=550 は 563.7 に収まる");
        pages[0].Should().HaveCount(24);
    }

    #endregion

    #region Issue #2047: 備考欄の折り返しを含めた改ページ

    /// <summary>
    /// Issue #2047: 残高不足の備考（備考欄で3行）を持つ行が続くと、見積もり上も3行分の高さを積む。
    /// 11行×54=594 > 563.7 のため2ページ（10行＋1行）に分割される。
    /// 旧実装は摘要の文字数しか見ず1行（22）として数え、1ページに収まると判定していた
    /// （実際の印字では FlowDocument が自動改ページし、月計が見出しの無いページへ回る）。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Landscape_RowsWithLongNote_SplitsByNoteHeight()
    {
        var rows = Enumerable.Range(0, 11)
            .Select(i => new ReportRow
            {
                Summary = $"鉄道（博多～天神）{i:D2}",
                Note = "支払額210円のうち不足額140円は現金で支払（旅費支給）",
            })
            .ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 1);

        pages.Should().HaveCount(2);
        pages[0].Should().HaveCount(10);
        pages[1].Should().HaveCount(1);
    }

    /// <summary>
    /// 対の表明: 同じ11行でも備考が短ければ1ページに収まる（備考の有無で分割を常に増やす実装を検出する）
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Landscape_RowsWithShortNote_StaysSinglePage()
    {
        var rows = Enumerable.Range(0, 11)
            .Select(i => new ReportRow
            {
                Summary = $"鉄道（博多～天神）{i:D2}",
                Note = "旅費支給",
            })
            .ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 1);

        pages.Should().HaveCount(1);
    }

    #endregion
}
