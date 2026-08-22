using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1822: <c>LedgerDetailDialog.xaml</c> がグループ配色を色値リテラル
/// （<c>&lt;SolidColorBrush Color="#E3F2FD"/&gt;</c> 等）で直接定義していた回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// Issue #1392 / #1461 で確立した「色値の Single Source of Truth は
/// <c>Resources/Styles/AccessibilityStyles.xaml</c>」という規約から外れており、
/// うち 6 値は既存の状態ブラシと重複していた。
/// </para>
/// <para>
/// 個別ファイルを名指しで検査すると、同じ形を持つ画面が追加されたときに静かに漏れる
/// （<c>.claude/rules/development-conventions.md</c> #1786「ガードを書くときは経路を列挙する」）。
/// 走査対象は <c>Views/</c> 配下の XAML から機械的に導出する。
/// </para>
/// </remarks>
public class ColorLiteralSingleSourceOfTruthTests
{
    /// <summary>
    /// グループ配色の移設先キー。移設漏れがあればどちらかの検査が落ちる。
    /// </summary>
    private static readonly string[] MovedBrushKeys =
    {
        "LedgerGroupBackground1Brush",
        "LedgerGroupBackground2Brush",
        "LedgerGroupBackground3Brush",
        "LedgerGroupBackground4Brush",
        "LedgerGroupBackground5Brush",
        "LedgerGroupBadge1Brush",
        "LedgerGroupBadge2Brush",
        "LedgerGroupBadge3Brush",
        "LedgerGroupBadge4Brush",
        "LedgerGroupBadge5Brush",
    };

    /// <summary>
    /// 属性値として書かれた色値リテラル（<c>Foo="#RGB"</c>〜<c>Foo="#AARRGGBB"</c>）。
    /// </summary>
    private static readonly Regex ColorLiteralPattern =
        new Regex("[A-Za-z0-9_.:]+\\s*=\\s*\"#[0-9A-Fa-f]{3,8}\"", RegexOptions.Compiled);

    /// <summary>
    /// XAML コメントを除去する。
    /// </summary>
    /// <remarks>
    /// 「色値リテラルを直書きしない」という規約の理由を述べたコメント自体が違反として
    /// 検出される極性の反転を避けるため（<c>.claude/rules/development-conventions.md</c> #1692）。
    /// </remarks>
    private static string StripXamlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void Views配下のXamlに色値リテラルが直書きされていないこと()
    {
        var viewsRoot = Path.GetDirectoryName(
            ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml")))!;

        var xamlFiles = Directory.GetFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories);

        // 空振り検出: 走査対象が実在すること（対象の抽出が壊れると検査が静かに無効化される）
        xamlFiles.Should().HaveCountGreaterThan(
            10, "Views 配下の XAML 走査が空振りしていないこと");

        var violations = xamlFiles
            .Select(path => (Path: path, Text: StripXamlComments(File.ReadAllText(path))))
            // Color="#..." だけでなく Background / Foreground / BorderBrush 等、
            // ブラシを取りうる全属性の色値リテラルを対象にする
            // （検査を Color= に絞ると、より一般的な Background="#FFF3E0" の形が素通りする）。
            .Where(f => ColorLiteralPattern.IsMatch(f.Text))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();

        violations.Should().BeEmpty(
            "色値は AccessibilityStyles.xaml のブラシキーを DynamicResource で参照すること" +
            "（Issue #1392 / #1461 / #1822）。違反: " + string.Join(", ", violations));
    }

    [Fact]
    public void 移設したグループ配色キーがAccessibilityStylesに定義されていること()
    {
        var styles = File.ReadAllText(ViewSourceLocator.Resolve(
            Path.Combine("Resources", "Styles", "AccessibilityStyles.xaml")));

        foreach (var key in MovedBrushKeys)
        {
            styles.Should().Contain(
                $"x:Key=\"{key}\"",
                $"{key} は色値 SSOT である AccessibilityStyles.xaml に定義されていること（Issue #1822）");
        }
    }

    [Fact]
    public void 履歴詳細ダイアログがグループ配色をDynamicResourceで参照すること()
    {
        var dialog = File.ReadAllText(ViewSourceLocator.Resolve(
            Path.Combine("Views", "Dialogs", "LedgerDetailDialog.xaml")));

        foreach (var key in MovedBrushKeys)
        {
            dialog.Should().Contain(
                $"{{DynamicResource {key}}}",
                $"{key} は StaticResource ではなく DynamicResource で参照すること（Issue #1461 / #1822）");
        }

        // 旧キーが残っていないこと（新旧併存の中途半端な状態を許さない）
        dialog.Should().NotContain(
            "GroupBadgeColor",
            "移設前のローカルキー（GroupBadgeColor1-5）は残さないこと");
        dialog.Should().NotContain(
            "StaticResource GroupColor",
            "移設前のローカルキー（GroupColor0-5）への参照は残さないこと");
    }
}
