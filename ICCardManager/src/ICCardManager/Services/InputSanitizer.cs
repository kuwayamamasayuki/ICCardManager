using System.Text;
using System.Text.RegularExpressions;

namespace ICCardManager.Services;

/// <summary>
/// サニタイズオプション
/// </summary>
[Flags]
public enum SanitizeOptions
{
    /// <summary>
    /// なし
    /// </summary>
    None = 0,

    /// <summary>
    /// 前後の空白を削除
    /// </summary>
    Trim = 1,

    /// <summary>
    /// 制御文字を削除
    /// </summary>
    RemoveControlCharacters = 2,

    /// <summary>
    /// ゼロ幅文字を削除
    /// </summary>
    RemoveZeroWidthCharacters = 4,

    /// <summary>
    /// 不正なサロゲートペアを削除
    /// </summary>
    RemoveInvalidSurrogates = 8,

    /// <summary>
    /// 連続する空白を単一に正規化
    /// </summary>
    NormalizeWhitespace = 16,

    /// <summary>
    /// 標準的なサニタイズ（すべてのオプションを適用）
    /// </summary>
    Standard = Trim | RemoveControlCharacters | RemoveZeroWidthCharacters | RemoveInvalidSurrogates | NormalizeWhitespace
}

/// <summary>
/// 入力値サニタイズサービス
/// </summary>
/// <remarks>
/// ユーザー入力から危険な文字や不正なUnicodeを除去し、
/// 安全な文字列に変換します。
/// XAMLやExcel出力時の表示問題を防止します。
/// </remarks>
public static partial class InputSanitizer
{
    #region 正規表現パターン

