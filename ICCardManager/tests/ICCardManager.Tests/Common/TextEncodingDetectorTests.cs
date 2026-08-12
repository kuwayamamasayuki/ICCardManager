using System.Text;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="TextEncodingDetector"/> の単体テスト（Issue #1744）
/// </summary>
/// <remarks>
/// 判別順序（UTF-8 を先に試す）が本質的に重要なため、
/// 「Shift_JIS が UTF-8 と誤判定されないこと」と
/// 「UTF-8 が Shift_JIS と誤判定されないこと」を両方向から固定する。
/// </remarks>
public class TextEncodingDetectorTests
{
    private static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

    // 実データに近いサンプル（職員CSVの1行）。ASCII と日本語が混在する形。
    private const string SampleCsv = "0123456789ABCDEF,山田太郎,001,福岡市役所";

    #region BOM 付き

    [Fact]
    public void Decode_UTF8_BOM付きの日本語を復号できること()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(SampleCsv));

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf8WithBom);
        result.Text.Should().Be(SampleCsv);
    }

    [Fact]
    public void Decode_UTF16LE_BOM付きの日本語を復号できること()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(SampleCsv));

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf16LittleEndian);
        result.Text.Should().Be(SampleCsv);
    }

    [Fact]
    public void Decode_UTF16BE_BOM付きの日本語を復号できること()
    {
        var bytes = Encoding.BigEndianUnicode.GetPreamble()
            .Concat(Encoding.BigEndianUnicode.GetBytes(SampleCsv));

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf16BigEndian);
        result.Text.Should().Be(SampleCsv);
    }

    [Fact]
    public void Decode_UTF32LE_BOM付きの日本語を復号できること()
    {
        // FF FE 00 00 は UTF-16 LE の BOM（FF FE）と前方一致するため、
        // UTF-32 を先に判定しないと文字化けする（StreamReader と同じ判定順）。
        var bytes = Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(SampleCsv));

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf32LittleEndian);
        result.Text.Should().Be(SampleCsv);
    }

    [Fact]
    public void Decode_UTF32BE_BOM付きの日本語を復号できること()
    {
        var utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        var bytes = utf32Be.GetPreamble().Concat(utf32Be.GetBytes(SampleCsv));

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf32BigEndian);
        result.Text.Should().Be(SampleCsv);
    }

    [Fact]
    public void Decode_UTF8_BOM付きだが後続が不正なバイト列は判別不能になること()
    {
        // BOM が UTF-8 を名乗っていても、置換文字（U+FFFD）を混ぜて読み進めない。
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(new byte[] { 0x82, 0x20 });

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Undecidable);
        result.IsDecoded.Should().BeFalse();
        result.Text.Should().BeNull();
    }

    #endregion

    #region BOM 無し

    [Fact]
    public void Decode_BOM無しUTF8の日本語を復号できること()
    {
        var bytes = Encoding.UTF8.GetBytes(SampleCsv);

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf8);
        result.Text.Should().Be(SampleCsv);
    }

    [Fact]
    public void Decode_BOM無しUTF8の日本語がShiftJISと誤判定されないこと()
    {
        // UTF-8 の日本語（E3 81 82 等）は Shift_JIS としても偶然復号できてしまうため、
        // UTF-8 を先に試す順序でなければこのテストは落ちる。
        var bytes = Encoding.UTF8.GetBytes("福岡市交通局");

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf8);
        result.Text.Should().Be("福岡市交通局");
    }

    [Fact]
    public void Decode_BOM無しShiftJISの日本語を復号できること()
    {
        var bytes = ShiftJis.GetBytes(SampleCsv);

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.ShiftJis);
        result.Text.Should().Be(SampleCsv);
    }

    [Fact]
    public void Decode_BOM無しShiftJISの復号結果に置換文字が含まれないこと()
    {
        // Issue #1744 の回帰テスト本体。
        // 従来は Encoding.UTF8（置換フォールバック）固定だったため U+FFFD が混入した。
        var bytes = ShiftJis.GetBytes("山田太郎,鉄道（博多駅～天神駅）,ﾊﾝｶｸｶﾅ");

        var result = TextEncodingDetector.Decode(bytes);

        result.Text.Should().NotContain("�");
        result.Text.Should().Be("山田太郎,鉄道（博多駅～天神駅）,ﾊﾝｶｸｶﾅ");
    }

    [Fact]
    public void Decode_ASCIIのみのBOM無しファイルはUTF8として復号されること()
    {
        // ASCII は UTF-8 でも Shift_JIS でも同じバイト列。先に成功する UTF-8 が選ばれる。
        var bytes = Encoding.ASCII.GetBytes("ID,IDm,Date\n1,0123456789ABCDEF,2026-04-01");

        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Utf8);
        result.Text.Should().Be("ID,IDm,Date\n1,0123456789ABCDEF,2026-04-01");
    }

    [Fact]
    public void Decode_空のバイト列は空文字列として復号されること()
    {
        var result = TextEncodingDetector.Decode(new byte[0]);

        result.IsDecoded.Should().BeTrue();
        result.Text.Should().Be(string.Empty);
    }

    #endregion

    #region 判別不能

    [Theory]
    // 0x82 は Shift_JIS のリードバイトだが 0x20 は正当なトレイルバイトではない。
    // UTF-8 としても 0x82 単独は継続バイトの位置違いで不正。
    [InlineData(new byte[] { 0x82, 0x20 })]
    // 途中で切れた Shift_JIS 2バイト文字（リードバイトのみで終端）
    [InlineData(new byte[] { 0x93, 0x8C, 0x8B })]
    public void Decode_UTF8でもShiftJISでも読めないバイト列は判別不能になること(byte[] bytes)
    {
        var result = TextEncodingDetector.Decode(bytes);

        result.Encoding.Should().Be(DetectedTextEncoding.Undecidable);
        result.IsDecoded.Should().BeFalse();
        result.Text.Should().BeNull();
    }

    [Fact]
    public void Decode_nullを渡した場合は空文字列として扱うこと()
    {
        var result = TextEncodingDetector.Decode(null);

        result.IsDecoded.Should().BeTrue();
        result.Text.Should().Be(string.Empty);
    }

    #endregion

    #region 表示名（ログ・エラー文言用）

    [Theory]
    [InlineData(DetectedTextEncoding.Utf8WithBom, "UTF-8（BOM付き）")]
    [InlineData(DetectedTextEncoding.Utf8, "UTF-8（BOMなし）")]
    [InlineData(DetectedTextEncoding.ShiftJis, "Shift_JIS")]
    public void DisplayName_判別結果に応じた表示名を返すこと(DetectedTextEncoding encoding, string expected)
    {
        TextEncodingDetector.GetDisplayName(encoding).Should().Be(expected);
    }

    #endregion
}

/// <summary>
/// バイト列連結のテスト用ヘルパー
/// </summary>
internal static class TextEncodingDetectorTestExtensions
{
    public static byte[] Concat(this byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        System.Buffer.BlockCopy(first, 0, result, 0, first.Length);
        System.Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
}
