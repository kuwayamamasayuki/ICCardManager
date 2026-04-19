using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1274: <see cref="LendingStatusPresenter"/> の単体テスト。
/// </summary>
public class LendingStatusPresenterTests
{
    #region Resolve - 状態分岐

    /// <summary>
    /// 在庫（利用可）状態: Icon/ShortText/AccessibilityText が仕様どおり。
    /// </summary>
    [Fact]
    public void Resolve_Available_ReturnsAvailablePresentation()
    {
        var result = LendingStatusPresenter.Resolve(isLent: false, isRefunded: false);

        result.Status.Should().Be(LendingStatus.Available);
        result.Icon.Should().Be(LendingStatusPresenter.AvailableIcon);
        result.Icon.Should().Be("📥");
        result.ShortText.Should().Be("在庫");
        result.AccessibilityText.Should().Be("利用可能な在庫カードです");
    }

    /// <summary>
    /// 貸出中（貸出者名なし）: 汎用テキスト。
    /// </summary>
    [Fact]
    public void Resolve_LentWithoutStaff_ReturnsGenericLentText()
    {
        var result = LendingStatusPresenter.Resolve(isLent: true, isRefunded: false, lentStaffName: null);

        result.Status.Should().Be(LendingStatus.Lent);
        result.Icon.Should().Be("📤");
        result.ShortText.Should().Be("貸出中");
        result.AccessibilityText.Should().Be("貸出中のカードです");
    }

    /// <summary>
    /// 貸出中（貸出者名あり）: ShortText とアクセシビリティ両方に名前が含まれる。
    /// </summary>
    [Fact]
    public void Resolve_LentWithStaff_IncludesStaffNameInBothTexts()
    {
        var result = LendingStatusPresenter.Resolve(isLent: true, isRefunded: false, lentStaffName: "山田太郎");

        result.Status.Should().Be(LendingStatus.Lent);
        result.Icon.Should().Be("📤");
        result.ShortText.Should().Be("貸出中（山田太郎）");
        result.AccessibilityText.Should().Be("山田太郎 さんに貸出中のカードです");
    }

    /// <summary>
    /// 貸出中（貸出者名が空文字）: null と同様に汎用テキストを使用。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_LentWithEmptyStaff_FallsBackToGenericText(string emptyStaff)
    {
        var result = LendingStatusPresenter.Resolve(isLent: true, isRefunded: false, lentStaffName: emptyStaff);

        result.ShortText.Should().Be("貸出中");
        result.AccessibilityText.Should().Be("貸出中のカードです");
    }

    /// <summary>
    /// 払戻済: 他の状態より優先。
    /// </summary>
    [Fact]
    public void Resolve_Refunded_ReturnsRefundedPresentation()
    {
        var result = LendingStatusPresenter.Resolve(isLent: false, isRefunded: true);

        result.Status.Should().Be(LendingStatus.Refunded);
        result.Icon.Should().Be(LendingStatusPresenter.RefundedIcon);
        result.Icon.Should().Be("🚫");
        result.ShortText.Should().Be("払戻済");
        result.AccessibilityText.Should().Be("払戻済のカードです");
    }

    /// <summary>
    /// 払戻済は貸出中より優先される（Issue #530 の既存仕様との整合）。
    /// </summary>
    [Fact]
    public void Resolve_RefundedAndLent_PrioritizesRefunded()
    {
        var result = LendingStatusPresenter.Resolve(isLent: true, isRefunded: true, lentStaffName: "山田");

        result.Status.Should().Be(LendingStatus.Refunded);
        result.ShortText.Should().Be("払戻済");
    }

    #endregion

    #region FormatWithIcon

    [Theory]
    [InlineData(false, false, null, "📥 在庫")]
    [InlineData(true, false, null, "📤 貸出中")]
    [InlineData(true, false, "佐藤", "📤 貸出中（佐藤）")]
    [InlineData(false, true, null, "🚫 払戻済")]
    public void FormatWithIcon_AllStates_ProducesExpectedOutput(
        bool isLent, bool isRefunded, string lentStaffName, string expected)
    {
        var presentation = LendingStatusPresenter.Resolve(isLent, isRefunded, lentStaffName);
        LendingStatusPresenter.FormatWithIcon(presentation).Should().Be(expected);
    }

