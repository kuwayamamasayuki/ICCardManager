using FluentAssertions;
using ICCardManager.Infrastructure.Security;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Security;

/// <summary>
/// Issue #1267: <see cref="FormulaInjectionSanitizer"/> の単体テスト。
/// </summary>
public class FormulaInjectionSanitizerTests
{
    #region IsDangerous

    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData("+1+1")]
    [InlineData("-MSF(1)")]
    [InlineData("@CONCAT")]
    [InlineData("\t=1+1")]       // タブ先頭
    [InlineData("\r=1+1")]       // CR先頭
    [InlineData("=")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("@")]
    public void IsDangerous_StartsWithDangerousChar_ReturnsTrue(string input)
    {
        FormulaInjectionSanitizer.IsDangerous(input).Should().BeTrue();
    }

    [Theory]
    // 危険文字で始まらない通常文字列
    [InlineData("hello")]
    [InlineData("123abc")]
    [InlineData("日本語")]
    [InlineData("  =1+1")]       // スペース先頭（スペース自体は危険文字ではない）
    [InlineData("1=2")]          // 途中に=があっても先頭ではない
    [InlineData("'=1+1")]        // 既にサニタイズ済み（'は危険文字ではない）
    [InlineData("\n=1+1")]       // LF は Excel の先頭スキップ対象外（ブラックリストに含めない）
    // null / 空文字列
    [InlineData(null)]
    [InlineData("")]
    public void IsDangerous_危険文字で始まらない入力はFalseを返すこと(string input)
    {
        FormulaInjectionSanitizer.IsDangerous(input).Should().BeFalse();
    }

    #endregion

    #region Sanitize

    [Theory]
    [InlineData("=SUM(1,2)", "'=SUM(1,2)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-3", "'-3")]
    [InlineData("@foo", "'@foo")]
    [InlineData("\t=1+1", "'\t=1+1")]
    [InlineData("\r=1+1", "'\r=1+1")]
    public void Sanitize_DangerousInput_PrependsSingleQuote(string input, string expected)
    {
        FormulaInjectionSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("日本語の備考")]
    [InlineData("2026-04-18")]
    [InlineData("1=2")]
    [InlineData("")]
    public void Sanitize_SafeInput_ReturnsUnchanged(string input)
    {
        FormulaInjectionSanitizer.Sanitize(input).Should().Be(input);
    }

    [Fact]
    public void Sanitize_Null_ReturnsNull()
    {
        FormulaInjectionSanitizer.Sanitize(null).Should().BeNull();
    }

    /// <summary>
    /// 二重サニタイズは発生しない（idempotent）: 一度サニタイズ済みの値は <c>'</c> で始まり、
    /// <c>'</c> 自体は危険文字集合に含まれないため、再度サニタイズしても変化しない。
    /// </summary>
    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData("+1+1")]
    [InlineData("@foo")]
    public void Sanitize_IsIdempotent(string input)
    {
        var once = FormulaInjectionSanitizer.Sanitize(input);
        var twice = FormulaInjectionSanitizer.Sanitize(once);

        twice.Should().Be(once, "二重サニタイズによる「''=...」は発生しない");
    }

    #endregion

    #region SanitizeOrEmpty

    [Fact]
    public void SanitizeOrEmpty_Null_ReturnsEmptyString()
    {
        FormulaInjectionSanitizer.SanitizeOrEmpty(null).Should().Be("");
    }

    [Fact]
    public void SanitizeOrEmpty_EmptyString_ReturnsEmptyString()
    {
        FormulaInjectionSanitizer.SanitizeOrEmpty("").Should().Be("");
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("normal text", "normal text")]
    [InlineData("日本語", "日本語")]
    public void SanitizeOrEmpty_NonNullInput_DelegatesToSanitize(string input, string expected)
    {
        FormulaInjectionSanitizer.SanitizeOrEmpty(input).Should().Be(expected);
    }

    #endregion

    #region DangerousStartChars

    /// <summary>
    /// DangerousStartChars は OWASP CSV Injection Prevention Cheat Sheet 準拠で
    /// = / + / - / @ / \t / \r の6文字を含むこと。
    /// </summary>
    [Fact]
    public void DangerousStartChars_ContainsOwaspRecommendedSet()
    {
        FormulaInjectionSanitizer.DangerousStartChars.Should().BeEquivalentTo(
            new[] { '=', '+', '-', '@', '\t', '\r' });
    }

    #endregion

    #region Unsanitize (Issue #1808)

    /// <summary>
    /// Issue #1808: エクスポートが付与した先頭 <c>'</c>（直後が危険文字）を 1 文字だけ取り除くこと。
    /// </summary>
    [Theory]
    [InlineData("'=SUM(1,2)", "=SUM(1,2)")]
    [InlineData("'+1+1", "+1+1")]
    [InlineData("'-異動予定", "-異動予定")]
    [InlineData("'@foo", "@foo")]
    [InlineData("'\t=1+1", "\t=1+1")]
    [InlineData("'\r=1+1", "\r=1+1")]
    public void Unsanitize_QuoteFollowedByDangerousChar_RemovesOnlyTheQuote(string input, string expected)
    {
        FormulaInjectionSanitizer.Unsanitize(input).Should().Be(expected);
    }

    /// <summary>
    /// Issue #1808: サニタイズ由来ではない値は変更しないこと。
    /// 先頭が <c>'</c> でも直後が危険文字でなければ利用者が入力した正当な文字列とみなす。
    /// </summary>
    [Theory]
    [InlineData("hello")]
    [InlineData("=SUM(1,2)")]     // 未サニタイズの値はそのまま
    [InlineData("'hello")]        // ' の直後が安全文字
    [InlineData("'")]             // ' のみ
    [InlineData("''")]            // '' （2 文字目が ' で危険文字ではない）
    [InlineData("''=foo")]        // Sanitize は '' を作らない（冪等）ため、これは利用者の入力
    [InlineData("  '=1")]         // 先頭がスペース
    [InlineData("")]
    [InlineData(null)]
    public void Unsanitize_NotSanitizedValue_ReturnsUnchanged(string input)
    {
        FormulaInjectionSanitizer.Unsanitize(input).Should().Be(input);
    }

    /// <summary>
    /// Issue #1808: Sanitize → Unsanitize で元の値に戻ること（往復対称性）。
    /// エクスポート（Sanitize）→ インポート（Unsanitize）の経路で DB 値が変質しないための性質。
    /// </summary>
    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData("-異動予定")]
    [InlineData("+81-90")]
    [InlineData("@channel")]
    [InlineData("hello")]
    [InlineData("日本語")]
    [InlineData("")]
    public void Unsanitize_AfterSanitize_RoundTripsToOriginal(string original)
    {
        FormulaInjectionSanitizer.Unsanitize(FormulaInjectionSanitizer.Sanitize(original))
            .Should().Be(original);
    }

    /// <summary>
    /// Issue #1808: 冪等性。2 回適用しても 1 回目と同じ結果になる
    /// （Sanitize が二重付与しないことの対。取り除いた後の値は <c>'</c> で始まらないため）。
    /// </summary>
    [Fact]
    public void Unsanitize_IsIdempotent()
    {
        var once = FormulaInjectionSanitizer.Unsanitize("'=foo");
        once.Should().Be("=foo");
        FormulaInjectionSanitizer.Unsanitize(once).Should().Be(once);
    }

    #endregion
}
