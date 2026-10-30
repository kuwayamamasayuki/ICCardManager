using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2308: アクセスキー（<c>_X</c>）がダイアログ内で重複しないこと、
/// および 03_画面設計書 §5.3 の一覧が本番の XAML と一致することを、本番の全 XAML の静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// WPF は同じアクセスキーを持つ要素が複数あると、Alt+キーのたびにフォーカスを順に移すだけで
/// <b>ボタンを実行しない</b>（<c>AccessKeyEventArgs.IsMultiple</c>）。行編集の「同行者数(_G)」と
/// 「逆算した金額を適用(_G)」、設定の「…秒数(_D)」と「データベース保存先フォルダ(_D)」、
/// 「参照...(_R)」と「デフォルトに戻す(_R)」が重複しており、03 §5.3 が案内する Alt+キー 1 回の操作が成り立たなかった。
/// </para>
/// <para>
/// <b>表示条件で除外しない</b>。行編集の「逆算した金額を適用」は導入行の残高の訂正提案が出ているときだけ
/// 表示されるため、通常の編集では衝突が見えない — 表示条件が重なった瞬間にだけ壊れる形こそ検査で止める。
/// 走査対象は本番の全 XAML から導出する（ファイル名で列挙しない。#1786）。
/// </para>
/// <para>
/// アクセスキーを持ち得る記述は 4 形ある: ①開始タグの <c>Content</c> / <c>Header</c> 属性
/// ②<c>&lt;AccessText Text="…"/&gt;</c> ③<c>&lt;Setter Property="Content" Value="…"/&gt;</c>
/// ④<c>Label</c> / <c>Button</c> 等の本体の直接テキスト。<c>TextBlock</c> の文字列はアクセスキーを解釈しないので対象外。
/// <b>既知の走査範囲外</b>: ①バインディングやコードビハインドで組み立てる文字列（本番には現在無い）
/// ②ファイルをまたぐ衝突 — 一意性はファイル（＝ダイアログ）単位で見るので、<c>UserControl</c> を別ファイルに置いて
/// ダイアログへ埋め込むと、埋め込み先とのキーの衝突は検出できない（本番には現在 <c>UserControl</c> が無い。足すときは検査を広げる）
/// ③同じ表記のラベル（設定の「参照...」が 2 つ）どうしの、§5.3 の項目との対応の入れ替わり（表記で区別できない）。
/// </para>
/// </remarks>
public class AccessKeyConventionTests
{
    /// <summary>本体の直接テキストがアクセスキーとして解釈される要素（ContentControl 系・AccessText）。</summary>
    private static readonly HashSet<string> AccessKeyTextHosts = new(StringComparer.Ordinal)
    {
        "Label", "Button", "CheckBox", "RadioButton", "ToggleButton", "RepeatButton",
        "AccessText", "GroupBox", "TabItem", "Expander", "MenuItem",
    };

    internal sealed record AccessKeyUse(char Key, int Line, string Text);

    #region 本番 XAML

    [Fact]
    public void 本番のXAMLでアクセスキーがファイルごとに一意であること()
    {
        var violations = ProductionAccessKeys()
            .SelectMany(kv => FindDuplicates(kv.Value).Select(d => $"{kv.Key}: {d}"))
            .ToList();

        violations.Should().BeEmpty(
            "同じアクセスキーを持つ要素が複数あると、Alt+キーはフォーカスを移すだけでボタンを実行しない" +
            "（03_画面設計書 §5.4。Issue #2308）。違反: {0}", string.Join(" / ", violations));
    }

