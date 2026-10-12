using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="LogContinuationLines"/> の単体テスト（Issue #2270）
/// </summary>
/// <remarks>
/// ログファイルへ書き出す値の中の改行を「改行 ＋ 継続行の接頭辞」へ置き換え、
/// 埋め込まれた値が偽のレコードを行頭に差し込めないことを検証する。
/// </remarks>
public class LogContinuationLinesTests
{
    private const string NewLine = "\r\n";
    private const string Prefix = "  | ";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u000B")]
    [InlineData("\u000C")]
    public void Indent_改行の種類を問わず継続行へ置き換えること(string lineBreak)
    {
        var result = LogContinuationLines.Indent($"前半{lineBreak}[2026-01-01 00:00:00.000] [INF] 偽", NewLine, Prefix);

        result.Should().Be("前半\r\n  | [2026-01-01 00:00:00.000] [INF] 偽");
    }

    [Fact]
    public void Indent_CRLFは1つの改行として扱い空の継続行を作らないこと()
    {
        LogContinuationLines.Indent("a\r\nb", "\n", Prefix).Should().Be("a\n  | b");
    }

    [Fact]
    public void Indent_連続する改行はそれぞれ継続行になること()
    {
        // LF CR の順は CRLF ではないため 2 つの改行として扱う
        LogContinuationLines.Indent("a\n\rb", "\n", Prefix).Should().Be("a\n  | \n  | b");
    }

    [Fact]
    public void Indent_改行を含まない値はそのまま返すこと()
    {
        const string text = "設定値を日時として解釈できませんでした: last_backup_success_at=abc";

        LogContinuationLines.Indent(text, NewLine, Prefix).Should().BeSameAs(text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Indent_nullと空文字は空文字を返すこと(string? text)
    {
        LogContinuationLines.Indent(text, NewLine, Prefix).Should().BeEmpty();
    }
}
