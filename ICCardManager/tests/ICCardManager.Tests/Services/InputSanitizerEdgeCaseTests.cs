using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// InputSanitizerのエッジケーステスト
/// 既存テストで検出できないUnicode異常値・サロゲートペア境界・ゼロ幅文字位置パターンを検証する。
/// </summary>
public class InputSanitizerEdgeCaseTests
{
    #region サロゲートペアの境界パターン

    /// <summary>
    /// 文字列末尾に単独の高位サロゲートがある場合に正しく除去されること。
    /// </summary>
    [Fact]
    public void Sanitize_HighSurrogateAtEnd_RemovesIt()
    {
        var input = "テスト\uD800";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト");
    }

    /// <summary>
    /// 文字列先頭に単独の低位サロゲートがある場合に正しく除去されること。
    /// </summary>
    [Fact]
    public void Sanitize_LowSurrogateAtStart_RemovesIt()
    {
        var input = "\uDC00テスト";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト");
    }

    /// <summary>
    /// 連続する2つの高位サロゲート（どちらも単独）が両方除去されること。
    /// </summary>
    [Fact]
    public void Sanitize_TwoConsecutiveHighSurrogates_RemovesBoth()
    {
        var input = "テスト\uD800\uD801残り";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト残り");
    }

    /// <summary>
    /// 有効なサロゲートペアと無効なサロゲートが混在する場合、
    /// 有効なペアは保持され、無効な単独サロゲートのみ除去されること。
    /// </summary>
    [Fact]
    public void Sanitize_MixedValidAndInvalidSurrogates_KeepsValidRemovesInvalid()
    {
        // 😀(有効ペア) + 単独高位サロゲート
        var input = "前😀中\uD800後";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("前😀中後");
    }

    #endregion

    #region ゼロ幅文字の位置パターン

    /// <summary>
    /// 文字列末尾のBOM(U+FEFF)が除去されること。
    /// 既存テストは先頭のBOMのみ。
    /// </summary>
    [Fact]
    public void Sanitize_BomAtEnd_RemovesBom()
    {
        var input = "テスト\uFEFF";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト");
    }

    /// <summary>
    /// 連続するゼロ幅文字がすべて除去されること。
    /// </summary>
    [Fact]
    public void Sanitize_MultipleConsecutiveZeroWidthChars_RemovesAll()
    {
        var input = "テスト\u200B\u200C\u200D文字";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト文字");
    }

    /// <summary>
    /// Word Joiner (U+2060) が除去されること。
    /// </summary>
    [Fact]
    public void Sanitize_WordJoiner_RemovesIt()
    {
        var input = "テスト\u2060文字";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト文字");
    }

    #endregion

    #region 空白正規化のエッジケース

    /// <summary>
    /// タブとスペースが混在する場合、単一スペースに正規化されること。
    /// </summary>
    [Fact]
    public void Sanitize_MixedTabsAndSpaces_NormalizedToSingleSpace()
    {
        var input = "テスト\t  \t文字";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト 文字");
    }

    /// <summary>
    /// 全角スペース（U+3000）は空白正規化の対象外（保持される）。
    /// MultipleWhitespaceRegex は [ \t]+ なので全角スペースには適用されない。
    /// </summary>
    [Fact]
    public void Sanitize_FullWidthSpace_IsPreserved()
    {
        var input = "田中\u3000太郎";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("田中\u3000太郎", "全角スペースはMultipleWhitespaceRegexの対象外");
    }

    #endregion

    #region 制御文字の境界テスト

    /// <summary>
    /// タブ(U+0009)とLF(U+000A)とCR(U+000D)は制御文字から除外されていること。
    /// これらはControlCharactersRegexの範囲外。
    /// </summary>
    [Fact]
    public void Sanitize_TabAndNewlinesAreNotControlCharacters()
    {
        // タブはNormalizeWhitespaceで処理される
        // LFとCRは制御文字除去対象外
        var input = "行1\n行2\r行3";

        var result = InputSanitizer.Sanitize(input, SanitizeOptions.RemoveControlCharacters);

        result.Should().Contain("\n", "LFは制御文字として除去されない");
        result.Should().Contain("\r", "CRは制御文字として除去されない");
    }

