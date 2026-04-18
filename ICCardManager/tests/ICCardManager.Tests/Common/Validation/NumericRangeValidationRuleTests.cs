using System.Globalization;
using FluentAssertions;
using ICCardManager.Common.Validation;
using Xunit;

namespace ICCardManager.Tests.Common.Validation;

/// <summary>
/// Issue #1279: <see cref="NumericRangeValidationRule"/> の単体テスト。
/// </summary>
public class NumericRangeValidationRuleTests
{
    private static readonly CultureInfo Japanese = new("ja-JP");

    [Theory]
    [InlineData("0", 0, 20000)]
    [InlineData("1000", 0, 20000)]
    [InlineData("20000", 0, 20000)]
    [InlineData("-5", -10, 10)]
    public void Validate_範囲内の値の場合ValidResultを返すこと(string input, int min, int max)
    {
        var rule = new NumericRangeValidationRule { Min = min, Max = max, FieldName = "値" };

        var result = rule.Validate(input, Japanese);

        result.IsValid.Should().BeTrue();
        result.ErrorContent.Should().BeNull();
    }

    [Theory]
    [InlineData("-1", 0, 20000, "下限を下回")]
    [InlineData("-100", 0, 20000, "下限を下回")]
    public void Validate_下限未満の場合IsValid_falseで下限エラーメッセージを返すこと(
        string input, int min, int max, string expectedFragment)
    {
        var rule = new NumericRangeValidationRule { Min = min, Max = max, FieldName = "残額警告しきい値" };

        var result = rule.Validate(input, Japanese);

        result.IsValid.Should().BeFalse();
        result.ErrorContent.ToString().Should().Contain(expectedFragment);
        result.ErrorContent.ToString().Should().Contain("残額警告しきい値");
        result.ErrorContent.ToString().Should().Contain($"{min:N0}以上");
    }

    [Theory]
    [InlineData("20001", 0, 20000)]
    [InlineData("999999", 0, 20000)]
    public void Validate_上限超過の場合IsValid_falseで上限エラーメッセージを返すこと(string input, int min, int max)
    {
        var rule = new NumericRangeValidationRule { Min = min, Max = max, FieldName = "残額警告しきい値" };

        var result = rule.Validate(input, Japanese);

        result.IsValid.Should().BeFalse();
        result.ErrorContent.ToString().Should().Contain("上限を超えて");
        result.ErrorContent.ToString().Should().Contain("残額警告しきい値");
        result.ErrorContent.ToString().Should().Contain($"{max:N0}以下");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12x")]
    [InlineData("1.5")] // 小数は整数として解釈不能
    public void Validate_数値として解釈不能な文字列の場合エラーを返すこと(string input)
    {
        var rule = new NumericRangeValidationRule { Min = 0, Max = 100, FieldName = "金額" };

        var result = rule.Validate(input, Japanese);

        result.IsValid.Should().BeFalse();
        result.ErrorContent.ToString().Should().Contain("数値として認識できません");
        result.ErrorContent.ToString().Should().Contain("金額");
    }

    [Fact]
    public void Validate_空文字列かつAllowEmpty_falseの場合エラーを返すこと()
    {
        var rule = new NumericRangeValidationRule { Min = 0, Max = 100, FieldName = "金額", AllowEmpty = false };

        var result = rule.Validate("", Japanese);

        result.IsValid.Should().BeFalse();
        result.ErrorContent.ToString().Should().Contain("入力されていません");
    }

    [Fact]
    public void Validate_空文字列かつAllowEmpty_trueの場合ValidResultを返すこと()
    {
        var rule = new NumericRangeValidationRule { Min = 0, Max = 100, FieldName = "金額", AllowEmpty = true };

        var result = rule.Validate("", Japanese);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_nullかつAllowEmpty_trueの場合ValidResultを返すこと()
    {
        var rule = new NumericRangeValidationRule { AllowEmpty = true };

        var result = rule.Validate(null, Japanese);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_FieldName未設定の場合既定値の値が使われること()
    {
        var rule = new NumericRangeValidationRule { Min = 0, Max = 100 };

        var result = rule.Validate("-1", Japanese);

        result.IsValid.Should().BeFalse();
        // 既定の FieldName="値" がメッセージに含まれる
        result.ErrorContent.ToString().Should().Contain("値");
    }

    [Fact]
    public void Validate_上下限未指定の場合数値のみチェックされること()
    {
        // Min/Max がデフォルト値（int.MinValue / int.MaxValue）の場合、範囲チェックは事実上スキップされる
        var rule = new NumericRangeValidationRule { FieldName = "金額" };

        var validInt = rule.Validate("1000000", Japanese);
        var invalidText = rule.Validate("abc", Japanese);

        validInt.IsValid.Should().BeTrue();
        invalidText.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_エラーメッセージに実際の入力値が含まれること()
    {
        // Issue #1275 の3要素原則: エラーメッセージに実際の値を含める
        var rule = new NumericRangeValidationRule { Min = 0, Max = 100, FieldName = "残額警告しきい値" };

        var result = rule.Validate("500", Japanese);

        result.IsValid.Should().BeFalse();
        result.ErrorContent.ToString().Should().Contain("500", "実際の入力値が含まれるべき");
    }
}
