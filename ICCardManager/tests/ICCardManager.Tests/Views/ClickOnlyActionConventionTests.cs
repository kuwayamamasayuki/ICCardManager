using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2078: 「クリックでしか実行できない操作」を作らないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// メイン画面の警告エリアは <c>Border</c> に <c>MouseBinding</c>（LeftClick）を付けただけの作りで、
/// <c>Border</c> は <c>ContentElement</c> ではないためキーボードフォーカスもキー入力も受けなかった。
/// カードリーダーエラー警告（#1811）はクリックが<b>唯一の除去手段</b>なので、マウスを使わない職員には
/// 取り除く方法が存在しない状態だった。
/// </para>
/// <para>
/// 検査は 2 段に分ける。
/// </para>
/// <list type="number">
/// <item>
/// <b>リポジトリ全体の導出ガード</b>: <c>Views/</c> 配下の全 XAML を走査し、LeftClick の
/// <c>MouseBinding</c> にキーボードの代替があること。走査対象をファイル名で列挙しないのは、
/// 同型の画面が増えたときに静かに漏れるのを防ぐため（#1786）。
/// </item>
/// <item>
/// <b>警告エリアそのものの表明</b>: 「禁止された形の不在」（MouseBinding が残っていない）と
/// 「正しい形の存在」（<c>Button</c> がコマンドにバインドされ、フォーカス枠のトリガーを持つ）を
/// <b>対で</b>置く。不在だけを見ると、行から操作をまるごと取り去った実装でも緑になる（#1786）。
/// </item>
/// </list>
/// <para>
/// <b>「キーボードの代替がある」は 2 つの条件の積</b>。
/// ①<b>到達できる器の中にある</b>こと — クリック可能な行が <c>ListBox</c> / <c>ListView</c> /
/// <c>DataGrid</c> / <c>TreeView</c>（キーボード操作を備えた選択コンテナ）の内側にあること。
/// <c>ItemsControl</c> や素の <c>StackPanel</c> にはフォーカスも選択も無く、行へ辿り着く手段が無い。
/// ②<b>起動する手段がある</b>こと — 同じファイル内に同じコマンドの <c>KeyBinding</c> があるか
/// （カード一覧の Enter / Space）、その画面がキー入力を自前で処理している
/// （<c>KeyDown</c> / <c>PreviewKeyDown</c> の宣言。バス停名の候補リストは #2072 でこの形。
/// <c>ListBoxItem</c> は <c>Focusable="False"</c> のまま入力欄の ↓↑ / Enter で選ぶ）。
/// </para>
/// <para>
/// <b>②をファイル単位の粗い許容にしたのは意図的だが、それだけでは fail-open になる</b>。
/// <c>MainWindow.xaml</c> は履歴エリアに無関係な <c>PreviewKeyDown</c>（#1907）を持つため、
/// ②だけで判定すると<b>この Issue の欠陥そのものを免除してしまう</b>（初版が実際にそうだった）。
/// ①を積で課すことで、判定は「その行にキーボードで辿り着けるか」という守りたい性質に戻る。
/// 逆に①だけにすると、選択コンテナに入れただけで Enter が何も起こさない形を通す。
/// <b>粗い条件を置くなら、それ単独で判定が成立していないことを確かめる。</b>
/// </para>
/// <para>
/// <b>検査できない範囲</b>: 実際にフォーカスが当たるか・Enter で <c>Command</c> が起動するかは
/// WPF の実行時挙動であり、XAML テキストからは確かめられない（<c>Window</c> は STA 依存で
/// xUnit から生成できない）。ここで固定するのは「キーボードで到達できる作りになっているか」までで、
/// 実機でのフォーカス移動・読み上げは手動検証する。
/// </para>
/// <para>
/// 走査は <see cref="XamlElementInspection"/> へ集約し、私的コピーを増やさない（testing.md）。
/// </para>
/// </remarks>
public class ClickOnlyActionConventionTests
{
    /// <summary>キーボード操作を備えた選択コンテナ（この内側なら行へ辿り着ける）。</summary>
    private static readonly string[] KeyboardNavigableContainers =
    {
        "ListBox", "ListView", "DataGrid", "TreeView",
    };