    [Fact]
    public void FormatWithIcon_Null_ThrowsArgumentNullException()
    {
        var act = () => LendingStatusPresenter.FormatWithIcon(null);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region LendingStatusPresentation コンストラクタ

    [Fact]
    public void Constructor_AllValidArgs_PropertiesSet()
    {
        var p = new LendingStatusPresentation(
            LendingStatus.Lent, "I", "S", "A");

        p.Status.Should().Be(LendingStatus.Lent);
        p.Icon.Should().Be("I");
        p.ShortText.Should().Be("S");
        p.AccessibilityText.Should().Be("A");
    }

    [Theory]
    [InlineData(null, "S", "A")]
    [InlineData("I", null, "A")]
    [InlineData("I", "S", null)]
    public void Constructor_NullString_ThrowsArgumentNullException(
        string icon, string shortText, string accessibility)
    {
        var act = () => new LendingStatusPresentation(
            LendingStatus.Available, icon, shortText, accessibility);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region 定数

    /// <summary>
    /// Issue #1274: アイコン定数値が想定どおり（XAML 表示やドキュメントと一致）。
    /// </summary>
    [Fact]
    public void IconConstants_HaveExpectedValues()
    {
        LendingStatusPresenter.LentIcon.Should().Be("📤");
        LendingStatusPresenter.AvailableIcon.Should().Be("📥");
        LendingStatusPresenter.RefundedIcon.Should().Be("🚫");
    }

    #endregion

    #region DTO連携の回帰保証

    /// <summary>
    /// <see cref="ICCardManager.Dtos.CardBalanceDashboardItem"/> が
    /// Presenter 経由で表示用プロパティを提供していること。
    /// </summary>
    [Fact]
    public void CardBalanceDashboardItem_Lent_UsesPresenter()
    {
        var item = new ICCardManager.Dtos.CardBalanceDashboardItem
        {
            IsLent = true,
            LentStaffName = "田中"
        };

        item.LentStatusIcon.Should().Be("📤");
        item.LentStatusDisplay.Should().Be("貸出中", "短ラベルには貸出者名を含めない（既存仕様との互換性）");
        item.LentInfoDisplay.Should().Be("貸出中（田中）", "情報表示には貸出者名を含める");
        item.LentStatusAccessibilityText.Should().Be("田中 さんに貸出中のカードです");
    }

    [Fact]
    public void CardBalanceDashboardItem_Available_UsesPresenter()
    {
        var item = new ICCardManager.Dtos.CardBalanceDashboardItem
        {
            IsLent = false
        };

        item.LentStatusIcon.Should().Be("📥");
        item.LentStatusDisplay.Should().Be("在庫");
        item.LentStatusAccessibilityText.Should().Be("利用可能な在庫カードです");
    }

    /// <summary>
    /// <see cref="ICCardManager.Dtos.CardDto"/> が Presenter 経由で3状態を正しく提供。
    /// </summary>
    [Theory]
    [InlineData(false, false, null, "📥", "在庫", "利用可能な在庫カードです")]
    [InlineData(true, false, "佐藤", "📤", "貸出中（佐藤）", "佐藤 さんに貸出中のカードです")]
    [InlineData(false, true, null, "🚫", "払戻済", "払戻済のカードです")]
    [InlineData(true, true, "佐藤", "🚫", "払戻済", "払戻済のカードです")] // 払戻済優先
    public void CardDto_AllStates_UsesPresenter(
        bool isLent, bool isRefunded, string lentStaffName,
        string expectedIcon, string expectedDisplay, string expectedA11y)
    {
        var dto = new ICCardManager.Dtos.CardDto
        {
            IsLent = isLent,
            IsRefunded = isRefunded,
            LentStaffName = lentStaffName
        };

        dto.LentStatusIcon.Should().Be(expectedIcon);
        dto.LentStatusDisplay.Should().Be(expectedDisplay);
        dto.LentStatusAccessibilityText.Should().Be(expectedA11y);
    }

    #endregion
}
