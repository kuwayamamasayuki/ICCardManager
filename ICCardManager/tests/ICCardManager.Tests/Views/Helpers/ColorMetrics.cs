using System;
using System.Globalization;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// 色の知覚的な距離を測る計算（Issue #1855）。
/// </summary>
/// <remarks>
/// <para>
/// 「リソースキーが違えば色も違う」は成り立たないため、パレットの回帰は
/// キーの一意性ではなく<b>解決後の色値の距離</b>で表明する必要がある。
/// <c>Application.Current.TryFindResource</c> は WPF（STA スレッド・
/// <c>Application</c> 実体）に依存して xUnit から呼べないため、
/// <c>AccessibilityStyles.xaml</c> をテキストとして読んで色値を取り出し、
/// ここで距離を計算する。
/// </para>
/// <para>
/// WPF に依存しない純粋な計算に切り出してあるので、しきい値そのものの妥当性も
/// 既知の入力（旧パレットの衝突ペア等）で固定できる。
/// </para>
/// </remarks>
internal static class ColorMetrics
{
    /// <summary>
    /// 色覚多様性シミュレーションの型。
    /// </summary>
    public enum ColorVisionType
    {
        /// <summary>1 型（赤錐体異常）</summary>
        Protanopia,

        /// <summary>2 型（緑錐体異常）</summary>
        Deuteranopia,

        /// <summary>3 型（青錐体異常）</summary>
        Tritanopia,
    }

    /// <summary>
    /// 全シミュレーション型。テストが型を数え漏らさないようここから導出する。
    /// </summary>
    public static readonly ColorVisionType[] AllColorVisionTypes =
    {
        ColorVisionType.Protanopia,
        ColorVisionType.Deuteranopia,
        ColorVisionType.Tritanopia,
    };

    /// <summary>
    /// Machado et al. (2009) の重症度 1.0 の変換行列（行優先）。
    /// </summary>
    private static readonly double[][] Protanopia =
    {
        new[] { 0.152286, 1.052583, -0.204868 },
        new[] { 0.114503, 0.786281, 0.099216 },
        new[] { -0.003882, -0.048116, 1.051998 },
    };

    private static readonly double[][] Deuteranopia =
    {
        new[] { 0.367322, 0.860646, -0.227968 },
        new[] { 0.280085, 0.672501, 0.047413 },
        new[] { -0.011820, 0.042940, 0.968881 },
    };

    private static readonly double[][] Tritanopia =
    {
        new[] { 1.255528, -0.076749, -0.178779 },
        new[] { -0.078411, 0.930809, 0.147602 },
        new[] { 0.004733, 0.691367, 0.303900 },
    };