    [Fact]
    public void LeftClickのMouseBindingにはキーボードの代替があること()
    {
        var violations = EnumerateViewXamlFiles()
            .SelectMany(file => FindViolations(File.ReadAllText(file))
                .Select(v => $"{Path.GetFileName(file)}:{v}"))
            .ToList();

        violations.Should().BeEmpty(
            "Issue #2078: クリックでしか実行できない操作はマウスを使わない職員が実行できない。" +
            "行そのものを Button にするか、キーボードで辿れる選択コンテナへ入れて起動キーを与えること（違反: " +
            string.Join(", ", violations) + "）");
    }

    /// <summary>
    /// 走査が空振りしていないことを、実在する LeftClick の <c>MouseBinding</c> 2 件で固定する。
    /// </summary>
    /// <remarks>
    /// 「違反が 0 件であること」だけを見ると、抽出が 1 件も拾わなくなった状態を検出できない（#1786）。
    /// 2 件は許容の経路が異なる（カード一覧＝<c>KeyBinding</c>、バス停名候補＝キーハンドラー）ので、
    /// どちらの経路が壊れても空振りに気付ける。
    /// </remarks>
    [Fact]
    public void 許容される2つの経路がどちらも走査対象に含まれること()
    {
        var mainWindow = ReadView("MainWindow.xaml");
        var busStopDialog = ReadView(Path.Combine("Dialogs", "BusStopInputDialog.xaml"));

        // 経路①: 同じコマンドの KeyBinding（カード一覧は Enter / Space で履歴を開ける）
        ExtractLeftClickCommands(mainWindow).Select(c => c.Command)
            .Should().Contain("OpenCardHistoryFromDashboardCommand");
        ExtractKeyBindingCommands(mainWindow)
            .Should().Contain("OpenCardHistoryFromDashboardCommand");

        // 経路②: 画面のキーハンドラー（バス停名の候補は入力欄の ↓↑ / Enter で選ぶ。#2072）
        ExtractLeftClickCommands(busStopDialog).Select(c => c.Command)
            .Should().Contain("SelectSuggestionCommand");
        ExtractKeyBindingCommands(busStopDialog)
            .Should().NotContain("SelectSuggestionCommand", "この画面の代替は KeyBinding ではない");
        HasKeyHandler(busStopDialog).Should().BeTrue(
            "入力欄の PreviewKeyDown が候補リストのキー操作を担う（Issue #2072）");
    }

    [Fact]
    public void 警告行はクリック専用のMouseBindingを持たないこと()
    {
        ExtractLeftClickCommands(ExtractWarningItemsControl())
            .Should().BeEmpty(
                "Issue #2078: 警告行の操作は Button（Enter / Space が既定で効き UIA の Invoke が付く）で行う。" +
                "MouseBinding へ戻すとキーボードから実行できなくなる");
    }

    [Fact]
    public void 警告行はフォーカスできるButtonで操作できること()
    {
        var warningArea = ExtractWarningItemsControl();

        var button = XamlElementInspection
            .EnumerateElements(warningArea, "Button")
            .FirstOrDefault(b => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(b.StartTag, "Command")) == "HandleWarningClickCommand");

        button.Should().NotBeNull(
            "Issue #2078: 警告行は HandleWarningClickCommand にバインドした Button であること" +
            "（Border + MouseBinding ではキーボードフォーカスもキー入力も受けない）");

        XamlElementInspection.GetAttribute(button!.StartTag, "CommandParameter")
            .Should().Be("{Binding}", "操作対象の警告そのものをコマンドへ渡すこと");

        XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(button.StartTag, "AutomationProperties.Name"))
            .Should().Be("DisplayText",
                "読み上げ名は警告文そのものを動的に読ませる（固定ラベルを置くと中身が読まれない。#1812）");
    }

    /// <summary>
    /// フォーカス枠のトリガーが種別の <c>DataTrigger</c> より<b>後ろ</b>にあることを固定する。
    /// </summary>
    /// <remarks>
    /// トリガーは後勝ちのため、順序が逆だと DB 接続断の行（<c>ErrorBorderBrush</c> で枠線を塗る）だけ
    /// フォーカス枠が出ず、キーボードで辿る職員が「いまどこにいるか」を見失う。
    /// <b>「フォーカス枠がある」ことだけを表明すると、この順序の退行を検出できない。</b>
    /// </remarks>
    [Fact]
    public void 警告行のフォーカス枠が種別の色より後ろに置かれていること()
    {
        var triggers = XamlElementInspection
            .EnumerateElements(ExtractWarningItemsControl(), "ControlTemplate.Triggers")
            .Select(t => t.Body)
            .FirstOrDefault();

        triggers.Should().NotBeNull("警告行の Button はテンプレートのトリガーで見た目を切り替える");

        var focusIndex = triggers!.IndexOf("IsKeyboardFocused", StringComparison.Ordinal);
        var typeIndex = triggers.IndexOf("DatabaseConnectionLost", StringComparison.Ordinal);

        focusIndex.Should().BeGreaterThan(0, "キーボードフォーカス時の枠線トリガーがあること（Issue #2078）");
        typeIndex.Should().BeGreaterThan(0, "DB 接続断の色分けトリガーがあること（Issue #1110）");
        focusIndex.Should().BeGreaterThan(typeIndex,
            "トリガーは後勝ちのため、フォーカス枠を種別の色より後ろに置かないと" +
            "DB 接続断の行だけフォーカス位置が見えなくなる（Issue #2078）");
    }

    [Fact]
    public void 検査ロジックが違反と適合をサンプル入力で区別すること()
    {
        const string clickRow =
            @"<MouseBinding MouseAction=""LeftClick"" Command=""{Binding DataContext.DoItCommand, "
            + @"RelativeSource={RelativeSource AncestorType=ItemsControl}}"" CommandParameter=""{Binding}""/>";
        const string keyBinding = @"<KeyBinding Key=""Enter"" Command=""{Binding DoItCommand}""/>";
        const string keyHandler = @"<TextBox PreviewKeyDown=""Box_PreviewKeyDown""/>";

        // 器にも起動手段にも欠ける ＝ #2078 の欠陥そのもの
        FindViolations($"<ItemsControl>{clickRow}</ItemsControl>").Should().ContainSingle();

        // 器はあるが起動手段が無い（Enter が何も起こさない）
        FindViolations($"<ListBox>{clickRow}</ListBox>").Should().ContainSingle();

        // 起動手段はあるが器が無い（行へ辿り着けない）＝ MainWindow の fail-open を塞ぐ
        FindViolations($"{keyHandler}<ItemsControl>{clickRow}</ItemsControl>").Should().ContainSingle();
        FindViolations($"{keyBinding}<ItemsControl>{clickRow}</ItemsControl>").Should().ContainSingle();

        // 器＋KeyBinding／器＋キーハンドラーは許容する（正当な既存実装を塞がない）
        FindViolations($"{keyBinding}<ListBox>{clickRow}</ListBox>").Should().BeEmpty();
        FindViolations($"{keyHandler}<ListView>{clickRow}</ListView>").Should().BeEmpty();

        // 別コマンドの KeyBinding では許容しない
        FindViolations(
                @"<KeyBinding Key=""Enter"" Command=""{Binding OtherCommand}""/><ListBox>" + clickRow + "</ListBox>")
            .Should().ContainSingle();

        // LeftClick 以外（ダブルクリック等）は対象外
        FindViolations(
                @"<ItemsControl><MouseBinding MouseAction=""LeftDoubleClick"" Command=""{Binding DoItCommand}""/></ItemsControl>")
            .Should().BeEmpty();

        // 規約の理由を書いたコメント自体を違反として拾わない（極性の反転、#1692）
        FindViolations($"<!-- {clickRow} は禁止 -->").Should().BeEmpty();

        // 行番号はコメントを挟んでもずれない
        FindViolations("<!-- 1 行目\n2 行目 -->\n<ItemsControl>\n" + clickRow + "\n</ItemsControl>")
            .Should().ContainSingle().Which.Should().StartWith("4 ");

        // キーハンドラーの判定は KeyDown / PreviewKeyDown の両方を認める（KeyUp は認めない）
        HasKeyHandler(@"<TextBox KeyDown=""Box_KeyDown""/>").Should().BeTrue();
        HasKeyHandler(keyHandler).Should().BeTrue();
        HasKeyHandler(@"<TextBox PreviewKeyUp=""Box_PreviewKeyUp""/>").Should().BeFalse();
    }

    /// <summary>
    /// 1 ファイル分の XAML から、キーボードの代替を欠く LeftClick を列挙する。
    /// </summary>
    private static IReadOnlyList<string> FindViolations(string rawXaml)
    {
        var source = XamlElementInspection.StripXmlComments(rawXaml);
        var keyboardCommands = ExtractKeyBindingCommands(source);
        var hasKeyHandler = HasKeyHandler(source);
        var outsideContainer = ExtractLeftClickCommands(MaskNavigableContainers(source))
            .Select(c => c.Line)
            .ToHashSet();

        return ExtractLeftClickCommands(source)
            .Where(c => outsideContainer.Contains(c.Line)
                        || !(keyboardCommands.Contains(c.Command) || hasKeyHandler))
            .Select(c => $"{c.Line} ({c.Command})")
            .ToList();
    }

    /// <summary>
    /// キーボード操作を備えた選択コンテナの<b>本体</b>を空白へ潰す（行数は保つ）。
    /// </summary>
    /// <remarks>
    /// 潰した後に残る LeftClick が「器の外」にあるもの。
    /// <see cref="XamlElementInspection.EnumerateElements"/> は部分文字列を返すため行番号が相対値になる。
    /// 元の文字列の側を潰せば、報告する行番号は実ファイルのままになる
    /// （コメント除去が改行を残すのと同じ理由。#2073）。
    /// </remarks>
    private static string MaskNavigableContainers(string source)
    {
        var bodies = KeyboardNavigableContainers
            .SelectMany(container => XamlElementInspection.EnumerateElements(source, container))
            .Select(e => e.Body)
            .Where(body => body.Length > 0)
            .ToList();

        var masked = source;
        foreach (var body in bodies)
        {
            // 入れ子のコンテナは外側を潰した時点で内側も消えるため、見つからなければ読み飛ばす
            var index = masked.IndexOf(body, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            masked = masked.Remove(index, body.Length)
                .Insert(index, new string('\n', body.Count(ch => ch == '\n')));
        }

        return masked;
    }

    /// <summary>その画面がキー入力を自前で処理しているか（<c>KeyDown</c> / <c>PreviewKeyDown</c> の宣言）。</summary>
    private static bool HasKeyHandler(string xaml)
        => xaml.IndexOf("KeyDown=", StringComparison.Ordinal) >= 0;

    private static IReadOnlyList<ClickAction> ExtractLeftClickCommands(string xaml)
        => XamlElementInspection.EnumerateElements(xaml, "MouseBinding")
            .Where(e => XamlElementInspection.GetAttribute(e.StartTag, "MouseAction") == "LeftClick")
            .Select(e => new
            {
                e.Line,
                Command = XamlElementInspection.GetBindingPropertyName(
                    XamlElementInspection.GetAttribute(e.StartTag, "Command")),
            })
            .Where(x => x.Command != null)
            .Select(x => new ClickAction(x.Line, x.Command!))
            .ToList();

    private static ISet<string> ExtractKeyBindingCommands(string xaml)
        => XamlElementInspection.EnumerateElements(xaml, "KeyBinding")
            .Select(e => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(e.StartTag, "Command")))
            .Where(c => c != null)
            .Select(c => c!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>警告エリアの <c>ItemsControl</c>（ItemsSource=WarningMessages）の本体を返す。</summary>
    private static string ExtractWarningItemsControl()
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadView("MainWindow.xaml"));

        var itemsControl = XamlElementInspection
            .EnumerateElements(xaml, "ItemsControl")
            .FirstOrDefault(e => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(e.StartTag, "ItemsSource")) == "WarningMessages");

        itemsControl.Should().NotBeNull("警告エリアの ItemsControl（WarningMessages）が MainWindow.xaml 内に存在すべき");
        return itemsControl!.Body;
    }

    private static string ReadView(string relativePath)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} が {path} に存在すべき");
        return File.ReadAllText(path);
    }

    private static IEnumerable<string> EnumerateViewXamlFiles()
    {
        var viewsRoot = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views");
        Directory.Exists(viewsRoot).Should().BeTrue($"View のソースルート {viewsRoot} が存在すべき");

        return Directory
            .EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private sealed record ClickAction(int Line, string Command);
}
