using System.Globalization;
using System.Windows;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// StringToVisibilityConverterの単体テスト（Issue #638）
/// </summary>
public class StringToVisibilityConverterTests
{
    private readonly StringToVisibilityConverter _converter = new();

    [Fact]
    public void Convert_NonEmptyString_ReturnsVisible()
    {
        var result = _converter.Convert("hello", typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Visible);
    }

    [Fact]
    public void Convert_EmptyString_ReturnsCollapsed()
    {
        var result = _converter.Convert(string.Empty, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_Null_ReturnsCollapsed()
    {
        var result = _converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_NonStringObject_ReturnsCollapsed()
    {
        var result = _converter.Convert(42, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void Convert_JapaneseString_ReturnsVisible()
    {
        var result = _converter.Convert("プレビューエラー: ファイルが見つかりません", typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Visible);
    }
}