    /// <summary>
    /// <c>#RRGGBB</c> 形式の色値を 0〜255 の RGB へ変換する。
    /// </summary>
    /// <exception cref="FormatException">形式が想定と異なるとき。</exception>
    public static (double R, double G, double B) ParseHex(string hex)
    {
        if (hex == null)
        {
            throw new FormatException("色値が null です。");
        }

        var body = hex.StartsWith("#", StringComparison.Ordinal) ? hex.Substring(1) : hex;

        // WPF は #AARRGGBB も受け付ける。将来その形で系列色が定義されたとき、
        // ここで弾くと呼び出し元には「キーが見つからない」としか見えず原因に辿り着けない
        if (body.Length == 8)
        {
            body = body.Substring(2);
        }

        if (body.Length != 6)
        {
            throw new FormatException($"#RRGGBB または #AARRGGBB 形式ではありません: {hex}");
        }

        return (
            int.Parse(body.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(body.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(body.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// WCAG 2.1 の相対輝度（0〜1）。グレースケール印刷・ロービジョンでの分離を測る指標。
    /// </summary>
    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return 0.2126 * ToLinear(r) + 0.7152 * ToLinear(g) + 0.0722 * ToLinear(b);
    }

    /// <summary>
    /// 白背景（#FFFFFF）に対する WCAG コントラスト比。
    /// </summary>
    public static double ContrastAgainstWhite(string hex)
        => Contrast(hex, "#FFFFFF");

    /// <summary>
    /// CIE76 の色差 ΔE（CIE L*a*b*、D65）。
    /// </summary>
    public static double DeltaE(string hexA, string hexB)
    {
        var a = ToLab(hexA);
        var b = ToLab(hexB);
        return Math.Sqrt(
            ((a.L - b.L) * (a.L - b.L))
            + ((a.A - b.A) * (a.A - b.A))
            + ((a.B - b.B) * (a.B - b.B)));
    }

    /// <summary>
    /// 指定した色覚型でシミュレーションした色を <c>#RRGGBB</c> で返す。
    /// </summary>
    /// <remarks>
    /// <b>変換行列は線形 RGB 上で定義されている</b>ため、ガンマ補正された sRGB 値へ
    /// そのまま掛けてはいけない（Machado et al. 2009）。逆ガンマ → 行列 → 再ガンマの順で適用する。
    /// ガンマ空間で掛けると別のモデルを計算していることになり、しきい値の根拠が失われる。
    /// </remarks>
    public static string Simulate(string hex, ColorVisionType type)
    {
        var m = GetMatrix(type);
        var (r, g, b) = ParseHex(hex);

        var lr = ToLinear(r);
        var lg = ToLinear(g);
        var lb = ToLinear(b);

        var sr = ToSrgb255((m[0][0] * lr) + (m[0][1] * lg) + (m[0][2] * lb));
        var sg = ToSrgb255((m[1][0] * lr) + (m[1][1] * lg) + (m[1][2] * lb));
        var sb = ToSrgb255((m[2][0] * lr) + (m[2][1] * lg) + (m[2][2] * lb));

        return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", sr, sg, sb);
    }

    /// <summary>
    /// 2 色の WCAG コントラスト比（明暗どちらが引数でも同じ値）。
    /// </summary>
    public static double Contrast(string hexA, string hexB)
    {
        var a = RelativeLuminance(hexA);
        var b = RelativeLuminance(hexB);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// 全色覚型でシミュレーションしたときの ΔE の最小値。
    /// </summary>
    /// <remarks>
    /// 「どれか 1 つの型で潰れる」ことを検出したいので、最小値（最悪ケース）で評価する。
    /// </remarks>
    public static double MinDeltaEAcrossColorVisionTypes(string hexA, string hexB)
    {
        var min = double.MaxValue;
        foreach (var type in AllColorVisionTypes)
        {
            var d = DeltaE(Simulate(hexA, type), Simulate(hexB, type));
            if (d < min)
            {
                min = d;
            }
        }

        return min;
    }

    private static (double L, double A, double B) ToLab(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        var lr = ToLinear(r);
        var lg = ToLinear(g);
        var lb = ToLinear(b);

        var x = ((0.4124 * lr) + (0.3576 * lg) + (0.1805 * lb)) / 0.95047;
        var y = (0.2126 * lr) + (0.7152 * lg) + (0.0722 * lb);
        var z = ((0.0193 * lr) + (0.1192 * lg) + (0.9505 * lb)) / 1.08883;

        var fx = LabF(x);
        var fy = LabF(y);
        var fz = LabF(z);

        return ((116.0 * fy) - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }

    private static double LabF(double t)
        => t > (216.0 / 24389.0) ? Math.Pow(t, 1.0 / 3.0) : ((841.0 / 108.0) * t) + (4.0 / 29.0);

    private static double ToLinear(double channel255)
    {
        var c = channel255 / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// 線形 RGB（0〜1）を sRGB の 0〜255 へ戻す。
    /// </summary>
    // .NET Framework 4.8 には Math.Clamp が無い
    private static int ToSrgb255(double linear)
    {
        var v = Math.Max(0.0, Math.Min(1.0, linear));
        var encoded = v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1.0 / 2.4)) - 0.055;
        return (int)Math.Round(encoded * 255.0, MidpointRounding.AwayFromZero);
    }

    private static double[][] GetMatrix(ColorVisionType type)
    {
        switch (type)
        {
            case ColorVisionType.Protanopia:
                return Protanopia;
            case ColorVisionType.Deuteranopia:
                return Deuteranopia;
            case ColorVisionType.Tritanopia:
                return Tritanopia;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "未知の色覚型です。");
        }
    }
}
