using System.IO;
using FluentAssertions;
using ICCardManager.Infrastructure.Security;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Security;

/// <summary>
/// Issue #1703: <see cref="FileNameSanitizer"/> の単体テスト。
/// パス区切り・不正ファイル名文字を除去し、パストラバーサルを防ぐことを検証する。
/// </summary>
public class FileNameSanitizerTests
{
    #region SanitizeComponent: 危険文字の除去

    [Theory]
    [InlineData("x\\..\\..\\..\\Users\\Public\\report")]  // Windows 区切り + 親ディレクトリ
    [InlineData("../../../etc/passwd")]                     // Unix 区切り
    [InlineData("..\\..\\Users\\Public\\evil")]
    [InlineData("a/b\\c")]
    public void SanitizeComponent_WithPathSeparators_RemovesAllSeparators(string input)
    {
        var result = FileNameSanitizer.SanitizeComponent(input);

        // パス区切り文字が一切残らない = Path.Combine で結合してもフォルダを脱出できない
        result.Should().NotContain("/");
        result.Should().NotContain("\\");
        // 単一のファイル名構成要素であり、パス構造を持たない
        Path.GetFileName(result).Should().Be(result);
    }

    [Theory]
    [InlineData("a:b")]   // ドライブ指定子/代替データストリーム
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b>c")]
    [InlineData("a|b")]
    public void SanitizeComponent_WithReservedChars_ReplacesWithUnderscore(string input)
    {
        var result = FileNameSanitizer.SanitizeComponent(input);

        result.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
        result.Should().NotContain(":");
    }

    [Fact]
    public void SanitizeComponent_WithControlChars_ReplacesThem()
    {
        var result = FileNameSanitizer.SanitizeComponent("a\tb\nc\0d");

        result.Should().Be("a_b_c_d");
    }

    #endregion

    #region SanitizeComponent: 正常値は不変

    [Theory]
    [InlineData("はやかけん")]
    [InlineData("nimoca")]
    [InlineData("H001")]
    [InlineData("SUGOCA-2024")]
    [InlineData("職員_A")]
    public void SanitizeComponent_WithNormalValue_ReturnsUnchanged(string input)
    {
        FileNameSanitizer.SanitizeComponent(input).Should().Be(input);
    }

    #endregion

    #region 冪等性・null / 空

    [Fact]
    public void SanitizeComponent_IsIdempotent()
    {
        var once = FileNameSanitizer.SanitizeComponent("x\\..\\evil");
        var twice = FileNameSanitizer.SanitizeComponent(once);

        twice.Should().Be(once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitizeComponent_WithNullOrEmpty_ReturnsInput(string input)
    {
        FileNameSanitizer.SanitizeComponent(input).Should().Be(input);
    }

    [Fact]
    public void SanitizeComponentOrEmpty_WithNull_ReturnsEmpty()
    {
        FileNameSanitizer.SanitizeComponentOrEmpty(null).Should().Be(string.Empty);
    }

    #endregion
}
