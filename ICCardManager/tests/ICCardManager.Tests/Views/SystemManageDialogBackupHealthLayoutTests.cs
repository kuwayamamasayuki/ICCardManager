using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1689: システム管理ダイアログ「バックアップ状況」セクションのレイアウト回帰テスト。
/// </summary>
/// <remarks>
/// バックアップ保存先は UNC パス（<c>\\server\share\backup</c>）になり得て長文になる。
/// 文字サイズは「小/中/大/特大」の4段階で変わるため、幅の調整では特大でまた破綻する。
/// そのため長文になり得る行はすべて <c>TextWrapping="Wrap"</c> で担保し、
/// 子を無限幅で測定してしまう横方向 <c>StackPanel</c> は使わない（Issue #1687 / #1688 の原則）。
///
/// 実際の描画検証には UI オートメーションが必要なため、ここでは
/// 「折り返しを壊すレイアウトが再導入されていないか」を XAML テキスト上で静的に固定する。
/// 文字サイズ変更時の実表示は手動検証する。
/// </remarks>
public class SystemManageDialogBackupHealthLayoutTests
{
    private static readonly string SystemManageDialogXamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "SystemManageDialog.xaml"));

    [Fact]
    public void Backup_health_group_box_should_exist()
    {
        ExtractBackupHealthGroupBox().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void All_backup_health_text_blocks_should_wrap()
    {
        var section = ExtractBackupHealthGroupBox();

        // 健全性表示のバインド対象（長文になり得るもの）を列挙し、それぞれの TextBlock が Wrap を持つこと
        var boundProperties = new[]
        {
            "LastBackupSuccessText",
            "BackupGenerationText",
            "BackupFreeSpaceText",
            "BackupFolderText",
            "LastBackupMachineText",
            "LastVacuumText"
        };

        foreach (var property in boundProperties)
        {
            var textBlock = Regex.Match(
                section,
                $@"<TextBlock[^>]*Text\s*=\s*""\{{Binding\s+{property}\b[\s\S]*?(?:/>|</TextBlock>)");

            textBlock.Success.Should().BeTrue($"{property} を表示する TextBlock が存在すべき");
            textBlock.Value.Should().Contain(
                @"TextWrapping=""Wrap""",
                $"{property} は長文になり得るため折り返しで担保する（Issue #1689）");
        }
    }

    [Fact]
    public void Backup_health_lines_should_not_use_horizontal_stack_panel()
    {
        var section = ExtractBackupHealthGroupBox();

        // 横方向 StackPanel は子を無限幅で測定するため、その中では TextWrapping が効かない。
        // ボタン列（作成／フォルダを開く）だけは折り返し不要なので、健全性表示の範囲に限って検査する。
        var healthLines = section.Substring(0, section.IndexOf("任意のタイミングで", StringComparison.Ordinal));

        Regex.Matches(healthLines, @"<StackPanel[^>]*Orientation\s*=\s*""Horizontal""")
            .Cast<Match>()
            .Should().BeEmpty("折り返しが効かなくなるため、健全性表示に横方向 StackPanel を使わない（Issue #1687）");
    }

    [Fact]
    public void Backup_health_status_should_use_brush_resource_keys_not_literal_colors()
    {
        var section = ExtractBackupHealthGroupBox();

        // 色値の Single Source of Truth は AccessibilityStyles.xaml のブラシキー（Issue #1392 / #1461）
        Regex.IsMatch(section, @"Foreground\s*=\s*""#[0-9A-Fa-f]{6,8}""")
            .Should().BeFalse("色値リテラルではなく DynamicResource のブラシキーを参照する");

        section.Should().Contain("{DynamicResource DangerTextBrush}",
            "しきい値超過時は警告色を使う");
    }

    /// <summary>
    /// 「バックアップ状況」GroupBox の定義全文を抽出する。
    /// </summary>
    private static string ExtractBackupHealthGroupBox()
    {
        var xaml = File.ReadAllText(SystemManageDialogXamlPath);

        var match = Regex.Match(
            xaml,
            @"<GroupBox[^>]*Header\s*=\s*""バックアップ状況""[\s\S]*?</GroupBox>",
            RegexOptions.Compiled);

        match.Success.Should().BeTrue("SystemManageDialog.xaml に「バックアップ状況」の GroupBox が存在すべき");
        return match.Value;
    }
}
