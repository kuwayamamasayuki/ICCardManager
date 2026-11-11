#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2451: 管理者マニュアル §1.6.3「紙の出納簿から引き継ぐ（年度の途中から使い始める場合）」の図が、
/// 本文の手順で入力する欄（繰越額・累計受入・累計払出）の写った画像であり、図に入力した値と本文の例の値が一致することを検証する。
/// </summary>
/// <remarks>
/// <para>
/// §1.6.3 は §1.6.1（新規購入）と同じ画像 <c>card_registration_mode.png</c>（新規購入が選ばれた初期表示）を使っており、
/// しかもその画像は繰越額・累計の欄（#1215）が増える前に撮ったものだったため、手順 10〜12 で入力する欄が図に無かった。
/// §1.6.3 の図は「紙の出納簿からの繰越」を入力した状態の <c>card_registration_mode_carryover.png</c> を使う。
/// </para>
/// <para>
/// 図は撮影テスト（<c>ICCardManager.UITests</c> の <c>TouchScreenshotTests.card_registration_mode_…</c>）が
/// 定数の値を入力して撮る。本文の「例では「5000」」と撮影の定数が食い違うと、図と本文が別の値を示すため、
/// 撮影テストのソースから定数を読み、本文の例と照合する（UITests は単体テストから参照しないので、ソースを読む）。
/// </para>
/// </remarks>
public class ManualCardRegistrationModeScreenshotConventionTests
{
    private const string CarryoverImage = "card_registration_mode_carryover.png";
    private const string InitialImage = "card_registration_mode.png";

    /// <summary>撮影テストの繰越月のコンボの位置（年度順。0 = 4 月）。</summary>
    private static readonly Regex MonthIndexConstant = new(
        @"private\s+const\s+int\s+CarryoverMonthMayIndex\s*=\s*(?<value>\d+)\s*;", RegexOptions.Compiled);

    private static readonly Regex StringConstant = new(
        @"private\s+const\s+string\s+ScreenshotCarryover(?<name>\w+)\s*=\s*""(?<value>[^""]*)""\s*;", RegexOptions.Compiled);

    /// <summary>撮影テストの定数名 → 本文で値を案内する手順の項目名（太字）。</summary>
    private static readonly IReadOnlyDictionary<string, string> LabelByConstant = new Dictionary<string, string>
    {
        ["StartingPage"] = "開始ページ",
        ["Balance"] = "繰越額",
        ["IncomeTotal"] = "累計受入",
        ["ExpenseTotal"] = "累計払出",
    };

    [Fact]
    public void 年度の途中から使い始める節の図は繰越の入力欄が写った画像であること()
    {
        var section = ExtractSection(ReadAdminManual(), "1.6.3");

        section.Should().Contain($"../screenshots/{CarryoverImage}",
            "§1.6.3 の手順では繰越額・累計受入・累計払出を入力するので、それらの欄が写った画像を載せること");
        section.Should().NotContain($"../screenshots/{InitialImage}",
            "新規購入が選ばれた初期表示の画像では、手順で入力する繰越の欄の値が写らない");
        File.Exists(Path.Combine(TestPaths.GetSolutionRoot(), "docs", "screenshots", CarryoverImage))
            .Should().BeTrue("参照する画像が公開先に実在すること");
    }

    [Fact]
    public void 図に入力した値と本文の例の値が一致すること()
    {
        var section = ExtractSection(ReadAdminManual(), "1.6.3");
        var source = File.ReadAllText(Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Tests", "TouchScreenshotTests.cs"));

        var constants = StringConstant.Matches(source).Cast<Match>()
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);
        constants.Keys.Should().BeEquivalentTo(LabelByConstant.Keys, "撮影テストが入力する値の定数をすべて照合すること（抽出の空振りの検出を兼ねる）");

        foreach (var pair in LabelByConstant)
        {
            ExtractExampleValue(section, pair.Value).Should().Be(constants[pair.Key],
                $"本文の **{pair.Value}** の例の値は、図を撮るときに入力する値（ScreenshotCarryover{pair.Key}）と同じであること");
        }

        var monthIndex = MonthIndexConstant.Match(source);
        monthIndex.Success.Should().BeTrue("撮影テストの繰越月の位置の定数を抽出できること");
        var month = (int.Parse(monthIndex.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) + 3) % 12 + 1;
        ExtractExampleValue(section, "繰越月").Should().Be($"{month}月", "本文の繰越月の例は、図で選ぶ月と同じであること");
    }

    [Theory]
    [InlineData("10. **繰越額** に、残高を入力します。例では「5000」（5,000 円）です。\n", "繰越額", "5000")]
    [InlineData("7. **繰越月** を選びます。例では「5月」（理由）です。\n", "繰越月", "5月")]
    [InlineData("11. **累計受入** に入力します。\n", "累計受入", null)]
    [InlineData("10. **繰越額** に入力します。\n11. **累計受入** に入力します。例では「12000」です。\n", "繰越額", null)]
    public void 抽出_手順の項目名の行から例の値を取り出し_別の行の例は拾わない(string text, string label, string? expected)
    {
        ExtractExampleValue(text, label).Should().Be(expected);
    }

    [Fact]
    public void 抽出_節は次の同じ深さ以上の見出しの手前までを取り出す()
    {
        const string text = "#### 1.6.2 前\nA\n#### 1.6.3 対象\nB\n##### 補足\nC\n#### 1.6.4 次\nD\n";

        ExtractSection(text, "1.6.3").Should().Be("#### 1.6.3 対象\nB\n##### 補足\nC\n");
    }

    /// <summary>
    /// 本文から、太字の項目名（<c>**繰越額**</c>）で始まる手順の行の「例では「…」」の値を取り出す。無ければ null。
    /// </summary>
    internal static string? ExtractExampleValue(string section, string label)
    {
        var pattern = new Regex(
            @"^\s*\d+\.\s*\*\*" + Regex.Escape(label) + @"\*\*[^\r\n]*?例では「(?<value>[^」]+)」", RegexOptions.Multiline);
        var match = pattern.Match(section);
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>「#### 1.6.3 …」の見出しから、次の同じ深さ以上の見出しの手前までを返す。</summary>
    internal static string ExtractSection(string text, string number)
    {
        var normalized = text.Replace("\r\n", "\n");
        var heading = new Regex(@"^(?<hashes>#+)\s+" + Regex.Escape(number) + @"\s", RegexOptions.Multiline).Match(normalized);
        heading.Success.Should().BeTrue($"§{number} の見出しがあること");

        var depth = heading.Groups["hashes"].Value.Length;
        var next = new Regex(@"^#{1," + depth + @"}\s", RegexOptions.Multiline).Match(normalized, heading.Index + heading.Length);
        return next.Success
            ? normalized.Substring(heading.Index, next.Index - heading.Index)
            : normalized.Substring(heading.Index);
    }

    private static string ReadAdminManual() =>
        File.ReadAllText(Path.Combine(TestPaths.GetSolutionRoot(), "docs", "manual", "管理者マニュアル.md"));
}
