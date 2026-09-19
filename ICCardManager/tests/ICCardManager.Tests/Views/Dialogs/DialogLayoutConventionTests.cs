using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1616: 文字サイズ設定（小/中/大/特大）への追従と、ダイアログのリサイズ可否に関する
/// XAML 規約の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// FontSize の数値ハードコードは、文字サイズ設定（AccessibilityStyles.xaml の
/// BaseFontSize / SmallFontSize 等のキーを App.xaml.cs が動的更新する仕組み）に
/// 追従しないため禁止。必ず DynamicResource 経由でフォントサイズキーを参照すること。
/// </para>
/// <para>
/// ResizeMode="NoResize" のダイアログは、特大文字で長い職員名・カード種別名の折返しが
/// 多発しても利用者がウィンドウを広げる手段がなく手詰まりになるため禁止。
/// SizeToContent="Height" と ResizeMode="CanResize" の併用は WPF のサポートされた
/// パターンで、利用者が手動リサイズした時点で SizeToContent が Manual に切り替わり、
/// 以後は利用者の指定サイズが維持される。
/// </para>
/// <para>
/// 実際の特大文字設定でのレイアウト確認は UI 自動化を要するため PR テストプランで
/// 手動検証する。本テストは XAML 上の規約違反を早期検出する静的セーフティネット
/// （Issue #1468 で確立した静的解析方式を踏襲）。
/// </para>
/// </remarks>
public class DialogLayoutConventionTests
{
    private static readonly string ViewsDirectory = ResolveViewsDirectory();

    private static string DialogsDirectory => Path.Combine(ViewsDirectory, "Dialogs");

    /// <summary>
    /// Views 配下の全 XAML で、FontSize に数値リテラルを直接指定してはならない。
    /// </summary>
    [Fact]
    public void Viewsの全XAMLにFontSize数値ハードコードが存在しないこと()
    {
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(ViewsDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                // FontSize="11" のような数値リテラル指定を検出する。
                // FontSize="{DynamicResource BaseFontSize}" は先頭が '{' のためマッチしない。
                if (Regex.IsMatch(lines[i], @"FontSize\s*=\s*""\d"))
                {
                    violations.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "FontSize の数値ハードコードは文字サイズ設定（小/中/大/特大）に追従できない（Issue #1616）。" +
            "AccessibilityStyles.xaml のフォントサイズキー（BaseFontSize / SmallFontSize / " +
            "LargeFontSize / DialogIconFontSize 等）を DynamicResource で参照すること。" +
            "違反箇所: " + string.Join(" / ", violations));
    }

    /// <summary>
    /// ダイアログの Window で ResizeMode="NoResize" を使用してはならない。
    /// </summary>
    [Fact]
    public void ダイアログのWindowにResizeMode_NoResizeを使用しないこと()
    {
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(DialogsDirectory, "*.xaml", SearchOption.TopDirectoryOnly))
        {
            var windowTag = ExtractWindowOpeningTag(File.ReadAllText(path));
            if (Regex.IsMatch(windowTag, @"ResizeMode\s*=\s*""NoResize"""))
            {
                violations.Add(Path.GetFileName(path));
            }
        }

        violations.Should().BeEmpty(
            "ResizeMode=\"NoResize\" のダイアログは、文字サイズ「特大」で折返しが多発しても" +
            "利用者がウィンドウを広げられず手詰まりになる（Issue #1616）。" +
            "ResizeMode=\"CanResize\" とし、MinWidth / MinHeight で最小サイズを保証すること。" +
            "違反ダイアログ: " + string.Join(", ", violations));
    }

    /// <summary>
    /// SizeToContent を使用するダイアログは、リサイズで操作不能な極小サイズに
    /// 縮められないよう MinWidth / MinHeight を宣言しなければならない。
    /// </summary>
    [Fact]
    public void SizeToContentを使用するダイアログはMinWidthとMinHeightを宣言していること()
    {
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(DialogsDirectory, "*.xaml", SearchOption.TopDirectoryOnly))
        {
            var windowTag = ExtractWindowOpeningTag(File.ReadAllText(path));
            if (!Regex.IsMatch(windowTag, @"SizeToContent\s*=\s*"""))
            {
                continue;
            }

            var hasMinWidth = Regex.IsMatch(windowTag, @"\bMinWidth\s*=\s*""");
            var hasMinHeight = Regex.IsMatch(windowTag, @"\bMinHeight\s*=\s*""");
            if (!hasMinWidth || !hasMinHeight)
            {
                violations.Add($"{Path.GetFileName(path)} (MinWidth={hasMinWidth}, MinHeight={hasMinHeight})");
            }
        }

