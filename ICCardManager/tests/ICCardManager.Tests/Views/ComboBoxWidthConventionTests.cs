using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2142: 幅を固定した <c>ComboBox</c> で、文字サイズ「大」以上だと選択中の文言が切れるのを固定する。
/// </summary>
/// <remarks>
/// <para>
/// ui-conventions #1687「幅ではなく折り返しで担保する」は <c>TextBlock</c> の話で、<c>ComboBox</c> には及んでいなかった。
/// <c>ComboBox</c> は選択中の項目を折り返さず、幅に入らない文字は<b>黙って切れる</b>。メイン画面の並び順
/// （「残高（少ない順）」）、設定画面の部署種別（判断の決め手になる「旅費」が見えない）・トースト位置が該当した。
/// </para>
/// <para>
/// 是正は <c>Width</c> を同じ値の <c>MinWidth</c> にし、<c>HorizontalAlignment="Left"</c> で内容に合わせて伸ばす形。
/// 下限を残すので、通常の文字サイズでの見た目は変わらない。Issue が名指ししたのは 3 箇所だったが、
/// 全画面を走査すると同じ形が 16 箇所あった（年・月の選択、カード名の絞り込み等）。
/// </para>
/// </remarks>
public class ComboBoxWidthConventionTests
{
    [Fact]
    public void ComboBoxに数値の固定幅を与えないこと()
    {
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindFixedWidthComboBoxes(f.Text).Select(line => $"{f.Name}:{line}"))
            .ToList();

        violations.Should().BeEmpty(
            "ComboBox の幅は MinWidth（下限）で与え、HorizontalAlignment=\"Left\" で内容に合わせて伸ばすこと。" +
            "固定幅だと文字サイズ「大」以上で選択中の文言が切れる（Issue #2142）");
    }

    [Theory]
    [InlineData("MainWindow.xaml", "ダッシュボードの並び順")]
    [InlineData("SettingsDialog.xaml", "DepartmentTypeComboBox")]
    [InlineData("SettingsDialog.xaml", "ToastPositionComboBox")]
    public void Issueが名指ししたComboBoxが内容に合わせて伸びること(string file, string name)
    {
        // 対の表明: 固定幅の不在だけだと、ComboBox の幅指定を丸ごと消して列幅いっぱいに引き伸ばす実装
        // （グリッドの列によっては逆に狭くなる）でも緑になる
        var combo = FillForegroundPairs.EnumerateProductionXaml()
            .Where(f => f.Name == file)
            .SelectMany(f => XamlElementInspection.EnumerateElementsIncludingNested(f.Text, "ComboBox"))
            .SingleOrDefault(c => XamlElementInspection.GetAttribute(c.StartTag, "x:Name") == name
                                  || XamlElementInspection.GetAttribute(c.StartTag, "AutomationProperties.Name") == name);

        combo.Should().NotBeNull("{0} の「{1}」が実在すること（検査対象の空振り防止）", file, name);
        XamlElementInspection.GetAttribute(combo!.StartTag, "MinWidth").Should().NotBeNull("下限の幅は残すこと");
        XamlElementInspection.GetAttribute(combo.StartTag, "HorizontalAlignment").Should().Be("Left");
    }

    [Theory]
    [InlineData("<ComboBox Width=\"130\"/>", 1)]
    [InlineData("<ComboBox x:Name=\"A\"\n          Width='200'\n          SelectedIndex=\"0\"/>", 1)]
    [InlineData("<ComboBox MinWidth=\"130\" HorizontalAlignment=\"Left\"/>", 0)]
    // MaxWidth は上限なので中身を切らない
    [InlineData("<ComboBox MinWidth=\"80\" MaxWidth=\"300\"/>", 0)]
    // Style の Setter で固定幅を与える形も同じ欠陥
    [InlineData("<Style TargetType=\"ComboBox\"><Setter Property=\"Width\" Value=\"120\"/></Style>", 1)]
    // ComboBox 以外の固定幅は対象外（TextBox の固定幅は入力中にスクロールするので中身を失わない）
    [InlineData("<TextBox Width=\"120\"/>", 0)]
    // バインドした幅は数値の固定幅ではない
    [InlineData("<ComboBox Width=\"{Binding ColumnWidth}\"/>", 0)]
    public void 固定幅の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        FindFixedWidthComboBoxes(xaml).Should().HaveCount(expected);
    }

    private static IEnumerable<int> FindFixedWidthComboBoxes(string xaml)
    {
        foreach (var combo in XamlElementInspection.EnumerateElementsIncludingNested(xaml, "ComboBox"))
        {
            if (IsNumeric(XamlElementInspection.GetAttribute(combo.StartTag, "Width")))
            {
                yield return combo.Line;
            }
        }

        foreach (var style in XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Style")
                     .Where(s => XamlElementInspection.GetAttribute(s.StartTag, "TargetType") == "ComboBox"))
        {
            if (IsNumeric(XamlElementInspection.GetSetterValue(style.Body, "Width")))
            {
                yield return style.Line;
            }
        }
    }

    private static bool IsNumeric(string? value)
        => value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
