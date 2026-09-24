using System;
using System.Collections.Generic;
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

    #region 切り詰めとサロゲートペア（Issue #2110）

    // 最大長で切り詰める 4 メソッド。切り詰めは RemoveInvalidSurrogates の「後」に走るため、
    // ここで分断されたペアの片割れは、どこでも取り除かれずに DB へ届く。
    private static readonly Dictionary<string, (Func<string, string> Sanitize, int MaxLength)> TruncatingMethods =
        new Dictionary<string, (Func<string, string>, int)>
        {
            [nameof(InputSanitizer.SanitizeName)] = (InputSanitizer.SanitizeName, 50),
            [nameof(InputSanitizer.SanitizeStaffNumber)] = (InputSanitizer.SanitizeStaffNumber, 20),
            [nameof(InputSanitizer.SanitizeNote)] = (InputSanitizer.SanitizeNote, 200),
            [nameof(InputSanitizer.SanitizeCardNumber)] = (InputSanitizer.SanitizeCardNumber, 20),
        };

    public static TheoryData<string> TruncatingMethodNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in TruncatingMethods.Keys)
        {
            data.Add(name);
        }
        return data;
    }

    /// <summary>
    /// UTF-16 のコード単位で数えて N 文字目にサロゲートペアの上位がかかる場合、
    /// ペアを分断せず 1 つ手前で切ること（単独の上位サロゲートを残さない）。
    /// </summary>
    [Theory]
    [MemberData(nameof(TruncatingMethodNames))]
    public void 最大長の位置にかかるサロゲートペアは分断せずペアごと落とすこと(string methodName)
    {
        var (sanitize, maxLength) = TruncatingMethods[methodName];
        var prefix = new string('あ', maxLength - 1);

        var result = sanitize(prefix + "😀");

        result.Should().Be(prefix);
        HasLoneSurrogate(result).Should().BeFalse();
    }

    /// <summary>
    /// 最大長にちょうど収まるサロゲートペアは、切り詰めで落とさないこと（対の表明）。
    /// これが無いと、サロゲートペアを含む入力を常に 1 文字短く切る実装でも上のテストは緑になる。
    /// </summary>
    [Theory]
    [MemberData(nameof(TruncatingMethodNames))]
    public void 最大長にちょうど収まるサロゲートペアは保つこと(string methodName)
    {
        var (sanitize, maxLength) = TruncatingMethods[methodName];
        var expected = new string('あ', maxLength - 2) + "😀";

        var result = sanitize(expected + "い");

        result.Should().Be(expected);
    }

    /// <summary>
    /// ペアを落として 1 つ手前で切った結果、末尾が空白になる場合は空白も落とすこと。
    /// 切り詰めの前に Trim を済ませているので、ここで落とさないと Standard の「前後の空白を削除」が崩れる。
    /// </summary>
    [Theory]
    [MemberData(nameof(TruncatingMethodNames))]
    public void ペアを落として末尾が空白になる場合は空白も落とすこと(string methodName)
    {
        var (sanitize, maxLength) = TruncatingMethods[methodName];
        var prefix = new string('あ', maxLength - 2);

        var result = sanitize(prefix + " 😀");

        result.Should().Be(prefix);
    }

    /// <summary>
    /// サロゲートペアを含まない入力でも、切り詰めた結果の末尾が空白なら空白を落とすこと。
    /// 末尾の空白の除去はペアを落としたときに限らず、切り詰めのたびに効く。
    /// </summary>
    [Theory]
    [MemberData(nameof(TruncatingMethodNames))]
    public void 切り詰めた結果の末尾が空白ならサロゲートペアが無くても空白を落とすこと(string methodName)
    {
        var (sanitize, maxLength) = TruncatingMethods[methodName];
        var prefix = new string('あ', maxLength - 1);

        var result = sanitize(prefix + " い");

        result.Should().Be(prefix);
    }

    /// <summary>
    /// サロゲートペアをどの位置に置いても、結果は単独のサロゲートを含まず、最大長以内で、
    /// サニタイズ済みの入力の先頭部分であること（位置を走査する不変条件）。
    /// </summary>
    [Theory]
    [MemberData(nameof(TruncatingMethodNames))]
    public void サロゲートペアの位置によらず結果は単独のサロゲートを含まない入力の先頭部分であること(string methodName)
    {
        var (sanitize, maxLength) = TruncatingMethods[methodName];

        for (var position = 0; position <= maxLength + 1; position++)
        {
            var input = new string('あ', position) + "😀" + new string('い', maxLength);

            var result = sanitize(input);

            HasLoneSurrogate(result).Should().BeFalse($"ペアを {position} 文字目に置いた入力");
            result.Length.Should().BeLessOrEqualTo(maxLength, $"ペアを {position} 文字目に置いた入力");
            input.Should().StartWith(result, $"ペアを {position} 文字目に置いた入力");
        }
    }

    /// <summary>
    /// 検査の固定: <see cref="HasLoneSurrogate"/> が単独の上位・下位を検出し、正しいペアを検出しないこと。
    /// これが壊れると上の表明はすべて空振りで緑になる。
    /// </summary>
    /// <remarks>
    /// InlineData にしない。xUnit は Theory の引数を直列化するため、単独のサロゲートが置換文字（U+FFFD）へ化ける。
    /// </remarks>
    [Fact]
    public void 単独のサロゲートの検査が既知の入力を正しく判定すること()
    {
        HasLoneSurrogate("あ\uD83D").Should().BeTrue("末尾の単独の上位サロゲート");
        HasLoneSurrogate("\uDE00あ").Should().BeTrue("先頭の単独の下位サロゲート");
        HasLoneSurrogate("\uD83Dあ\uDE00").Should().BeTrue("間に別の文字を挟んだ上位と下位");
        HasLoneSurrogate("あ😀").Should().BeFalse("正しいペア");
        HasLoneSurrogate("あい").Should().BeFalse("サロゲートを含まない");
    }

    private static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }
        return false;
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
