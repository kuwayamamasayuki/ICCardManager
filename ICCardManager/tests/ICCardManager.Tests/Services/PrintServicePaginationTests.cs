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
    private const int LandscapeMaxChars = 20;
    private const int PortraitMaxChars = 12;

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

        PrintService.GetDataRowHeight(row, isLandscape: true).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 摘要 null → 1行高さ
    /// </summary>
    [Fact]
    public void GetDataRowHeight_NullSummary_ReturnsSingleHeight()
    {
        var row = new ReportRow { Summary = null };

        PrintService.GetDataRowHeight(row, isLandscape: true).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 横向き: 20文字ちょうどは1行に収まる
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Landscape_20Chars_ReturnsSingleHeight()
    {
        var row = Row(summary: new string('あ', LandscapeMaxChars));

        PrintService.GetDataRowHeight(row, isLandscape: true).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 横向き: 21文字 → 2行高さ（境界の1文字超過）
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Landscape_21Chars_ReturnsDoubleHeight()
    {
        var row = Row(summary: new string('あ', LandscapeMaxChars + 1));

        PrintService.GetDataRowHeight(row, isLandscape: true).Should().Be(ExpectedDataRowHeightDouble);
    }

    /// <summary>
    /// 縦向き: 12文字ちょうどは1行に収まる
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Portrait_12Chars_ReturnsSingleHeight()
    {
        var row = Row(summary: new string('あ', PortraitMaxChars));

        PrintService.GetDataRowHeight(row, isLandscape: false).Should().Be(ExpectedDataRowHeight);
    }

    /// <summary>
    /// 縦向き: 13文字 → 2行高さ
    /// </summary>
    [Fact]
    public void GetDataRowHeight_Portrait_13Chars_ReturnsDoubleHeight()
    {
        var row = Row(summary: new string('あ', PortraitMaxChars + 1));

        PrintService.GetDataRowHeight(row, isLandscape: false).Should().Be(ExpectedDataRowHeightDouble);
    }

    /// <summary>
    /// 同じ15文字でも、横向きでは1行・縦向きでは2行になる（用紙方向で分岐）
    /// </summary>
    [Fact]
    public void GetDataRowHeight_OrientationDictatesWrapping()
    {
        var row = Row(summary: new string('あ', 15));

        PrintService.GetDataRowHeight(row, isLandscape: true).Should().Be(ExpectedDataRowHeight);
        PrintService.GetDataRowHeight(row, isLandscape: false).Should().Be(ExpectedDataRowHeightDouble);
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
            summaryRowCount: 0, isFirstCard: true);

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
            summaryRowCount: 2, isFirstCard: true);

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
            summaryRowCount: 2, isFirstCard: true);

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
            summaryRowCount: 2, isFirstCard: true);

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
            summaryRowCount: 0, isFirstCard: true);

        // 縦向き: 600x800（同じ面積）
        var portraitPages = PrintService.GroupRowsByPage(
            rows, pageWidth: 600, pageHeight: 800,
            summaryRowCount: 0, isFirstCard: true);

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
            summaryRowCount: 0, isFirstCard: true);

        // 合計行5個: 合計分110pt確保 → 1ページに収まる行数が減る
        var pagesManySummary = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 320,
            summaryRowCount: 5, isFirstCard: true);

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
            summaryRowCount: 0, isFirstCard: true);

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
            summaryRowCount: 1, isFirstCard: true);

        pages.SelectMany(p => p).Should().HaveCount(5);
    }

    #endregion

    #region Issue #1262: 実寸A4と端数行・合計行配置のテスト

    // A4 用紙の実寸（FlowDocument 設定値と一致: PrintService.CreateFlowDocument）
    private const double A4LandscapeWidth = 842;
    private const double A4LandscapeHeight = 595;
    private const double A4PortraitWidth = 595;
    private const double A4PortraitHeight = 842;

    /// <summary>
    /// Issue #1262: A4 横向き実寸で 30 行（短い摘要）のデータは 2 ページに分割されること。
    /// 利用可能高さ = 595-100-130 = 365pt, 短い行22pt → 1ページ16行 + 合計2行44pt。
    /// 30行なら 16+14 or 15+15 程度で 2 ページ分割する。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Landscape_30Rows_FitsInTwoPages()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Row($"行{i:D2}")).ToList();

        var pages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth,
            pageHeight: A4LandscapeHeight,
            summaryRowCount: 2, // 月計 + 累計
            isFirstCard: true);

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
            summaryRowCount: 2,
            isFirstCard: true);

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
            summaryRowCount: 2,
            isFirstCard: true);

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
            summaryRowCount: 0, isFirstCard: true);

        // summary 3行（月計+累計+繰越）を最終ページに確保
        var pagesWithSummary = PrintService.GroupRowsByPage(
            rows, pageWidth: 800, pageHeight: 300,
            summaryRowCount: 3, isFirstCard: true);

        // 両方とも20行を保持
        pagesNoSummary.SelectMany(p => p).Should().HaveCount(20);
        pagesWithSummary.SelectMany(p => p).Should().HaveCount(20);

        // summary 確保分の高さが必要な構成ではページ数が同じか多くなる
        pagesWithSummary.Count.Should().BeGreaterThanOrEqualTo(pagesNoSummary.Count,
            "summaryRowCount が増えると最終ページに必要な余白が増え、ページ分割が早まる");
    }

    /// <summary>
    /// Issue #1262: A4 縦向き実寸のページ容量。
    /// 利用可能高さ = 842-100-130 = 612pt, 短い行22pt → 1ページ27行程度 + 合計。
    /// </summary>
    [Fact]
    public void GroupRowsByPage_A4Portrait_HasLargerPerPageCapacityThanLandscape()
    {
        // 同じ30行を横向き・縦向きで分割
        var rows = Enumerable.Range(0, 30).Select(i => Row($"行{i:D2}")).ToList();

        var landscapePages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4LandscapeWidth, pageHeight: A4LandscapeHeight,
            summaryRowCount: 2, isFirstCard: true);

        var portraitPages = PrintService.GroupRowsByPage(
            rows,
            pageWidth: A4PortraitWidth, pageHeight: A4PortraitHeight,
            summaryRowCount: 2, isFirstCard: true);

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
            summaryRowCount: 0, isFirstCard: true);

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
            summaryRowCount: 100, isFirstCard: true);

        // 10行は欠落なく分割される
        pages.SelectMany(p => p).Should().HaveCount(10);
        // 各ページに少なくとも1行は入る
        pages.Should().OnlyContain(p => p.Count > 0);
    }

    #endregion
}
