using System.Globalization;
using System.Windows;
using FluentAssertions;
using ICCardManager.Views.Converters;
using Xunit;

namespace ICCardManager.Tests.Views.Converters;

/// <summary>
/// Views/Converters/VisibilityConverters.cs に定義された WPF 値コンバーターの単体テスト
///
/// 注: IntToVisibilityConverter は Issue #1227 で Common/Converters.cs に統合済み。
/// テストは Common/ConvertersTests.cs の IntToVisibilityConverterTests を参照。
/// </summary>
public class InverseBoolConverterTests
{
    private readonly InverseBoolConverter _converter = new();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Convert_bool値を反転すること(bool input, bool expected)
    {
        var result = _converter.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ConvertBack_bool値を反転すること(bool input, bool expected)
    {
        var result = _converter.ConvertBack(input, typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(expected);
    }

    [Fact]
    public void Convert_bool以外の型はfalseを返すこと()
    {
        var result = _converter.Convert("not a bool", typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(false);
    }

    [Fact]
    public void Convert_nullはfalseを返すこと()
    {
        var result = _converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(false);
    }
}

public class NotNullToBoolConverterTests
{
    private readonly NotNullToBoolConverter _converter = new();

    [Fact]
    public void Convert_オブジェクトが非nullの場合はtrueを返すこと()
    {
        var result = _converter.Convert(new object(), typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(true);
    }

    [Fact]
    public void Convert_文字列が非nullの場合はtrueを返すこと()
    {
        var result = _converter.Convert("any string", typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(true);
    }

    [Fact]
    public void Convert_空文字列も非nullなのでtrueを返すこと()
    {
        var result = _converter.Convert(string.Empty, typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(true, "Convertは「null かどうか」のみ判定する");
    }

    [Fact]
    public void Convert_nullの場合はfalseを返すこと()
    {
        var result = _converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture);

        result.Should().Be(false);
    }
}
