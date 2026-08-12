using System;
using System.Text;

namespace ICCardManager.Common
{
    /// <summary>判別された文字コード</summary>
    public enum DetectedTextEncoding
    {
        /// <summary>UTF-8（BOM付き）</summary>
        Utf8WithBom,

        /// <summary>UTF-16 リトルエンディアン（BOM付き）</summary>
        Utf16LittleEndian,

        /// <summary>UTF-16 ビッグエンディアン（BOM付き）</summary>
        Utf16BigEndian,

        /// <summary>UTF-32 リトルエンディアン（BOM付き）</summary>
        Utf32LittleEndian,

        /// <summary>UTF-32 ビッグエンディアン（BOM付き）</summary>
        Utf32BigEndian,

        /// <summary>UTF-8（BOMなし）</summary>
        Utf8,

        /// <summary>Shift_JIS（cp932。BOMなし）</summary>
        ShiftJis,

        /// <summary>判別不能（UTF-8・Shift_JIS のいずれとしても復号できない）</summary>
        Undecidable
    }

    /// <summary>テキストの復号結果</summary>
    public sealed class TextDecodeResult
    {
        /// <summary>判別された文字コード</summary>
        public DetectedTextEncoding Encoding { get; }

        /// <summary>復号されたテキスト（判別不能の場合は null）</summary>
        public string Text { get; }

        /// <summary>復号できたか</summary>
        public bool IsDecoded => Text != null;

        internal TextDecodeResult(DetectedTextEncoding encoding, string text)
        {
            Encoding = encoding;
            Text = text;
        }
    }