        violations.Should().BeEmpty(
            "SizeToContent を使用するリサイズ可能ダイアログは、MinWidth / MinHeight を併記して" +
            "操作不能な極小サイズへの縮小を防ぐこと（Issue #1616）。違反ダイアログ: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// ダイアログの初期 <c>Height</c> と <c>MinHeight</c> は、1366×768 の実用高さ 720 を超えてはならない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2076: 03_画面設計書 §5.6 は「<c>MinHeight ≤ 720</c>」と「初期 Height が実用高さを
    /// 超える場合は適切な初期値に抑える（SettingsDialog は 820 → 680）」を定めているが、
    /// どちらも検査が無く、<c>SystemManageDialog</c> が <c>Height="860"</c> / <c>MinHeight="700"</c> で
    /// 残っていた。ノート PC では「閉じる」やリストアのボタンが画面外に出て、押す手段が無い。
    /// </para>
    /// <para>
    /// 走査対象は <c>Views/Dialogs/</c> の全 XAML から導出する（ファイル名で列挙すると
    /// ダイアログが増えたときに静かに漏れる。#1786）。<c>SizeToContent="Height"</c> の
    /// ダイアログは <c>Height</c> を持たないので、その場合は <c>MaxHeight</c> が同じ役割を担う。
    /// </para>
    /// </remarks>
    [Fact]
    public void ダイアログの初期高さと最小高さが低解像度の実用高さを超えないこと()
    {
        var violations = new List<string>();

        foreach (var (name, windowTag) in EnumerateDialogWindowTags())
        {
            foreach (var attribute in new[] { "Height", "MaxHeight", "MinHeight" })
            {
                var value = ReadNumericAttribute(windowTag, attribute);
                if (value > PracticalScreenHeight)
                {
                    violations.Add($"{name} ({attribute}={value})");
                }
            }
        }

        violations.Should().BeEmpty(
            "Issue #2076: 1366×768 のノート PC（タスクバーを除いた実用高さ " + PracticalScreenHeight + "）で" +
            "ボタンが画面外に出ると押す手段が無い。03_画面設計書 §5.6 に従い初期値・最小値を抑えること。" +
            "違反: " + string.Join(", ", violations));
    }

    /// <summary>
    /// 高さの検査が空振りしていないことを、実在するダイアログの値で固定する。
    /// </summary>
    /// <remarks>
    /// 「違反ゼロ」だけを見ると、属性の照合が縮んで 1 件も読めなくなった状態と区別できない（#1786）。
    /// </remarks>
    [Fact]
    public void 高さの検査がダイアログの実際の値を読めていること()
    {
        var heights = EnumerateDialogWindowTags()
            .ToDictionary(t => t.Name, t => ReadNumericAttribute(t.WindowTag, "Height"), StringComparer.Ordinal);

        heights.Should().ContainKey("SettingsDialog.xaml")
            .WhoseValue.Should().Be(680, "§5.6 が 820 → 680 に抑えた実例として明記している値");
        heights.Should().ContainKey("SystemManageDialog.xaml")
            .WhoseValue.Should().Be(680, "Issue #2076 で是正した対象が読めていること");
        heights["CardTypeSelectionDialog.xaml"].Should().BeNull(
            "SizeToContent=\"Height\" のダイアログは Height を持たない — " +
            "未指定を 0 へ畳めると上限の無いダイアログを見逃すため、null のまま区別する");
        heights.Count.Should().BeGreaterThan(10, "Views/Dialogs/ の全ダイアログが走査対象であること");
    }

    /// <summary>
    /// <c>SizeToContent</c> で高さが伸びるダイアログは、上限（<c>MaxHeight</c>）を宣言しなければならない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2076 のコードレビューで検出。<c>SizeToContent="Height"</c> は内容の高さに合わせて
    /// ウィンドウを伸ばすため、<c>Height</c> を持たない — つまり上の検査（高さ ≤ 720）は
    /// **この形のダイアログを 1 件も見ていなかった**。上限が無ければ文字サイズ「特大」で
    /// 画面高さを超え、本 Issue が直している欠陥そのものになる。
    /// </para>
    /// <para>
    /// ガードは「守りたい性質」ではなく「その性質を破れる全経路」を列挙して書く（#1786）。
    /// 高さの上限を決める経路は <c>Height</c>（固定）と <c>MaxHeight</c>（<c>SizeToContent</c>）の 2 つある。
    /// </para>
    /// </remarks>
    [Fact]
    public void SizeToContentで伸びるダイアログが高さの上限を宣言していること()
    {
        var violations = new List<string>();

        foreach (var (name, windowTag) in EnumerateDialogWindowTags())
        {
            var sizeToContent = XamlElementInspection.GetAttribute(windowTag, "SizeToContent");
            if (sizeToContent == null
                || sizeToContent.IndexOf("Height", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            if (ReadNumericAttribute(windowTag, "MaxHeight") == null)
            {
                violations.Add(name);
            }
        }

        violations.Should().BeEmpty(
            "Issue #2076: SizeToContent=\"Height\" は内容の高さに合わせて伸びるため、MaxHeight が無いと" +
            "画面高さを超え得る（Height を持たないので高さ ≤ 720 の検査も素通りする）。違反: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// 上限の検査が空振りしていないことを、実在する <c>SizeToContent</c> ダイアログの数で固定する。
    /// </summary>
    [Fact]
    public void SizeToContentのダイアログが実際に走査されていること()
    {
        var sizeToContentDialogs = EnumerateDialogWindowTags()
            .Where(t => XamlElementInspection.GetAttribute(t.WindowTag, "SizeToContent") != null)
            .Select(t => t.Name)
            .ToList();

        sizeToContentDialogs.Should().Contain(new[]
        {
            "CardRegistrationModeDialog.xaml",
            "CardTypeSelectionDialog.xaml",
            "StaffAuthDialog.xaml",
        }, "SizeToContent を使う 3 つのダイアログが走査対象に含まれていること" +
           "（含まれていなければ上の検査は何も見ていない）");
    }

    /// <summary>タスクバーを除いた 1366×768 の実用高さ（03_画面設計書 §5.6）。</summary>
    private const double PracticalScreenHeight = 720;

    private static IEnumerable<(string Name, string WindowTag)> EnumerateDialogWindowTags()
        => Directory.EnumerateFiles(DialogsDirectory, "*.xaml", SearchOption.TopDirectoryOnly)
            .Select(path => (Path.GetFileName(path), ExtractWindowOpeningTag(File.ReadAllText(path))));

    /// <summary>開始タグから数値属性を読む。未指定・非数値（バインディング等）は null を返す。</summary>
    /// <remarks>
    /// 属性の切り出しは <see cref="XamlElementInspection.GetAttribute"/> へ委譲する。
    /// 私的コピーを書くと、そのコピーが既に解決済みだった欠陥（単引用符の属性・
    /// 属性名の境界判定）を再現する（testing.md「検査の下請け処理の複製をこれ以上増やさない」）。
    /// **未指定を 0 へ畳まない** — 0 にすると「指定が無い」と「十分小さい」が区別できず、
    /// 上限の無い <c>SizeToContent="Height"</c> のダイアログが検査を素通りする（コードレビューで検出）。
    /// </remarks>
    private static double? ReadNumericAttribute(string windowTag, string attributeName)
    {
        var raw = XamlElementInspection.GetAttribute(windowTag, attributeName);
        return raw != null
               && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : (double?)null;
    }

    /// <summary>
    /// Window 要素の開始タグ（&lt;Window ... &gt; の最初の &gt; まで）を抽出する。
    /// </summary>
    /// <remarks>
    /// XAML の属性値は引用符で囲まれ、開始タグ内に裸の <c>&gt;</c> は出現しないため、
    /// 最初の <c>&gt;</c> までを開始タグとみなしてよい（Issue #1503 と同じ前提）。
    /// </remarks>
    private static string ExtractWindowOpeningTag(string xaml)
    {
        var start = xaml.IndexOf("<Window", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "ダイアログ XAML のルートは Window 要素であるべき");

        var end = xaml.IndexOf('>', start);
        end.Should().BeGreaterThan(start, "Window 開始タグが閉じられているべき");

        return xaml.Substring(start, end - start + 1);
    }

    private static string ResolveViewsDirectory() => ViewSourceLocator.ResolveDirectory("Views");
}
