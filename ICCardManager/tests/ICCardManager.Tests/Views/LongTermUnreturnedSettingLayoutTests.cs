using System.Globalization;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 設定画面の「長期未返却とみなす日数」欄の静的検査（Issue #2152）。
/// </summary>
/// <remarks>
/// この欄は `Window` を実体化しないと動作を確かめられない（STA 依存で xUnit から実行できない）ため、
/// XAML のテキスト上で固定する（<see cref="CompanionCountTimeoutSettingLayoutTests"/> と同じ形）。
/// 入力時の範囲（<c>NumericRangeValidationRule</c> の Min/Max）は XAML のリテラルで、保存時の範囲
/// （<c>ValidationService.ValidateLongTermUnreturnedDays</c>）は <see cref="AppConstants"/> の定数で
/// 書かれている。片方だけ動かすと「入力時は妥当に見えるのに保存時に弾かれる」（#2009 で実際に踏んだ形）
/// ため、両者の一致をここで固定する。
/// </remarks>
public class LongTermUnreturnedSettingLayoutTests
{
    private const string TextBoxName = "LongTermUnreturnedDaysTextBox";

    private static string ReadSettingsDialog(string fileName)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "Dialogs", fileName);
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");
        return File.ReadAllText(path);
    }

    private static XamlElementInspection.XamlElement FindDaysTextBox()
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadSettingsDialog("SettingsDialog.xaml"));
        var textBoxes = XamlElementInspection.EnumerateElements(xaml, "TextBox")
            .Where(e => XamlElementInspection.GetAttribute(e.StartTag, "x:Name") == TextBoxName)
            .ToList();
        textBoxes.Should().ContainSingle($"日数の入力欄（{TextBoxName}）が設定画面にちょうど 1 つ存在すること");
        return textBoxes[0];
    }

    private static string FindRangeRule(XamlElementInspection.XamlElement textBox)
    {
        var rules = XamlElementInspection.EnumerateElements(textBox.Body, "validation:NumericRangeValidationRule").ToList();
        rules.Should().ContainSingle(
            "非数値や範囲外の入力を入力時に赤枠で示す検証ルールを持つこと。無いとバインディングが黙って値を捨て、" +
            "前の値のまま保存される（#1279 / #2009）");
        return rules[0].StartTag;
    }

    [Fact]
    public void 日数欄はLongTermUnreturnedDaysへ双方向に結び付くこと()
    {
        var textBox = FindDaysTextBox();

        textBox.Body.Should().Contain("<Binding Path=\"LongTermUnreturnedDays\"",
            "ViewModel の LongTermUnreturnedDays を入力・保存の対象にする");
    }

    [Fact]
    public void 日数欄の入力時の範囲は保存時の範囲と一致すること()
    {
        var rule = FindRangeRule(FindDaysTextBox());

        int.Parse(XamlElementInspection.GetAttribute(rule, "Min")!, CultureInfo.InvariantCulture)
            .Should().Be(AppConstants.MinLongTermUnreturnedDays, "入力時の下限＝保存時の下限");
        int.Parse(XamlElementInspection.GetAttribute(rule, "Max")!, CultureInfo.InvariantCulture)
            .Should().Be(AppConstants.MaxLongTermUnreturnedDays, "入力時の上限＝保存時の上限");
    }

    /// <summary>
    /// 範囲を定数から読む表明だけでは、定数と XAML を一緒に動かす変更を検出できない（#1884）。
    /// Issue で決めた 1〜365 をリテラルで固定する。
    /// </summary>
    [Fact]
    public void 日数の範囲は1日から365日であること()
    {
        AppConstants.MinLongTermUnreturnedDays.Should().Be(1, "0 日にすると貸出中のカードがすべて長期未返却になる");
        AppConstants.MaxLongTermUnreturnedDays.Should().Be(365);
    }

    [Fact]
    public void 保存時の検証で弾かれたとき日数欄へフォーカスが移ること()
    {
        // 保存時エラーのフォーカス移動（#1279）は ViewModel の FirstErrorField をコードビハインドが
        // 入力欄へ写像する。写像が欠けるとエラー文言は出るのに入力欄へ移れない
        var codeBehind = ReadSettingsDialog("SettingsDialog.xaml.cs");

        codeBehind.Should().Contain($"nameof(SettingsViewModel.LongTermUnreturnedDays) => {TextBoxName}");
    }
}
