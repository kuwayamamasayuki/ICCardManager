using System.Globalization;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Common.Validation;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// 同行者数入力の自動クローズ秒数の入力時検証（Issue #2009、コードレビュー是正）。
/// </summary>
/// <remarks>
/// 「入力中のフィードバック」と「保存時の判定」が同じ範囲を見ていることを表明する。
/// 食い違うと、職員には入力中は妥当に見えるのに保存だけが弾かれる。
/// </remarks>
public class CompanionCountTimeoutValidationRuleTests
{
    private readonly CompanionCountTimeoutValidationRule _rule = new();
    private readonly ValidationService _validationService = new();

    private bool RuleAccepts(string text)
        => _rule.Validate(text, CultureInfo.InvariantCulture).IsValid;

    [Theory]
    [InlineData("0")]    // 自動的に閉じない
    [InlineData("5")]    // 下限
    [InlineData("30")]   // 既定
    [InlineData("300")]  // 上限
    public void 範囲内は入力時に妥当とすること(string text)
    {
        RuleAccepts(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    [InlineData("301")]
    [InlineData("-1")]
    public void 範囲外は入力時に弾くこと(string text)
    {
        // NumericRangeValidationRule(Min=0, Max=300) では 1〜4 を通してしまう
        RuleAccepts(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("３０")]  // 全角
    [InlineData("")]
    public void 数値として読めない入力を弾くこと(string text)
    {
        var result = _rule.Validate(text, CultureInfo.InvariantCulture);

        result.IsValid.Should().BeFalse();
        result.ErrorContent.ToString().Should().EndWith("してください。", "行動指示で終わる（#1275）");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(-1)]
    public void 入力時の判定と保存時の判定が一致すること(int seconds)
    {
        // 手段が 2 通りある限り、次に範囲を変える人が片方を取りこぼす（#1763）
        RuleAccepts(seconds.ToString(CultureInfo.InvariantCulture))
            .Should().Be(_validationService.ValidateCompanionCountInputTimeout(seconds).IsValid);
    }

    [Fact]
    public void 許容範囲の境界は定数と一致すること()
    {
        // 期待値は定数から導出せずリテラルで書く（定数どうしを比べると同時に動かしたとき緑のまま通る。#1884）
        AppConstants.MinCompanionCountInputTimeoutSeconds.Should().Be(5);
        AppConstants.MaxCompanionCountInputTimeoutSeconds.Should().Be(300);
        CompanionCountTimeoutRange.IsValid(0).Should().BeTrue();
    }
}
