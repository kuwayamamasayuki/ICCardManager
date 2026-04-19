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

/// <summary>
/// Issue #1278: <see cref="CountToAccessibilityTextConverter"/> の単体テスト。
/// スクリーンリーダー向けに件数を自然言語で読み上げられるよう変換する。
/// </summary>
public class CountToAccessibilityTextConverterTests
{
    private readonly CountToAccessibilityTextConverter _converter = new();

    [Theory]
    [InlineData(1, "登録カード", "登録カードは1件です")]
    [InlineData(5, "登録カード", "登録カードは5件です")]
    [InlineData(100, "職員", "職員は100件です")]
    [InlineData(1, "履歴", "履歴は1件です")]
    public void Convert_正の整数の場合件数を含む説明文を返すこと(int count, string subject, string expected)
    {
        var result = _converter.Convert(count, typeof(string), subject, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Convert_ゼロまたは負数の場合空リストを示す説明文を返すこと(int count)
    {
        var result = _converter.Convert(count, typeof(string), "登録カード", CultureInfo.InvariantCulture);
        result.Should().Be("登録カードはまだありません。新規登録してください");
    }

    [Fact]
    public void Convert_主語が未指定の場合項目という汎用語を使うこと()
    {
        var result = _converter.Convert(3, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("項目は3件です");
    }

    [Fact]
    public void Convert_主語が空白のみの場合項目という汎用語を使うこと()
    {
        var result = _converter.Convert(0, typeof(string), "   ", CultureInfo.InvariantCulture);
        result.Should().Be("項目はまだありません。新規登録してください");
    }

    [Fact]
    public void Convert_文字列の数値を解釈できること()
    {
        var result = _converter.Convert("7", typeof(string), "カード", CultureInfo.InvariantCulture);
        result.Should().Be("カードは7件です");
    }

    [Fact]
    public void Convert_数値として解釈不能な値の場合空文字列を返すこと()
    {
        var result = _converter.Convert("abc", typeof(string), "登録カード", CultureInfo.InvariantCulture);
        result.Should().Be(string.Empty);
    }

    [Fact]
    public void Convert_nullの場合空文字列を返すこと()
    {
        var result = _converter.Convert(null, typeof(string), "登録カード", CultureInfo.InvariantCulture);
        result.Should().Be(string.Empty);
    }

    [Fact]
    public void Convert_境界値1件の場合件数テキストが返ること()
    {
        // 0件（まだありません）と1件（N件です）の境界を保証
        var result = _converter.Convert(1, typeof(string), "登録カード", CultureInfo.InvariantCulture);
        result.Should().Be("登録カード1件".Replace("登録カード1件", "登録カードは1件です"));
    }

    [Fact]
    public void ConvertBack_NotImplementedExceptionをスローすること()
    {
        var act = () => _converter.ConvertBack("x", typeof(int), null, CultureInfo.InvariantCulture);
        act.Should().Throw<System.NotImplementedException>();
    }
}