    [Fact]
    public void 設計書のアクセスキー一覧が本番のXAMLと一致すること()
    {
        var documented = ReadDocumentedAccessKeys();
        var uses = ProductionAccessKeys()
            .ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key), kv => kv.Value, StringComparer.Ordinal);
        var actual = uses.ToDictionary(
            kv => kv.Key, kv => kv.Value.Select(u => u.Key).OrderBy(c => c).ToList(), StringComparer.Ordinal);

        // 空振り防止: 欠陥のあった 2 画面が両方の側に現れること
        actual.Keys.Should().Contain(new[] { "LedgerRowEditDialog", "SettingsDialog" });
        documented.Keys.Should().Contain(new[] { "LedgerRowEditDialog", "SettingsDialog" });

        actual.Keys.Except(documented.Keys).Should().BeEmpty(
            "アクセスキーを持つダイアログはすべて 03_画面設計書 §5.3 に表を置くこと");
        documented.Keys.Except(actual.Keys).Should().BeEmpty(
            "03_画面設計書 §5.3 の表は、アクセスキーを持つ実在のダイアログだけを指すこと");

        foreach (var dialog in actual.Keys)
        {
            documented[dialog].Select(r => r.Key).OrderBy(c => c).Should().Equal(actual[dialog],
                "03_画面設計書 §5.3 の {0} の表は XAML のアクセスキーの全量と一致すること", dialog);

            // キーの集合だけでなく、キーと項目の対応も見る（Alt+R と Alt+E の入れ替わり等）
            var mismatches = uses[dialog]
                .Where(u => !documented[dialog].Any(r => r.Key == u.Key && CorrespondsTo(r.Item, LabelOf(u.Text))))
                .Select(u => $"Alt+{u.Key}「{LabelOf(u.Text)}」")
                .ToList();
            mismatches.Should().BeEmpty(
                "03_画面設計書 §5.3 の {0} の表で、各キーの項目が XAML の表記と対応すること。対応しないもの: {1}",
                dialog, string.Join("、", mismatches));
        }
    }

    [Theory]
    [InlineData("参照...(_R)", "参照...")]
    [InlineData("同行者数(_G):", "同行者数")]
    [InlineData("a__b(_C)", "a_b")]
    [InlineData("_Delete", "Delete")]
    public void ラベルの表記からアクセスキーの記法を除くこと(string text, string expected)
    {
        LabelOf(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("バックアップ参照...", "参照...", true)]
    [InlineData("データベース保存先フォルダパス", "データベース保存先フォルダ", true)]
    [InlineData("逆算した金額を適用（導入行の残高の訂正提案が出ているときだけ表示）", "逆算した金額を適用", true)]
    [InlineData("同行者数", "逆算した金額を適用", false)]
    [InlineData("データベース保存先フォルダパス", "同行者数入力を自動的に閉じるまでの秒数", false)]
    [InlineData("同行者数", "", false)]
    public void 設計書の項目とXAMLの表記の対応を判定すること(string item, string label, bool expected)
    {
        CorrespondsTo(item, label).Should().Be(expected);
    }

    [Fact]
    public void 行編集の逆算した金額の適用は同行者数と別のアクセスキーであること()
    {
        // Issue #2308 の欠陥そのもの（Alt+G の衝突）。一意性の検査を補う具体値の表明
        var keys = ProductionAccessKeys()["LedgerRowEditDialog.xaml"];
        keys.Should().Contain(u => u.Text.StartsWith("逆算した金額を適用", StringComparison.Ordinal) && u.Key == 'R');
        keys.Should().Contain(u => u.Text.StartsWith("同行者数", StringComparison.Ordinal) && u.Key == 'G');
    }

    #endregion

    #region 検査ロジックの固定（既知の入力）

    [Theory]
    [InlineData("保存(_S)", 'S')]
    [InlineData("同行者数(_g):", 'G')]
    [InlineData("a__b(_C)", 'C')]
    [InlineData("_Delete", 'D')]
    [InlineData("database__config", null)]
    [InlineData("末尾_", null)]
    [InlineData("記号なし", null)]
    public void アクセスキーの解釈がWPFの規則に従うこと(string text, char? expected)
    {
        ParseAccessKey(text).Should().Be(expected);
    }

    [Fact]
    public void 抽出がアクセスキーを持ち得る4形を拾い対象外の記述を拾わないこと()
    {
        const string xaml = @"<Window>
  <!-- <Button Content=""コメント(_Q)""/> -->
  <Label Content=""日付(_D):""/>
  <Button Header=""見出し(_H)""/>
  <AccessText Text=""アクセス(_A)""/>
  <Button><Button.Style><Style><Setter Property=""Content"" Value=""設定(_S)""/></Style></Button.Style></Button>
  <GroupBox><GroupBox.Style><Style><Setter Property=""Header"" Value=""見出し設定(_E)""/></Style></GroupBox.Style></GroupBox>
  <CheckBox>本体(_B)</CheckBox>
  <Button><Button.Content>内容(_X)</Button.Content></Button>
  <TextBlock Text=""テキスト(_T)""/>
  <TextBlock>本文(_Z)</TextBlock>
  <Button Content=""{Binding Caption_Y}""/>
  <Button ToolTip=""ヒント(_P)""/>
</Window>";

        ExtractAccessKeys(XamlElementInspection.StripXmlComments(xaml)).Select(u => u.Key)
            .Should().BeEquivalentTo(new[] { 'D', 'H', 'A', 'S', 'E', 'B', 'X' });
    }

    [Fact]
    public void 条件付きで表示される要素どうしの重複も検出すること()
    {
        const string xaml = @"<Window>
  <Label Content=""同行者数(_G):""/>
  <Border Visibility=""{Binding HasSuggestion, Converter={StaticResource BoolToVisibilityConverter}}"">
    <Button Content=""逆算した金額を適用(_g)""/>
  </Border>
  <Button Content=""キャンセル(_C)""/>
</Window>";

        FindDuplicates(ExtractAccessKeys(xaml)).Should().ContainSingle()
            .Which.Should().Contain("Alt+G");
    }

    [Fact]
    public void 重複が無ければ何も報告しないこと()
    {
        const string xaml = @"<Window>
  <Label Content=""同行者数(_G):""/>
  <Button Content=""逆算した金額を適用(_R)""/>
</Window>";

        FindDuplicates(ExtractAccessKeys(xaml)).Should().BeEmpty();
    }

    #endregion

    #region ヘルパー

    /// <summary>
    /// WPF の <c>AccessText</c> と同じ規則で、最初のアクセスキーを返す（<c>__</c> はリテラルの <c>_</c>）。
    /// </summary>
    internal static char? ParseAccessKey(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '_')
            {
                continue;
            }

            if (text[i + 1] == '_')
            {
                i++;
                continue;
            }

            return char.ToUpperInvariant(text[i + 1]);
        }

        return null;
    }

    internal static IReadOnlyList<AccessKeyUse> ExtractAccessKeys(string xaml)
    {
        var result = new List<AccessKeyUse>();

        void Add(string? value, int line)
        {
            if (value == null || XamlElementInspection.IsMarkupExtension(value))
            {
                return;
            }

            var text = value.StartsWith("{}", StringComparison.Ordinal) ? value.Substring(2) : value;
            var key = ParseAccessKey(text);
            if (key != null)
            {
                result.Add(new AccessKeyUse(key.Value, line, text.Trim()));
            }
        }

        foreach (var tag in XamlElementInspection.EnumerateStartTags(xaml))
        {
            var name = TagNameOf(tag.StartTag);
            if (name.Contains('.'))
            {
                continue;
            }

            if (name == "Setter")
            {
                var property = XamlElementInspection.GetAttribute(tag.StartTag, "Property");
                if (property == "Content" || property == "Header")
                {
                    Add(XamlElementInspection.GetAttribute(tag.StartTag, "Value"), tag.Line);
                }

                continue;
            }

            Add(XamlElementInspection.GetAttribute(tag.StartTag, "Content"), tag.Line);
            Add(XamlElementInspection.GetAttribute(tag.StartTag, "Header"), tag.Line);
            if (name == "AccessText")
            {
                Add(XamlElementInspection.GetAttribute(tag.StartTag, "Text"), tag.Line);
            }
        }

        foreach (Match text in Regex.Matches(xaml, @">(?<t>[^<>]*_[^<>]*)<"))
        {
            var index = text.Groups["t"].Index;
            var parent = XamlElementInspection.EnumerateEnclosingElements(xaml, index).LastOrDefault();
            if (parent != null && IsAccessKeyTextHost(TagNameOf(parent.StartTag)))
            {
                Add(text.Groups["t"].Value, XamlElementInspection.LineOf(xaml, index));
            }
        }

        return result.OrderBy(u => u.Line).ToList();
    }

    private static bool IsAccessKeyTextHost(string tagName)
    {
        // <Button.Content>本文</Button.Content> のプロパティ要素も、本体の直接テキストと同じに扱う
        var dot = tagName.IndexOf('.');
        if (dot >= 0)
        {
            var property = tagName.Substring(dot + 1);
            return property == "Content" || property == "Header";
        }

        return AccessKeyTextHosts.Contains(tagName);
    }

    private static string TagNameOf(string startTag)
        => Regex.Match(startTag, @"^<(?<n>[A-Za-z_][A-Za-z0-9_:.]*)").Groups["n"].Value;

    internal static IEnumerable<string> FindDuplicates(IEnumerable<AccessKeyUse> uses)
        => uses.GroupBy(u => u.Key)
            .Where(g => g.Count() > 1)
            .Select(g => $"Alt+{g.Key} が " + string.Join("、",
                g.Select(u => string.Format(CultureInfo.InvariantCulture, "{0} 行の「{1}」", u.Line, u.Text))));

    private static IReadOnlyDictionary<string, IReadOnlyList<AccessKeyUse>> ProductionAccessKeys()
        => FillForegroundPairs.EnumerateProductionXaml()
            .Select(f => (f.Name, Keys: ExtractAccessKeys(f.Text)))
            .Where(f => f.Keys.Count > 0)
            .ToDictionary(f => f.Name, f => f.Keys, StringComparer.Ordinal);

    /// <summary>
    /// 03_画面設計書 §5.3 の各表（見出し末尾の「（ダイアログ名）」→ Alt+キーの一覧）を読む。
    /// </summary>
    private static Dictionary<string, List<DocumentedAccessKey>> ReadDocumentedAccessKeys()
    {
        var text = File.ReadAllText(
            Path.Combine(TestPaths.GetSolutionRoot(), "docs", "design", "03_画面設計書.md")).Replace("\r\n", "\n");
        var start = text.IndexOf("\n### 5.3 ", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "03_画面設計書に §5.3 があること");
        var end = text.IndexOf("\n### 5.4 ", start, StringComparison.Ordinal);
        var section = end < 0 ? text.Substring(start) : text.Substring(start, end - start);

        var result = new Dictionary<string, List<DocumentedAccessKey>>(StringComparer.Ordinal);
        List<DocumentedAccessKey>? current = null;
        foreach (var line in section.Split('\n'))
        {
            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                // 「（ダイアログ名）」で終わらない見出しの下の表は、直前のダイアログへ混ぜない
                current = null;
                var heading = Regex.Match(line, @"^#### .*（(?<name>[A-Za-z]+)）\s*$");
                if (heading.Success)
                {
                    current = new List<DocumentedAccessKey>();
                    result[heading.Groups["name"].Value] = current;
                }

                continue;
            }

            var row = Regex.Match(line, @"^\| Alt\+(?<k>[A-Za-z0-9]) \| (?<item>[^|]+?) \|");
            if (row.Success && current != null)
            {
                current.Add(new DocumentedAccessKey(
                    char.ToUpperInvariant(row.Groups["k"].Value[0]), row.Groups["item"].Value.Trim()));
            }
        }

        return result;
    }

    private sealed record DocumentedAccessKey(char Key, string Item);

    /// <summary>
    /// アクセスキーの記法（<c>(_X)</c>・<c>_</c>・末尾のコロン）を除いた表示上のラベル。<c>__</c> はリテラルの <c>_</c>。
    /// </summary>
    internal static string LabelOf(string text)
    {
        var withoutKey = Regex.Replace(text, @"\(_[^_]\)", string.Empty);
        var label = Regex.Replace(withoutKey, "__|_", m => m.Value == "__" ? "_" : string.Empty);
        return label.Trim().TrimEnd(':', '：').Trim();
    }

    /// <summary>
    /// 設計書の項目が XAML のラベルと対応するか（一方が他方を含む）。設計書は「エクスポート データ種別」のように
    /// 画面上の位置を補った名前で書くので、完全一致ではなく包含で見る。
    /// </summary>
    /// <remarks>空のラベル・項目は対応しないとみなす（空文字はどの文字列にも含まれるので、そのままだと常に真になる）。</remarks>
    internal static bool CorrespondsTo(string item, string label)
        => item.Length > 0 && label.Length > 0
           && (item.IndexOf(label, StringComparison.Ordinal) >= 0 || label.IndexOf(item, StringComparison.Ordinal) >= 0);

    #endregion
}
