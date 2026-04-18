using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1273: <see cref="ToastLayoutCalculator"/> の単体テスト。
/// </summary>
public class ToastLayoutCalculatorTests
{
    #region ComputeMinWidth

    /// <summary>
    /// Medium (14pt) ではデフォルトの 360px を返す（既存動作との互換性）。
    /// </summary>
    [Fact]
    public void ComputeMinWidth_Medium_Returns360()
    {
        ToastLayoutCalculator.ComputeMinWidth(14.0).Should().Be(360);
    }

    /// <summary>
    /// 線形スケール: Small / Medium / Large / ExtraLarge の各ポイントで
    /// 仕様通りの値を返す。
    /// </summary>
    [Theory]
    [InlineData(12.0, 324)]   // Small:  360 + (12-14)*18 = 324
    [InlineData(14.0, 360)]   // Medium: 360 + 0 = 360
    [InlineData(16.0, 396)]   // Large:  360 + (16-14)*18 = 396
    [InlineData(20.0, 468)]   // ExtraLarge: 360 + (20-14)*18 = 468
    public void ComputeMinWidth_StandardFontSizes_ReturnsExpected(double baseFontSize, double expected)
    {
        ToastLayoutCalculator.ComputeMinWidth(baseFontSize).Should().Be(expected);
    }

    /// <summary>
    /// 極端に小さいフォント時は MinWidthFloor（300px）にクランプされる。
    /// </summary>
    [Fact]
    public void ComputeMinWidth_ExtremelySmallFont_ClampsToFloor()
    {
        // 6pt → 360 + (6-14)*18 = 360 - 144 = 216
        // ただし MinWidthFloor=300 でクランプされる
        ToastLayoutCalculator.ComputeMinWidth(6.0).Should().Be(ToastLayoutCalculator.MinWidthFloor);
    }

    /// <summary>
    /// 小数点以下は四捨五入される（WPF ピクセル単位との整合性のため）。
    /// </summary>
    [Fact]
    public void ComputeMinWidth_FractionalFontSize_RoundsResult()
    {
        // 15.5pt → 360 + 1.5*18 = 387 （ちょうど整数になるケース）
        ToastLayoutCalculator.ComputeMinWidth(15.5).Should().Be(387);

        // 14.3pt → 360 + 0.3*18 = 365.4 → 365 に丸められる
        ToastLayoutCalculator.ComputeMinWidth(14.3).Should().Be(365);
    }

    #endregion

    #region ComputeMaxHeight

    /// <summary>
    /// Medium (14pt) ではデフォルトの 220px を返す。
    /// </summary>
    [Fact]
    public void ComputeMaxHeight_Medium_Returns220()
    {
        ToastLayoutCalculator.ComputeMaxHeight(14.0).Should().Be(220);
    }

    /// <summary>
    /// 線形スケール: 各標準ポイントで仕様通りの値を返す。
    /// </summary>
    [Theory]
    [InlineData(12.0, 196)]   // Small:  220 + (12-14)*12 = 196
    [InlineData(14.0, 220)]   // Medium: 220
    [InlineData(16.0, 244)]   // Large:  220 + 24 = 244
    [InlineData(20.0, 292)]   // ExtraLarge: 220 + 72 = 292
    public void ComputeMaxHeight_StandardFontSizes_ReturnsExpected(double baseFontSize, double expected)
    {
        ToastLayoutCalculator.ComputeMaxHeight(baseFontSize).Should().Be(expected);
    }

    /// <summary>
    /// 極端に小さいフォント時は MaxHeightFloor（180px）にクランプされる。
    /// </summary>
    [Fact]
    public void ComputeMaxHeight_ExtremelySmallFont_ClampsToFloor()
    {
        // 6pt → 220 + (6-14)*12 = 220 - 96 = 124
        // ただし MaxHeightFloor=180 でクランプされる
        ToastLayoutCalculator.ComputeMaxHeight(6.0).Should().Be(ToastLayoutCalculator.MaxHeightFloor);
    }

    #endregion

    #region 定数の保証

    /// <summary>
    /// MaxWidth 定数の値は固定（520px）。設定画面の幅基準等、ドキュメントに記載した値と一致。
    /// </summary>
    [Fact]
    public void MaxWidth_IsFixedAt520()
    {
        ToastLayoutCalculator.MaxWidth.Should().Be(520);
    }

    /// <summary>
    /// MinWidth は MaxWidth を超えない（計算値の自然な上限として MaxWidth が機能すること）。
    /// 実装上は ComputeMinWidth にキャップはないが、XAML 側で MaxWidth が優先されるため
    /// 論理的に問題ない。本テストは仕様確認として存在。
    /// </summary>
    [Fact]
    public void ComputeMinWidth_VeryLargeFontSize_CanExceedMaxWidth()
    {
        // 50pt → 360 + 36*18 = 1008 （極端なフォントサイズ、実運用では使われない）
        // ComputeMinWidth は計算値を返すが、XAML の MaxWidth=520 で実画面は 520 に制限される
        ToastLayoutCalculator.ComputeMinWidth(50.0).Should().Be(1008);
    }

    /// <summary>
    /// MinWidth は MaxWidth よりも必ず小さい（標準的な運用レンジ 12-20pt 内で）。
    /// </summary>
    [Theory]
    [InlineData(12.0)]
    [InlineData(14.0)]
    [InlineData(16.0)]
    [InlineData(20.0)]
    public void ComputeMinWidth_StandardRange_DoesNotExceedMaxWidth(double baseFontSize)
    {
        ToastLayoutCalculator.ComputeMinWidth(baseFontSize)
            .Should().BeLessThanOrEqualTo(ToastLayoutCalculator.MaxWidth,
                $"通常の文字サイズ {baseFontSize}pt では MinWidth < MaxWidth である必要がある");
    }

    #endregion

    #region 回帰防止: Issue #1273 の具体的期待値

    /// <summary>
    /// Issue #1273 の PR 本文に記載した具体値とコードが一致することを保証する。
    /// </summary>
    [Fact]
    public void ComputeMinWidth_DocumentedValues_MatchImplementation()
    {
        // 開発者ガイド・PR 本文で Small=324 / Medium=360 / Large=396 / ExtraLarge=468 と記載
        ToastLayoutCalculator.ComputeMinWidth(12.0).Should().Be(324);
        ToastLayoutCalculator.ComputeMinWidth(14.0).Should().Be(360);
        ToastLayoutCalculator.ComputeMinWidth(16.0).Should().Be(396);
        ToastLayoutCalculator.ComputeMinWidth(20.0).Should().Be(468);
    }

    [Fact]
    public void ComputeMaxHeight_DocumentedValues_MatchImplementation()
    {
        // 開発者ガイド・PR 本文で Small=196 / Medium=220 / Large=244 / ExtraLarge=292 と記載
        ToastLayoutCalculator.ComputeMaxHeight(12.0).Should().Be(196);
        ToastLayoutCalculator.ComputeMaxHeight(14.0).Should().Be(220);
        ToastLayoutCalculator.ComputeMaxHeight(16.0).Should().Be(244);
        ToastLayoutCalculator.ComputeMaxHeight(20.0).Should().Be(292);
    }

    #endregion
}