    /// <summary>
    /// 制御文字パターン（U+0000-U+001F, U+007F-U+009F）
    /// タブ(0x09)、改行(0x0A)、キャリッジリターン(0x0D)は除外
    /// </summary>
    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]", RegexOptions.Compiled)]
    private static partial Regex ControlCharactersPattern();

    /// <summary>
    /// ゼロ幅文字パターン（U+200B-U+200D, U+FEFF, U+2060）
    /// </summary>
    [GeneratedRegex(@"[\u200B-\u200D\uFEFF\u2060]", RegexOptions.Compiled)]
    private static partial Regex ZeroWidthCharactersPattern();

    /// <summary>
    /// 連続する空白パターン
    /// </summary>
    [GeneratedRegex(@"[ \t]+", RegexOptions.Compiled)]
    private static partial Regex MultipleWhitespacePattern();

    #endregion

    #region パブリックメソッド

    /// <summary>
    /// 入力文字列をサニタイズ
    /// </summary>
    /// <param name="input">入力文字列</param>
    /// <param name="options">サニタイズオプション</param>
    /// <returns>サニタイズされた文字列</returns>
    public static string Sanitize(string? input, SanitizeOptions options = SanitizeOptions.Standard)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = input;

        // 不正なサロゲートペアを削除
        if (options.HasFlag(SanitizeOptions.RemoveInvalidSurrogates))
        {
            result = RemoveInvalidSurrogates(result);
        }

        // 制御文字を削除
        if (options.HasFlag(SanitizeOptions.RemoveControlCharacters))
        {
            result = ControlCharactersPattern().Replace(result, string.Empty);
        }

        // ゼロ幅文字を削除
        if (options.HasFlag(SanitizeOptions.RemoveZeroWidthCharacters))
        {
            result = ZeroWidthCharactersPattern().Replace(result, string.Empty);
        }

        // 連続する空白を単一に正規化
        if (options.HasFlag(SanitizeOptions.NormalizeWhitespace))
        {
            result = MultipleWhitespacePattern().Replace(result, " ");
        }

        // 前後の空白を削除
        if (options.HasFlag(SanitizeOptions.Trim))
        {
            result = result.Trim();
        }

        return result;
    }

    /// <summary>
    /// 職員名をサニタイズ
    /// </summary>
    /// <param name="name">職員名</param>
    /// <returns>サニタイズされた職員名</returns>
    /// <remarks>
    /// 許可文字: 日本語（ひらがな、カタカナ、漢字）、英数字、スペース、ハイフン、中点
    /// 最大長: 50文字
    /// </remarks>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        // 標準サニタイズを適用
        var sanitized = Sanitize(name, SanitizeOptions.Standard);

        // 最大長で切り詰め
        if (sanitized.Length > 50)
        {
            sanitized = sanitized[..50];
        }

        return sanitized;
    }

    /// <summary>
    /// 職員番号をサニタイズ
    /// </summary>
    /// <param name="number">職員番号</param>
    /// <returns>サニタイズされた職員番号</returns>
    /// <remarks>
    /// 許可文字: 英数字、ハイフン
    /// 最大長: 20文字
    /// </remarks>
    public static string SanitizeStaffNumber(string? number)
    {
        if (string.IsNullOrEmpty(number))
        {
            return string.Empty;
        }

        // 標準サニタイズを適用
        var sanitized = Sanitize(number, SanitizeOptions.Standard);

        // 最大長で切り詰め
        if (sanitized.Length > 20)
        {
            sanitized = sanitized[..20];
        }

        return sanitized;
    }

    /// <summary>
    /// 備考をサニタイズ
    /// </summary>
    /// <param name="note">備考</param>
    /// <returns>サニタイズされた備考</returns>
    /// <remarks>
    /// 許可文字: 日本語、英数字、基本記号
    /// 最大長: 200文字
    /// </remarks>
    public static string SanitizeNote(string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return string.Empty;
        }

        // 標準サニタイズを適用
        var sanitized = Sanitize(note, SanitizeOptions.Standard);

        // 最大長で切り詰め
        if (sanitized.Length > 200)
        {
            sanitized = sanitized[..200];
        }

        return sanitized;
    }

    /// <summary>
    /// バス停名をサニタイズ
    /// </summary>
    /// <param name="busStops">バス停名</param>
    /// <returns>サニタイズされたバス停名</returns>
    /// <remarks>
    /// 許可文字: 日本語、英数字、記号
    /// 最大長: 100文字
    /// </remarks>
    public static string SanitizeBusStops(string? busStops)
    {
        if (string.IsNullOrEmpty(busStops))
        {
            return string.Empty;
        }

        // 標準サニタイズを適用
        var sanitized = Sanitize(busStops, SanitizeOptions.Standard);

        // 最大長で切り詰め
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100];
        }

        return sanitized;
    }

    /// <summary>
    /// カード管理番号をサニタイズ
    /// </summary>
    /// <param name="cardNumber">カード管理番号</param>
    /// <returns>サニタイズされたカード管理番号</returns>
    /// <remarks>
    /// 許可文字: 英数字、ハイフン
    /// 最大長: 20文字
    /// </remarks>
    public static string SanitizeCardNumber(string? cardNumber)
    {
        if (string.IsNullOrEmpty(cardNumber))
        {
            return string.Empty;
        }

        // 標準サニタイズを適用
        var sanitized = Sanitize(cardNumber, SanitizeOptions.Standard);

        // 最大長で切り詰め
        if (sanitized.Length > 20)
        {
            sanitized = sanitized[..20];
        }

        return sanitized;
    }

    #endregion

    #region プライベートメソッド

    /// <summary>
    /// 不正なサロゲートペアを削除
    /// </summary>
    /// <param name="input">入力文字列</param>
    /// <returns>不正なサロゲートを除去した文字列</returns>
    private static string RemoveInvalidSurrogates(string input)
    {
        var sb = new StringBuilder(input.Length);

        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (char.IsHighSurrogate(c))
            {
                // 次の文字が低位サロゲートかチェック
                if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    // 正常なサロゲートペア
                    sb.Append(c);
                    sb.Append(input[i + 1]);
                    i++; // 低位サロゲートをスキップ
                }
                // 不正な高位サロゲート（ペアがない）は除去
            }
            else if (char.IsLowSurrogate(c))
            {
                // 不正な低位サロゲート（前に高位がない）は除去
            }
            else
            {
                // 通常の文字
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    #endregion
}