    /// <summary>
    /// 制御文字のみで構成される入力がTrimで空文字になること。
    /// </summary>
    [Fact]
    public void Sanitize_OnlyControlCharacters_ReturnsEmpty()
    {
        var input = "\u0000\u0001\u0002\u0003";

        var result = InputSanitizer.Sanitize(input);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// C1制御文字（U+0080-U+009F）が除去されること。
    /// </summary>
    [Fact]
    public void Sanitize_C1ControlCharacters_RemovesThem()
    {
        var input = "テスト\u0080\u008F\u009F文字";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("テスト文字");
    }

    #endregion

    #region SanitizeName — 切り詰めとサニタイズの組み合わせ

    /// <summary>
    /// 制御文字除去後に50文字に収まる場合、切り詰めは発生しないこと。
    /// </summary>
    [Fact]
    public void SanitizeName_ControlCharsRemovedThenWithinLimit_NoTruncation()
    {
        // 48文字 + 制御文字3個 = サニタイズ後48文字
        var input = new string('あ', 48) + "\u0000\u0001\u0002";

        var result = InputSanitizer.SanitizeName(input);

        result.Should().HaveLength(48);
    }

    /// <summary>
    /// サニタイズ後も50文字を超える場合、50文字に切り詰められること。
    /// </summary>
    [Fact]
    public void SanitizeName_SanitizedStillOverLimit_TruncatesTo50()
    {
        // 55文字（制御文字なし）
        var input = new string('あ', 55);

        var result = InputSanitizer.SanitizeName(input);

        result.Should().HaveLength(50);
    }

    /// <summary>
    /// null入力で空文字を返すこと。
    /// </summary>
    [Fact]
    public void SanitizeName_Null_ReturnsEmpty()
    {
        var result = InputSanitizer.SanitizeName(null);

        result.Should().BeEmpty();
    }

    #endregion

    #region SanitizeNote — 切り詰め境界

    /// <summary>
    /// ちょうど200文字の備考はそのまま返されること。
    /// </summary>
    [Fact]
    public void SanitizeNote_Exactly200Characters_NoTruncation()
    {
        var input = new string('あ', 200);

        var result = InputSanitizer.SanitizeNote(input);

        result.Should().HaveLength(200);
    }

    /// <summary>
    /// 201文字の備考は200文字に切り詰められること。
    /// </summary>
    [Fact]
    public void SanitizeNote_201Characters_TruncatesTo200()
    {
        var input = new string('あ', 201);

        var result = InputSanitizer.SanitizeNote(input);

        result.Should().HaveLength(200);
    }

    #endregion

    #region SanitizeCardNumber

    /// <summary>
    /// null入力で空文字を返すこと。
    /// </summary>
    [Fact]
    public void SanitizeCardNumber_Null_ReturnsEmpty()
    {
        var result = InputSanitizer.SanitizeCardNumber(null);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// ちょうど20文字のカード番号はそのまま返されること。
    /// </summary>
    [Fact]
    public void SanitizeCardNumber_Exactly20Characters_NoTruncation()
    {
        var input = new string('A', 20);

        var result = InputSanitizer.SanitizeCardNumber(input);

        result.Should().HaveLength(20);
    }

    #endregion

    #region SanitizeStaffNumber

    /// <summary>
    /// ちょうど20文字の職員番号はそのまま返されること。
    /// </summary>
    [Fact]
    public void SanitizeStaffNumber_Exactly20Characters_NoTruncation()
    {
        var input = new string('A', 20);

        var result = InputSanitizer.SanitizeStaffNumber(input);

        result.Should().HaveLength(20);
    }

    /// <summary>
    /// null入力で空文字を返すこと。
    /// </summary>
    [Fact]
    public void SanitizeStaffNumber_Null_ReturnsEmpty()
    {
        var result = InputSanitizer.SanitizeStaffNumber(null);

        result.Should().BeEmpty();
    }

    #endregion

    #region 複合的な異常入力

    /// <summary>
    /// サロゲートペア・ゼロ幅文字・制御文字・余分な空白がすべて混在する入力。
    /// </summary>
    [Fact]
    public void Sanitize_AllIssuesCombined_HandlesCorrectly()
    {
        // 先頭: BOM + 高位サロゲート単独
        // 中間: ゼロ幅スペース + 制御文字 + 連続空白
        // 末尾: 低位サロゲート単独
        var input = "\uFEFF\uD800田中\u200B\u0007  太郎\uDC00";

        var result = InputSanitizer.Sanitize(input);

        result.Should().Be("田中 太郎");
    }

    #endregion
}
