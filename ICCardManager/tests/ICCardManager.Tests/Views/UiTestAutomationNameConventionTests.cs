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
/// 空振り検出は「実データが非空であること」ではなく、①抽出ロジックを既知のサンプル入力で
/// 固定し、②<b>すべての定数へ分類の宣言を強制する</b>ことで担保する
/// （<c>development-conventions.md</c> #1786）。②が無いと、マーカーの付け忘れが
/// 「意図的に対象外」と区別できず検査が静かに素通りする（コードレビューで実測: 全マーカーを
/// 外しても名指しで固定した 2 件以外は緑だった）。一方「非空であること」を実データへ課すと、
/// 定数の正当な削除で赤くなり、修正者を「対象から外す」方向へ誘導してしまう。
/// </para>
/// <para>
/// <b>この検査の限界</b>: XAML の属性値はプロジェクト全体で 1 つの集合にまとめて突き合わせる。
/// したがって「その値がどこかの画面に存在する」ことは分かるが、「テストが開いている画面に
/// 存在する」ことまでは保証しない（"キャンセル" のように複数のダイアログが持つ名前がある）。
/// 画面単位の対応付けは維持コストに見合わないと判断した。
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
    /// <c>TestConstants</c> の <c>public const string</c> は、例外なく
    /// 「どの <c>AutomationProperties</c> と対応するか」または「対応しないこと（<c>[NotUiaName]</c>）」を
    /// 宣言していること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// マーカーの省略を許すと、新しい定数を足した人が付け忘れたときに上の 3 件が
    /// <b>静かに素通りする</b>（fail-open）。実測でも、全定数からマーカーを外すと
    /// 名指しで固定した 2 件以外は緑のままだった。省略を赤にすることで、
    /// 「検査の対象に入っているか」を定数ごとに人が判断せざるを得なくする。
    /// </para>
    /// <para>
    /// この形は「実データが非空であること」を課すのとは違い、定数を<b>削除</b>しても赤くならない
    /// （#1786 が戒める「規約が推奨する方向の変更で赤になる」形にならない）。
    /// 赤くなるのは分類を宣言しない定数を<b>追加</b>したときだけ。
    /// </para>
    /// </remarks>
    [Fact]
    public void TestConstantsの文字列定数はすべてUIA属性との対応を宣言していること()
    {
        var undeclared = ExtractStringConstants(ReadTestConstantsSource())
            .Where(c => c.Marker.Length == 0)
            .Select(c => c.Name)
            .ToList();

        undeclared.Should().BeEmpty(
            "Issue #2018: TestConstants の public const string は、[UiaName] / [UiaNamePrefix] / " +
            "[UiaHelpText] のいずれか、または [NotUiaName] を付けて分類を宣言すること。" +
            "省略を許すと付け忘れと「意図的に対象外」が区別できず、静的検査が素通りする。" +
            $"未宣言: {string.Join(" / ", undeclared)}");
    }

    /// <summary>
    /// 「消し忘れ」の対の表明: 既知の 2 定数の値とマーカーの固定。
    /// </summary>
    /// <remarks>
    /// Issue #2018 で実際に壊れていた 2 定数を名指しで固定する。
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
    public void 抽出はマーカーの無い定数とNotUiaNameを区別して拾うこと()
    {
        // 対の表明: 上のテストが「拾わない」ことを見ているのに対し、
        // こちらは同じ入力で「未宣言」と「意図的に対象外」が別物として現れることを見る。
        // 両者を混同すると、付け忘れの検出（fail-open の是正）が働かない。
        const string sample = @"
internal static class Sample
{
    [UiaName]
    public const string Marked = ""あ"";

    public const string Unmarked = ""い"";

    [NotUiaName]
    public const string Excluded = ""う"";
}";

        ExtractStringConstants(sample).Select(c => (c.Marker, c.Name)).Should().BeEquivalentTo(new[]
        {
            ("UiaName", "Marked"),
            (string.Empty, "Unmarked"),
            ("NotUiaName", "Excluded"),
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
    /// C# ソースから <c>public const string</c> 宣言をすべて抜き出す。
    /// 直前にマーカー属性が付いていればその名前を、無ければ空文字を <c>Marker</c> に入れる。
    /// </summary>
    internal static IReadOnlyList<MarkedConstant> ExtractStringConstants(string source)
    {
        var code = StripComments(source);

        var pattern =
            @"(?:\[\s*(?<marker>UiaName|UiaNamePrefix|UiaHelpText|NotUiaName)\s*\]\s*)?" +
            @"public\s+const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]*)""\s*;";

        return Regex.Matches(code, pattern)
            .Cast<Match>()
            .Select(m => new MarkedConstant(
                m.Groups["marker"].Success ? m.Groups["marker"].Value : string.Empty,
                m.Groups["name"].Value,
                m.Groups["value"].Value))
            .ToList();
    }

    /// <summary>
    /// <c>[UiaName]</c> / <c>[UiaNamePrefix]</c> / <c>[UiaHelpText]</c> が付いた定数だけを返す。
    /// </summary>
    internal static IReadOnlyList<MarkedConstant> ExtractMarkedConstants(string source)
        => ExtractStringConstants(source)
            .Where(c => c.Marker.Length > 0 && c.Marker != "NotUiaName")
            .ToList();

    /// <summary>
    /// 文字列リテラルの中身は保ったまま、行コメントとブロックコメントだけを取り除く。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TestSourceInspection.ToCodeOnly</c> は文字列リテラルの中身も消すため、
    /// 定数の値そのものを検査する本テストには使えない（#1960 と同じ理由）。
    /// </para>
    /// <para>
    /// <b>前提</b>: 逐語的文字列（<c>@"..."</c>）と通常の文字列を区別しない。
    /// <c>""</c> のエスケープはどちらの解釈でも「文字を落とさない」ので影響しないが、
    /// <b>末尾がバックスラッシュで終わる逐語的文字列</b>（<c>@"C:\"</c>）と
    /// <c>char</c> リテラルの <c>'"'</c> では走査が 1 つ分ずれる。
    /// 検査対象は <c>TestConstants.cs</c>（UI の要素名を並べた定数だけのファイル）に限られ、
    /// どちらも現れないためこの前提で足りる。対象を広げるときは
    /// <c>TestSourceInspection</c> 側へヘルパーを移して正しく扱うこと。
    /// </para>
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