    /// <summary>
    /// バイト列の文字コードを判別してテキストへ復号する（Issue #1744）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判別は「BOM → BOMなし UTF-8 → BOMなし Shift_JIS」の順で行い、
    /// どの候補でも復号できない場合は <see cref="DetectedTextEncoding.Undecidable"/> を返す。
    /// </para>
    /// <para>
    /// <b>UTF-8 を Shift_JIS より先に試す順序は仕様である。</b>
    /// UTF-8 の日本語（例: E3 81 82）は cp932 としても偶然復号できてしまうため、
    /// 逆順にすると UTF-8 のファイルが文字化けする。一方 Shift_JIS の日本語は
    /// トレイルバイトが 0x40-0x7E を取り得るため、ほぼ確実に不正な UTF-8 になる。
    /// </para>
    /// <para>
    /// <b>復号はすべて例外フォールバックで行う。</b> 既定の <see cref="System.Text.Encoding.UTF8"/> は
    /// 不正バイトを U+FFFD へ黙って置換するため、「読めたのか化けたのか」を呼び出し元が区別できない。
    /// 置換ではなく例外を判定シグナルにすることで、化けたまま DB へ書き込む経路を断つ。
    /// </para>
    /// <para>
    /// 設定ファイル（<c>database_config.txt</c>）の読み込みには使わない。あちらは
    /// Inno Setup が ANSI で書く前提で「BOMなし＝Shift_JIS」と決め打ちしてよく、判別の方針が異なる。
    /// </para>
    /// </remarks>
    public static class TextEncodingDetector
    {
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
        private static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };
        private static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };
        private static readonly byte[] Utf32LeBom = { 0xFF, 0xFE, 0x00, 0x00 };
        private static readonly byte[] Utf32BeBom = { 0x00, 0x00, 0xFE, 0xFF };

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // .NET Framework は cp932 を標準で解決できる（.NET Core と異なり
        // CodePagesEncodingProvider の登録は不要）。ターゲットを変える際は要確認。
        private static readonly Encoding StrictShiftJis = Encoding.GetEncoding(
            932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        private static readonly Encoding StrictUtf16Le =
            new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

        private static readonly Encoding StrictUtf16Be =
            new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

        private static readonly Encoding StrictUtf32Le =
            new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);

        private static readonly Encoding StrictUtf32Be =
            new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

        /// <summary>
        /// バイト列の文字コードを判別して復号する
        /// </summary>
        /// <param name="bytes">対象のバイト列（null・空は空文字列として扱う）</param>
        /// <returns>
        /// 復号結果。どの候補でも復号できなかった場合は
        /// <see cref="TextDecodeResult.IsDecoded"/> が false になる
        /// </returns>
        public static TextDecodeResult Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return new TextDecodeResult(DetectedTextEncoding.Utf8, string.Empty);
            }

            // BOM は長いものから照合する。UTF-32 LE の BOM（FF FE 00 00）は
            // UTF-16 LE の BOM（FF FE）と前方一致するため、順序を逆にすると誤判定する。
            if (StartsWith(bytes, Utf32LeBom))
            {
                return DecodeWithBom(bytes, Utf32LeBom.Length, StrictUtf32Le, DetectedTextEncoding.Utf32LittleEndian);
            }

            if (StartsWith(bytes, Utf32BeBom))
            {
                return DecodeWithBom(bytes, Utf32BeBom.Length, StrictUtf32Be, DetectedTextEncoding.Utf32BigEndian);
            }

            if (StartsWith(bytes, Utf8Bom))
            {
                return DecodeWithBom(bytes, Utf8Bom.Length, StrictUtf8, DetectedTextEncoding.Utf8WithBom);
            }

            if (StartsWith(bytes, Utf16LeBom))
            {
                return DecodeWithBom(bytes, Utf16LeBom.Length, StrictUtf16Le, DetectedTextEncoding.Utf16LittleEndian);
            }

            if (StartsWith(bytes, Utf16BeBom))
            {
                return DecodeWithBom(bytes, Utf16BeBom.Length, StrictUtf16Be, DetectedTextEncoding.Utf16BigEndian);
            }

            // BOM なし: UTF-8 → Shift_JIS の順で厳格に復号を試す（順序の理由はクラスコメント参照）
            if (TryDecode(bytes, 0, StrictUtf8, out var utf8Text))
            {
                return new TextDecodeResult(DetectedTextEncoding.Utf8, utf8Text);
            }

            if (TryDecode(bytes, 0, StrictShiftJis, out var shiftJisText))
            {
                return new TextDecodeResult(DetectedTextEncoding.ShiftJis, shiftJisText);
            }

            return new TextDecodeResult(DetectedTextEncoding.Undecidable, null);
        }

        /// <summary>
        /// 判別結果の表示名を返す（ログ・エラー文言用）
        /// </summary>
        public static string GetDisplayName(DetectedTextEncoding encoding)
        {
            switch (encoding)
            {
                case DetectedTextEncoding.Utf8WithBom:
                    return "UTF-8（BOM付き）";
                case DetectedTextEncoding.Utf8:
                    return "UTF-8（BOMなし）";
                case DetectedTextEncoding.ShiftJis:
                    return "Shift_JIS";
                case DetectedTextEncoding.Utf16LittleEndian:
                    return "UTF-16 LE";
                case DetectedTextEncoding.Utf16BigEndian:
                    return "UTF-16 BE";
                case DetectedTextEncoding.Utf32LittleEndian:
                    return "UTF-32 LE";
                case DetectedTextEncoding.Utf32BigEndian:
                    return "UTF-32 BE";
                default:
                    return "判別不能";
            }
        }

        /// <summary>
        /// BOM が示す文字コードで復号する。復号できない場合は判別不能とする
        /// </summary>
        /// <remarks>
        /// BOM が UTF-8 を名乗っていても中身が壊れている場合はある。
        /// そこで置換文字を混ぜて読み進めると、化けたデータが 6 年保存の台帳へ入るため、
        /// 「BOM で名乗ったからには厳格に読める」ことを条件にする。
        /// </remarks>
        private static TextDecodeResult DecodeWithBom(
            byte[] bytes, int bomLength, Encoding encoding, DetectedTextEncoding detected)
        {
            return TryDecode(bytes, bomLength, encoding, out var text)
                ? new TextDecodeResult(detected, text)
                : new TextDecodeResult(DetectedTextEncoding.Undecidable, null);
        }

        private static bool TryDecode(byte[] bytes, int offset, Encoding encoding, out string text)
        {
            try
            {
                text = encoding.GetString(bytes, offset, bytes.Length - offset);
                return true;
            }
            catch (ArgumentException)
            {
                // DecoderFallbackException は ArgumentException を継承する。
                // UTF-16 / UTF-32 でバイト数が文字幅の倍数でない場合も同系統の例外になる。
                text = null;
                return false;
            }
        }

        private static bool StartsWith(byte[] bytes, byte[] prefix)
        {
            if (bytes.Length < prefix.Length)
            {
                return false;
            }

            for (var i = 0; i < prefix.Length; i++)
            {
                if (bytes[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
