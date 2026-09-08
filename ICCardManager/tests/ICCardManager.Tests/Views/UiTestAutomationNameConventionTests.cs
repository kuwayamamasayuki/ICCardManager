using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2018: UI テストの要素名定数（<c>ICCardManager.UITests.Infrastructure.TestConstants</c>）が、
/// 実際の XAML の <c>AutomationProperties.Name</c> / <c>AutomationProperties.HelpText</c> と
/// 一致していることを静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// UI テストは CI（<c>Category!=UI</c>）で実行されないため、定数と XAML のずれは
/// 誰かが Windows で UI テストを流すまで検出されない。実際
/// <c>StaffManageDeleteButtonName</c> は追加当初（#1500）から <c>Content</c> の文字列 "削除" のままで、
/// <c>AutomationProperties.Name="職員削除"</c>（#1404 で付与）とは一度も一致していなかった。
/// <c>ByName("削除")</c> はボタン内側の Text 要素に一致し、Text は Invoke パターンを持たないため
/// <c>PatternNotSupportedException</c> になる。同じく <c>StaffAuthDialogName</c> は
/// Window の <c>Title</c> を持っており、UIA Name（<c>AutomationProperties.Name</c> が優先される）と
/// 食い違っていた。
/// </para>
/// <para>
/// 本テストは CI で走る通常の単体テストプロジェクトに置き、UITests のソースを
/// <b>テキストとして</b>走査する（UITests は net48 / FlaUI 依存で、参照したくないため）。
/// 検査対象は <c>[UiaName]</c> / <c>[UiaNamePrefix]</c> / <c>[UiaHelpText]</c> の
/// マーカーが付いた定数に限る。マーカーの無い定数（TextBlock の本文で検索する値、
/// タイムアウト秒数など）は対象外。
/// </para>
/// <para>
/// 空振り検出は「実データが非空であること」ではなく、抽出ロジックを既知のサンプル入力で
/// 固定することで担保する（<c>development-conventions.md</c> #1786）。
/// 実データ側の表明だけだと、マーカーを全部外した実装でも緑になる一方、
/// 「非空であること」を実データへ課すと定数の正当な削除で赤くなる。
/// </para>
/// </remarks>
public class UiTestAutomationNameConventionTests
{
    private const string TestConstantsRelativePath =
        @"tests\ICCardManager.UITests\Infrastructure\TestConstants.cs";

    /// <summary>
    /// マーカーの付いた定数と、その値・マーカー種別。
    /// </summary>
    internal readonly struct MarkedConstant
    {
        public MarkedConstant(string marker, string name, string value)
        {
            Marker = marker;
            Name = name;
            Value = value;
        }

        public string Marker { get; }
        public string Name { get; }
        public string Value { get; }
    }

    // ── 実データに対する表明 ──────────────────────────────

    [Fact]
    public void UIA名マーカーの付いた定数は実際のXAMLのAutomationPropertiesNameと一致すること()
    {
        var constants = ExtractMarkedConstants(ReadTestConstantsSource());
        var xamlNames = CollectXamlAttributeValues("Name");

        var mismatches = constants
            .Where(c => c.Marker == "UiaName")
            .Where(c => !xamlNames.Contains(c.Value))
            .Select(c => $"{c.Name} = \"{c.Value}\"")
            .ToList();

        mismatches.Should().BeEmpty(
            "Issue #2018: [UiaName] を付けた定数の値が、どの XAML の AutomationProperties.Name にも存在しない。" +
            "UI テストは ByName でこの値を探すため、一致しないと別種の要素（Content の Text など）を掴むか、" +
            "そもそも見つからない。XAML 側の値に合わせて定数を直すこと。" +
            $"不一致: {string.Join(" / ", mismatches)}");
    }

