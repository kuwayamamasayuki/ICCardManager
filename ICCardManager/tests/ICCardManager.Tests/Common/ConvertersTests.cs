using System.Globalization;
using System.Windows;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Common/Converters.cs に定義された WPF 値コンバーターの単体テスト
/// </summary>
public class IntToVisibilityConverterTests
{
    private readonly IntToVisibilityConverter _converter = new();

    [Fact]
    public void Convert_正の整数の場合Visibleを返すこと()
    {
        var result = _converter.Convert(5, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Visible);
    }

    [Fact]
    public void Convert_ゼロの場合Collapsedを返すこと()
    {
        var result = _converter.Convert(0, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_負の整数の場合Collapsedを返すこと()
    {
        var result = _converter.Convert(-1, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_整数以外の場合Collapsedを返すこと()
    {
        var result = _converter.Convert("abc", typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_nullの場合Collapsedを返すこと()
    {
        var result = _converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }
}

public class InverseBooleanConverterTests
{
    private readonly InverseBooleanConverter _converter = new();

    [Fact]
    public void Convert_trueの場合falseを返すこと()
    {
        var result = _converter.Convert(true, typeof(bool), null, CultureInfo.InvariantCulture);
        result.Should().Be(false);
    }

    [Fact]
    public void Convert_falseの場合trueを返すこと()
    {
        var result = _converter.Convert(false, typeof(bool), null, CultureInfo.InvariantCulture);
        result.Should().Be(true);
    }

    [Fact]
    public void Convert_bool以外の場合falseを返すこと()
    {
        var result = _converter.Convert("not a bool", typeof(bool), null, CultureInfo.InvariantCulture);
        result.Should().Be(false);
    }

    [Fact]
    public void ConvertBack_trueの場合falseを返すこと()
    {
        var result = _converter.ConvertBack(true, typeof(bool), null, CultureInfo.InvariantCulture);
        result.Should().Be(false);
    }

    [Fact]
    public void ConvertBack_falseの場合trueを返すこと()
    {
        var result = _converter.ConvertBack(false, typeof(bool), null, CultureInfo.InvariantCulture);
        result.Should().Be(true);
    }

    [Fact]
    public void ConvertBack_bool以外の場合falseを返すこと()
    {
        var result = _converter.ConvertBack(42, typeof(bool), null, CultureInfo.InvariantCulture);
        result.Should().Be(false);
    }
}

public class BoolToVisibilityConverterTests
{
    private readonly BoolToVisibilityConverter _converter = new();

    [Fact]
    public void Convert_trueの場合Visibleを返すこと()
    {
        var result = _converter.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Visible);
    }

    [Fact]
    public void Convert_falseの場合Collapsedを返すこと()
    {
        var result = _converter.Convert(false, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_invertパラメータでtrueの場合Collapsedを返すこと()
    {
        var result = _converter.Convert(true, typeof(Visibility), "invert", CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_invertパラメータでfalseの場合Visibleを返すこと()
    {
        var result = _converter.Convert(false, typeof(Visibility), "invert", CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Visible);
    }

    [Fact]
    public void Convert_bool以外の場合Collapsedを返すこと()
    {
        var result = _converter.Convert("not a bool", typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_nullの場合Collapsedを返すこと()
    {
        var result = _converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }
}

public class FileSizeConverterTests
{
    private readonly FileSizeConverter _converter = new();

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    public void Convert_バイト単位の場合B表示になること(long input, string expected)
    {
        var result = _converter.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    public void Convert_KB単位の場合KB表示になること(long input, string expected)
    {
        var result = _converter.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Fact]
    public void Convert_MB単位の場合MB表示になること()
    {
        var result = _converter.Convert(1048576L, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("1 MB");
    }

    [Fact]
    public void Convert_GB単位の場合GB表示になること()
    {
        var result = _converter.Convert(1073741824L, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("1 GB");
    }

    [Fact]
    public void Convert_long以外の場合文字列表現を返すこと()
    {
        var result = _converter.Convert(42, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("42");
    }

    [Fact]
    public void Convert_nullの場合空文字列を返すこと()
    {
        var result = _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be(string.Empty);
    }
}
