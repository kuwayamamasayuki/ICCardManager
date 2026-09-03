using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 設定画面の「同行者数入力を自動的に閉じるまでの秒数」欄の静的検査（Issue #2009）。
/// </summary>
/// <remarks>
/// この欄は `Window` のコードビハインドを実体化しないと動作を確かめられない（STA 依存で
/// xUnit から実行できない）ため、XAML のテキスト上で固定する（#1817 / #1794 と同じ形）。
/// 検査は「禁止された形の不在」ではなく**正しい形の存在**を表明する — 兄弟の数値欄
/// （残額警告しきい値、#1279）は `NumericRangeValidationRule` を持っており、これが無いと
/// 非数値の入力がバインディングで黙って捨てられ、赤枠も出ないまま前の値が保存される。
/// </remarks>
public class CompanionCountTimeoutSettingLayoutTests
{
    private static string ReadSettingsDialogXaml()
    {
        var path = Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Views", "Dialogs", "SettingsDialog.xaml");
        File.Exists(path).Should().BeTrue($"検査対象の XAML が見つからない: {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractTimeoutTextBox(string xaml)
    {
        var match = Regex.Match(
            xaml,
            "<TextBox\\s+x:Name=\"CompanionCountTimeoutTextBox\".*?</TextBox>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("秒数入力欄（CompanionCountTimeoutTextBox）が設定画面に存在すること");
        return match.Value;
    }

    [Fact]
    public void 秒数欄は数値範囲の検証ルールを持つこと()
    {
        var textBox = ExtractTimeoutTextBox(ReadSettingsDialogXaml());

        textBox.Should().Contain("NumericRangeValidationRule",
            "非数値を入力したとき、バインディングが黙って値を捨てて前の値のまま保存されるのを防ぐ（#1279）");
        textBox.Should().Contain("Min=\"0\"", "0 は「自動的に閉じない」の指定として許可する");
        textBox.Should().Contain("Max=\"300\"", "上限は AppConstants.MaxCompanionCountInputTimeoutSeconds と一致させる");
    }

    [Fact]
    public void 秒数欄はスキップ設定が有効なときは操作できないこと()
    {
        var textBox = ExtractTimeoutTextBox(ReadSettingsDialogXaml());

        // スキップが有効ならダイアログ自体が出ないため、この値は使われない
        textBox.Should().Contain("IsEnabled=\"{Binding SkipCompanionCountInputOnReturn, Converter={StaticResource InverseBooleanConverter}}\"");
    }
}