    [Fact]
    public void UIA名プレフィックスマーカーの付いた定数は実際のXAMLのAutomationPropertiesNameの前方一致であること()
    {
        var constants = ExtractMarkedConstants(ReadTestConstantsSource());
        var xamlNames = CollectXamlAttributeValues("Name");

        var mismatches = constants
            .Where(c => c.Marker == "UiaNamePrefix")
            .Where(c => !xamlNames.Any(n => n.StartsWith(c.Value, StringComparison.Ordinal)))
            .Select(c => $"{c.Name} = \"{c.Value}\"")
            .ToList();

        mismatches.Should().BeEmpty(
            "Issue #2018: [UiaNamePrefix] を付けた定数の値で始まる AutomationProperties.Name が XAML に無い。" +
            $"不一致: {string.Join(" / ", mismatches)}");
    }

    [Fact]
    public void HelpTextマーカーの付いた定数は実際のXAMLのAutomationPropertiesHelpTextと一致すること()
    {
        var constants = ExtractMarkedConstants(ReadTestConstantsSource());
        var xamlHelpTexts = CollectXamlAttributeValues("HelpText");

        var mismatches = constants
            .Where(c => c.Marker == "UiaHelpText")
            .Where(c => !xamlHelpTexts.Contains(c.Value))
            .Select(c => $"{c.Name} = \"{c.Value}\"")
            .ToList();

        mismatches.Should().BeEmpty(
            "Issue #2018: [UiaHelpText] を付けた定数の値が、どの XAML の AutomationProperties.HelpText にも存在しない。" +
            $"不一致: {string.Join(" / ", mismatches)}");
    }

    /// <summary>
    /// 「消し忘れ」の対の表明: マーカーの無い定数を静かに増やさせないための、既知の 2 定数の固定。
    /// </summary>
    /// <remarks>
    /// 実データ全体へ「非空であること」を課すと定数の正当な削除で赤くなるため、
    /// Issue #2018 で実際に壊れていた 2 定数だけを名指しで固定する。
    /// この 2 つは経路上どうしても UIA Name で探す必要があり、マーカーを外す理由が無い。
    /// </remarks>
    [Theory]
    [InlineData("StaffManageDeleteButtonName", "職員削除")]
    [InlineData("StaffAuthDialogName", "職員証認証ダイアログ")]
    public void Issue2018で是正した定数はUIA名マーカー付きで宣言されていること(string constantName, string expectedValue)
    {
        var constants = ExtractMarkedConstants(ReadTestConstantsSource());

        var target = constants.FirstOrDefault(c => c.Name == constantName);

        target.Name.Should().Be(constantName,
            $"Issue #2018: {constantName} は [UiaName] を付けて静的検査の対象に保つこと。");
        target.Marker.Should().Be("UiaName");
        target.Value.Should().Be(expectedValue);
    }

    // ── 抽出ロジックそのものの固定（サンプル入力） ──────────────

    [Fact]
    public void 抽出はマーカーの付いた定数だけを拾うこと()
    {
        const string sample = @"
internal static class Sample
{
    [UiaName]
    public const string Marked = ""あ"";

    public const string Unmarked = ""い"";

    [UiaNamePrefix]
    public const string Prefixed = ""う"";

    [UiaHelpText]
    public const string Helped = ""え"";
}";

        var extracted = ExtractMarkedConstants(sample);

        extracted.Select(c => (c.Marker, c.Name, c.Value)).Should().BeEquivalentTo(new[]
        {
            ("UiaName", "Marked", "あ"),
            ("UiaNamePrefix", "Prefixed", "う"),
            ("UiaHelpText", "Helped", "え"),
        });
    }

    [Fact]
    public void 抽出はコメント内のマーカーを拾わないこと()
    {
        // 極性の反転（#1692）: マーカーの使い方を説明するコメント自体が検出されると、
        // 「存在しない定数が XAML に無い」という誤検出で規約が信用されなくなる。
        const string sample = @"
internal static class Sample
{
    /// <summary>[UiaName] を付けると検査対象になる。</summary>
    // [UiaName] public const string CommentedOut = ""けす"";
    /* [UiaHelpText]
       public const string InBlockComment = ""ぶろっく""; */
    public const string Unmarked = ""い"";
}";

        ExtractMarkedConstants(sample).Should().BeEmpty();
    }

