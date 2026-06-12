using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
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

    private static string ResolveViewsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Views ディレクトリを {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
