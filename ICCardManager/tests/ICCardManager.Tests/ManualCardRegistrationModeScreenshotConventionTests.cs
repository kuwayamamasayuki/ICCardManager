#nullable enable
using System.Collections.Generic;
using System.Globalization;
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
/// 定数を照合しても、撮影テストが定数ではなくリテラルを入力していれば図は定数と別の値になるので、
/// 撮影テストの本体が定数を入力していること（リテラルを入力していないこと）も対で照合する。
/// </para>
/// </remarks>
public class ManualCardRegistrationModeScreenshotConventionTests
{
    private const string SectionHeading = "#### 1.6.3 紙の出納簿から引き継ぐ（年度の途中から使い始める場合）";
    private const string CarryoverImage = "card_registration_mode_carryover.png";
    private const string InitialImage = "card_registration_mode.png";
    private const string CaptureMethodSignature = "void card_registration_mode_カード登録方法の選択ダイアログ()";
    private const string MonthIndexConstantName = "CarryoverMonthMayIndex";

    /// <summary>撮影テストの繰越月のコンボの位置（年度順。0 = 4 月）。</summary>
    private static readonly Regex MonthIndexConstant = new(
        @"private\s+const\s+int\s+" + MonthIndexConstantName + @"\s*=\s*(?<value>\d+)\s*;", RegexOptions.Compiled);

    private static readonly Regex StringConstant = new(
        @"private\s+const\s+string\s+ScreenshotCarryover(?<name>\w+)\s*=\s*""(?<value>[^""]*)""\s*;", RegexOptions.Compiled);

    /// <summary>入力欄へリテラルを直接入力する形（<c>.Text = "6000"</c>）。</summary>
    private static readonly Regex LiteralTextAssignment = new(@"\.Text\s*=\s*\$?@?""", RegexOptions.Compiled);

    /// <summary>コンボを位置のリテラルで選ぶ形（<c>.Select(2)</c>）。</summary>
    private static readonly Regex LiteralSelect = new(@"\.Select\(\s*\d+\s*\)", RegexOptions.Compiled);

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
        var section = MarkdownDocumentInspection.ExtractSection(ReadAdminManual(), SectionHeading);

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
        var section = MarkdownDocumentInspection.ExtractSection(ReadAdminManual(), SectionHeading);
        var source = ReadCaptureTestSource();

        var constants = StringConstant.Matches(source).Cast<Match>()
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);
        constants.Keys.Should().BeEquivalentTo(LabelByConstant.Keys, "撮影テストが入力する値の定数をすべて照合すること（抽出の空振りの検出を兼ねる）");

        foreach (var pair in LabelByConstant)
        {
            ExtractExampleValue(section, pair.Value).Should().Be(constants[pair.Key],
                $"本文の **{pair.Value}** の例の値は、図を撮るときに入力する値（ScreenshotCarryover{pair.Key}）と同じであること");
        }

        var month = CarryoverMonth(source);
        ExtractExampleValue(section, "繰越月").Should().Be($"{month}月", "本文の繰越月の例は、図で選ぶ月と同じであること");
        section.Should().Contain($"「{month}月から繰越」", "できたことの確認で示す繰越の行は、図で選ぶ繰越月の行であること");
        section.Should().Contain($"{month % 12 + 1} 月 1 日から", "例の説明（何月からピッすいで管理するか）は、図で選ぶ繰越月の翌月であること");
    }

    [Fact]
    public void 撮影テストは定数の値を入力し_リテラルを入力しないこと()
    {
        var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(ReadCaptureTestSource(), CaptureMethodSignature);

        FindInputViolations(body).Should().BeEmpty(
            "図の値は定数が決める前提で本文と照合しているので、撮影テストは定数を入力し、リテラルを入力しないこと");
    }

    [Theory]
    [InlineData("x.Text = ScreenshotCarryoverStartingPage; x.Text = ScreenshotCarryoverBalance; x.Text = ScreenshotCarryoverIncomeTotal; x.Text = ScreenshotCarryoverExpenseTotal; c.Select(CarryoverMonthMayIndex);", 0)]
    [InlineData("x.Text = ScreenshotCarryoverStartingPage; x.Text = \"6000\"; x.Text = ScreenshotCarryoverIncomeTotal; x.Text = ScreenshotCarryoverExpenseTotal; c.Select(CarryoverMonthMayIndex);", 2)]
    [InlineData("x.Text = ScreenshotCarryoverStartingPage; x.Text = ScreenshotCarryoverBalance; x.Text = ScreenshotCarryoverIncomeTotal; x.Text = ScreenshotCarryoverExpenseTotal; c.Select(2);", 2)]
    [InlineData("x.Text = ScreenshotCarryoverStartingPage; x.Text = ScreenshotCarryoverBalance; x.Text = ScreenshotCarryoverIncomeTotal; c.Select(CarryoverMonthMayIndex);", 1)]
    public void 抽出_定数を入力していない欄とリテラルの入力を数える(string body, int expectedViolations)
    {
        FindInputViolations(body).Should().HaveCount(expectedViolations);
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

    /// <summary>
    /// 撮影テストの本体から、定数を入力していない欄・リテラルの入力・位置のリテラルでの選択を列挙する。
    /// </summary>
    internal static IReadOnlyList<string> FindInputViolations(string body)
    {
        var violations = new List<string>();
        foreach (var name in LabelByConstant.Keys)
        {
            if (!Regex.IsMatch(body, @"\.Text\s*=\s*ScreenshotCarryover" + name + @"\s*;"))
            {
                violations.Add($"ScreenshotCarryover{name} を入力していない");
            }
        }

        violations.AddRange(LiteralTextAssignment.Matches(body).Cast<Match>().Select(m => $"リテラルを入力している: {m.Value}"));

        if (!Regex.IsMatch(body, @"\.Select\(\s*" + MonthIndexConstantName + @"\s*\)"))
        {
            violations.Add($"繰越月を {MonthIndexConstantName} で選んでいない");
        }

        violations.AddRange(LiteralSelect.Matches(body).Cast<Match>().Select(m => $"位置のリテラルで選んでいる: {m.Value}"));
        return violations;
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

    /// <summary>撮影テストが選ぶ繰越月（コンボの位置は年度順。0 = 4 月）。</summary>
    private static int CarryoverMonth(string source)
    {
        var monthIndex = MonthIndexConstant.Match(source);
        monthIndex.Success.Should().BeTrue("撮影テストの繰越月の位置の定数を抽出できること");
        return (int.Parse(monthIndex.Groups["value"].Value, CultureInfo.InvariantCulture) + 3) % 12 + 1;
    }

    private static string ReadCaptureTestSource() =>
        File.ReadAllText(Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Tests", "TouchScreenshotTests.cs"));

    private static string ReadAdminManual() =>
        File.ReadAllText(Path.Combine(TestPaths.GetSolutionRoot(), "docs", "manual", "管理者マニュアル.md"));
}