    [Fact]
    public void 抽出は文字列リテラル内のスラッシュでコメント判定しないこと()
    {
        // "データエクスポート/インポートダイアログを開く" のように値にスラッシュを含む定数がある。
        const string sample = @"
internal static class Sample
{
    [UiaName]
    public const string Slashed = ""データエクスポート/インポートダイアログを開く"";
}";

        ExtractMarkedConstants(sample).Select(c => c.Value).Should()
            .ContainSingle().Which.Should().Be("データエクスポート/インポートダイアログを開く");
    }

    [Fact]
    public void XAML抽出はバインディング式を値として扱わないこと()
    {
        const string sample =
            "<Button AutomationProperties.Name=\"職員削除\" />\n" +
            "<TextBlock AutomationProperties.Name=\"{Binding StatusMessage}\" />\n" +
            "<Button AutomationProperties.HelpText=\"AとB\" />";

        ExtractAttributeValues(sample, "Name").Should().BeEquivalentTo(new[] { "職員削除" });
        ExtractAttributeValues(sample, "HelpText").Should().BeEquivalentTo(new[] { "AとB" });
    }

    [Fact]
    public void XAML抽出はXMLエンティティを復号すること()
    {
        const string sample = "<Button AutomationProperties.Name=\"A&amp;B\" />";

        ExtractAttributeValues(sample, "Name").Should().BeEquivalentTo(new[] { "A&B" });
    }

    // ── ヘルパー ─────────────────────────────────────────

    private static string ReadTestConstantsSource()
    {
        var path = Path.Combine(
            TestPaths.GetSolutionRoot(),
            TestConstantsRelativePath.Replace('\\', Path.DirectorySeparatorChar));

        File.Exists(path).Should().BeTrue(
            $"UI テストの定数ファイルが見つからない: {path}。移動した場合は本テストのパスも更新すること。");

        return File.ReadAllText(path);
    }

    private static HashSet<string> CollectXamlAttributeValues(string attributeName)
    {
        var root = TestPaths.GetProductionSourceRoot();
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = file.Substring(root.Length);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach (var value in ExtractAttributeValues(File.ReadAllText(file), attributeName))
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>
    /// XAML テキストから <c>AutomationProperties.&lt;attributeName&gt;="..."</c> の値を抜き出す。
    /// マークアップ拡張（<c>{Binding ...}</c> 等）は実行時に決まるため対象外。
    /// </summary>
    internal static IReadOnlyList<string> ExtractAttributeValues(string xaml, string attributeName)
    {
        var pattern = $@"AutomationProperties\.{Regex.Escape(attributeName)}\s*=\s*""([^""]*)""";

        return Regex.Matches(xaml, pattern)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(v => !v.StartsWith("{", StringComparison.Ordinal))
            .Select(System.Net.WebUtility.HtmlDecode)
            .ToList();
    }

    /// <summary>
    /// C# ソースから、<c>[UiaName]</c> 等のマーカーが直前に付いた
    /// <c>public const string</c> 宣言を抜き出す。
    /// </summary>
    internal static IReadOnlyList<MarkedConstant> ExtractMarkedConstants(string source)
    {
        var code = StripComments(source);

        var pattern =
            @"\[\s*(?<marker>UiaName|UiaNamePrefix|UiaHelpText)\s*\]\s*" +
            @"public\s+const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]*)""\s*;";

        return Regex.Matches(code, pattern)
            .Cast<Match>()
            .Select(m => new MarkedConstant(
                m.Groups["marker"].Value,
                m.Groups["name"].Value,
                m.Groups["value"].Value))
            .ToList();
    }

    /// <summary>
    /// 文字列リテラルの中身は保ったまま、行コメントとブロックコメントだけを取り除く。
    /// </summary>
    /// <remarks>
    /// <c>TestSourceInspection.ToCodeOnly</c> は文字列リテラルの中身も消すため、
    /// 定数の値そのものを検査する本テストには使えない（#1960 と同じ理由）。
    /// </remarks>
    internal static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '"')
            {
                // 文字列リテラル（逐語的文字列 @"" は "" が 1 個の " を表すが、
                // どちらの解釈でもコメント判定には影響しないため区別しない）
                sb.Append(c);
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        sb.Append(source[i]).Append(source[i + 1]);
                        i += 2;
                        continue;
                    }

                    sb.Append(source[i]);
                    if (source[i] == '"')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
