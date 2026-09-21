using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 返却フローの途中で出るダイアログが「貸出」のシグナル色を使っていないことの静的検査（Issue #2079）。
/// </summary>
/// <remarks>
/// <para>
/// バス停名入力ダイアログは返却処理の途中（返却 → バス停名入力 → 同行者数入力）に出るのに、
/// ヘッダーへ <c>LendingBackgroundBrush</c> / <c>LendingForegroundBrush</c> を使っていた。
/// 画面の色が「返却（寒色）→ 貸出（暖色）→ 返却（寒色）」と切り替わり、
/// 暖色＝貸出・寒色＝返却という区別（03_画面設計書 §4.2.1）が崩れる。
/// </para>
/// <para>
/// <c>ReturnBackgroundBrush</c>（寒色）は「返却完了」に加えて情報ヘッダー・操作ヒントの汎用色として
/// 兼用することが設計判断として明記されている（Issue #1399）。一方 <c>LendingBackgroundBrush</c> に
/// そうした汎用用途は無く、**貸出専用のシグナル色**である。
/// </para>
/// <para>
/// <c>Window</c> のコードビハインドは STA 依存で xUnit から実体化できないため、XAML のテキスト上で
/// 固定する（#1817 / #1794 / #2009 と同じ形）。
/// </para>
/// <para>
/// 走査対象は**ファイル名で列挙せず**、<c>MainViewModel.HandleReturnSuccessAsync</c> から到達する
/// <c>ShowDialogAsync&lt;Views.Dialogs.…&gt;</c> の型引数として導出する（#1786）。
/// 返却フローへダイアログを足した日に検査から静かに漏れないようにするため。
/// 展開は private メソッドを推移的に辿る — <c>CompanionCountInputDialog</c> は
/// <c>HandleReturnSuccessAsync</c> の直接の本体ではなくヘルパー
/// （<c>ShowCompanionCountInputIfNeededAsync</c>）の内側で開かれており、
/// 「1 つのメソッドの本体」を走査単位にすると素通りする（#1997）。
/// </para>
/// </remarks>
public class ReturnFlowDialogHeaderColorConventionTests
{
    private const string ReturnBackground = "ReturnBackgroundBrush";
    private const string ReturnForeground = "ReturnForegroundBrush";

    /// <summary>返却フローの起点。ここから到達するダイアログが検査対象になる。</summary>
    private const string ReturnFlowEntryPoint = "HandleReturnSuccessAsync";

    #region 走査対象の導出（MainViewModel の呼び出し関係から）

    private static string ReadMainViewModelCodeOnly()
    {
        var path = Path.Combine(
            TestPaths.GetProductionSourceRoot(), "ViewModels", "MainViewModel.cs");
        File.Exists(path).Should().BeTrue($"検査対象のソースが見つからない: {path}");
        return TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
    }

    private static readonly Regex MethodDeclarationRegex = new(
        @"(?m)^[ \t]*(?:private|protected|internal|public)[^\r\n;()=]*?\b(?<name>\w+)[ \t]*\(");

    /// <summary>
    /// メソッド名 → 本体（<c>{ }</c> または <c>=&gt; …;</c>）の対応表を作る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 波括弧の対応付けは <see cref="TestSourceInspection.ExtractMethodBody"/> に任せ、複製しない。
    /// 引き当ては「アクセス修飾子からメソッド名まで」の実テキストをマーカーにして行う。
    /// </para>
    /// <para>
    /// 式本体（<c>=&gt;</c>）のメンバーを素通りさせると、<c>ExtractMethodBody</c> が
    /// <b>次のメソッドの波括弧</b>を掴んで無関係な本体を返す。宣言の括弧を数えて分岐する。
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, string> CollectMethodBodies(string codeOnly)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in MethodDeclarationRegex.Matches(codeOnly))
        {
            var name = match.Groups["name"].Value;
            if (bodies.ContainsKey(name))
            {
                continue;
            }

            var afterParameters = SkipParameterList(codeOnly, match.Index + match.Length - 1);
            if (afterParameters < 0)
            {
                continue;
            }

            var rest = codeOnly.Substring(afterParameters).TrimStart();
            if (rest.StartsWith("=>", StringComparison.Ordinal))
            {
                var end = rest.IndexOf(';');
                bodies[name] = end < 0 ? rest : rest.Substring(0, end);
                continue;
            }

            // 末尾の "(" と直前の空白を落とした部分がマーカー（IndexOf でこの宣言に当たる）
            var marker = match.Value.Substring(0, match.Value.Length - 1).TrimEnd().TrimStart();
            bodies[name] = TestSourceInspection.ExtractMethodBody(codeOnly, marker);
        }

        return bodies;
    }

    /// <summary>開き括弧の位置から、対応する閉じ括弧の次の位置を返す（見つからなければ -1）。</summary>
    private static int SkipParameterList(string codeOnly, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < codeOnly.Length; i++)
        {
            if (codeOnly[i] == '(')
            {
                depth++;
            }
            else if (codeOnly[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }
        }

        return -1;
    }

    private static readonly Regex ShowDialogRegex =
        new(@"ShowDialogAsync<Views\.Dialogs\.(?<dialog>\w+)>");

    /// <summary>
    /// <paramref name="entryPoint"/> から到達するメソッドを推移的に辿り、開かれるダイアログ名を集める。
    /// </summary>
    private static IReadOnlyList<string> CollectReachableDialogs(string codeOnly, string entryPoint)
    {
        var methods = CollectMethodBodies(codeOnly);
        methods.Should().ContainKey(
            entryPoint, "返却フローの起点が MainViewModel に存在すること（リネームしたら本定数も更新する）");

        var dialogs = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(entryPoint);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!visited.Add(name) || !methods.TryGetValue(name, out var body))
            {
                continue;
            }

            foreach (Match match in ShowDialogRegex.Matches(body))
            {
                dialogs.Add(match.Groups["dialog"].Value);
            }

            foreach (var candidate in methods.Keys)
            {
                if (!visited.Contains(candidate)
                    && Regex.IsMatch(body, $@"(?<![\w.]){Regex.Escape(candidate)}\s*\("))
                {
                    queue.Enqueue(candidate);
                }
            }
        }

        return dialogs.ToList();
    }

    #endregion

    #region ヘッダーの切り出し

    /// <summary>ダイアログ上部の説明ヘッダー（<c>Grid.Row="0"</c> の Border）。</summary>
    private sealed record Header(string Background, string TitleForeground, string Body);

    private static Header ExtractHeader(string xaml, string label)
    {
        // コメントは先に落とす。規約の理由を書いたコメント自体が検出される極性の反転を避ける（#1692）
        var stripped = XamlElementInspection.StripXmlComments(xaml);

        var border = XamlElementInspection.EnumerateElements(stripped, "Border")
            .FirstOrDefault(e => XamlElementInspection.GetAttribute(e.StartTag, "Grid.Row") == "0");

        border.Should().NotBeNull($"{label} に説明ヘッダーの Border（Grid.Row=\"0\"）が存在すること");

        var background = XamlElementInspection.GetAttribute(border!.StartTag, "Background");
        background.Should().NotBeNull($"{label} の説明ヘッダーに Background が指定されていること");

        var title = XamlElementInspection.EnumerateElements(border.Body, "TextBlock").FirstOrDefault();
        title.Should().NotBeNull($"{label} の説明ヘッダーに見出しの TextBlock が存在すること");

        var foreground = XamlElementInspection.GetAttribute(title!.StartTag, "Foreground");
        foreground.Should().NotBeNull($"{label} の説明ヘッダーの見出しに Foreground が指定されていること");

        return new Header(background!, foreground!, border.Body);
    }

    private static string ResourceKeyOf(string markupExtension)
    {
        var match = Regex.Match(markupExtension, @"\{\s*(?:Dynamic|Static)Resource\s+(?<key>\w+)\s*\}");
        match.Success.Should().BeTrue(
            "ブラシは DynamicResource / StaticResource で参照すること（色リテラルの直書きは #2073 で禁止）: {0}",
            markupExtension);
        return match.Groups["key"].Value;
    }

    #endregion

    #region 本体（欠陥を突く側）

    [Fact]
    public void 返却フローのダイアログのヘッダーが返却系の寒色であること()
    {
        var codeOnly = ReadMainViewModelCodeOnly();
        var dialogs = CollectReachableDialogs(codeOnly, ReturnFlowEntryPoint);

        dialogs.Should().NotBeEmpty("返却フローから開かれるダイアログが導出できること");

        foreach (var dialog in dialogs)
        {
            var path = ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", $"{dialog}.xaml"));
            var header = ExtractHeader(File.ReadAllText(path), dialog);

            ResourceKeyOf(header.Background).Should().Be(
                ReturnBackground,
                "{0} は返却処理の途中で出る画面なので、ヘッダーの背景は返却系の寒色であること。"
                    + "貸出の暖色を使うと「貸出になった？」と読まれる（Issue #2079）",
                dialog);

            ResourceKeyOf(header.TitleForeground).Should().Be(
                ReturnForeground,
                "{0} のヘッダー見出しの文字色も返却系へ揃えること（Issue #2079）",
                dialog);
        }
    }

    [Fact]
    public void 返却フローのダイアログのヘッダーに貸出のシグナル色を使わないこと()
    {
        var codeOnly = ReadMainViewModelCodeOnly();

        foreach (var dialog in CollectReachableDialogs(codeOnly, ReturnFlowEntryPoint))
        {
            var path = ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", $"{dialog}.xaml"));
            var header = ExtractHeader(File.ReadAllText(path), dialog);

            // ヘッダーの内側（カウントダウン等の副次的な文字色も含む）に暖色が紛れ込まないこと
            header.Body.Should().NotContain(
                "Lending",
                "{0} のヘッダーは返却フローの表示領域なので、貸出のシグナル色を含まないこと（Issue #2079）",
                dialog);
        }
    }

    #endregion

    #region 対の表明（正当な既存挙動を塞いでいないこと・走査が届いていること）

    [Fact]
    public void 金額欄の強調色は検査の対象外であること()
    {
        // #2079 の判断: 金額の LendingForegroundBrush は「貸出」のシグナルではなく、
        // LedgerDetailDialog / LedgerRowEditDialog と共用の「金額の強調色」。
        // ヘッダーだけを見る検査であることを対で固定する — 走査をファイル全体へ広げた実装は、
        // アプリ全体の金額表現を巻き込んで赤くなる（規約が推奨しない方向へ修正者を誘導する。#1786）。
        var busStop = File.ReadAllText(
            ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "BusStopInputDialog.xaml")));
        var stripped = XamlElementInspection.StripXmlComments(busStop);

        stripped.Should().Contain(
            "LendingForegroundBrush",
            "金額欄の強調色は現状維持であること（ここが消えたら #2079 の判断が変わっている）");

        ExtractHeader(busStop, "BusStopInputDialog").Body.Should().NotContain(
            "LendingForegroundBrush", "ただしヘッダーの内側には残っていないこと");
    }

    [Fact]
    public void 走査が直接の本体だけでなくヘルパーの内側まで届いていること()
    {
        var codeOnly = ReadMainViewModelCodeOnly();
        var dialogs = CollectReachableDialogs(codeOnly, ReturnFlowEntryPoint);

        dialogs.Should().Contain(
            "BusStopInputDialog", "返却フローの起点の本体から直接開かれるダイアログ");
        dialogs.Should().Contain(
            "CompanionCountInputDialog", "ヘルパーの内側で開かれるダイアログ（#1997）");

        // 展開が実際に効いていることを対で固定する。1 段で止めた実装（直接の本体だけを見る）では
        // CompanionCountInputDialog が集まらないことを、同じ導出処理で示す。
        var entryBody = CollectMethodBodies(codeOnly)[ReturnFlowEntryPoint];
        ShowDialogRegex.Matches(entryBody).Cast<Match>()
            .Select(m => m.Groups["dialog"].Value)
            .Should().NotContain(
                "CompanionCountInputDialog",
                "直接の本体には現れない＝展開なしでは拾えないこと（この前提が変わったら対の表明を書き直す）");
    }

    [Fact]
    public void 検査ロジックが既知のサンプル入力で違反と適合を区別できること()
    {
        // 実データが規約を満たしていても検査が働き続けることを示す（#1786 の「空振り検出」）。
        const string violating = @"<Window><Grid>
    <!-- ReturnBackgroundBrush を使うこと というコメント自体は違反にしない -->
    <Border Grid.Row=""0"" Background=""{DynamicResource LendingBackgroundBrush}"">
        <StackPanel>
            <TextBlock Text=""見出し"" Foreground=""{DynamicResource LendingForegroundBrush}""/>
        </StackPanel>
    </Border>
</Grid></Window>";

        const string conforming = @"<Window><Grid>
    <Border Grid.Row=""0"" Background=""{DynamicResource ReturnBackgroundBrush}"">
        <StackPanel>
            <TextBlock Text=""見出し"" Foreground=""{DynamicResource ReturnForegroundBrush}""/>
        </StackPanel>
    </Border>
</Grid></Window>";

        var bad = ExtractHeader(violating, "sample");
        ResourceKeyOf(bad.Background).Should().Be("LendingBackgroundBrush");
        ResourceKeyOf(bad.TitleForeground).Should().Be("LendingForegroundBrush");
        bad.Body.Should().Contain("Lending", "コメントを剥がしても本体の違反は残ること");

        var good = ExtractHeader(conforming, "sample");
        ResourceKeyOf(good.Background).Should().Be(ReturnBackground);
        ResourceKeyOf(good.TitleForeground).Should().Be(ReturnForeground);
        good.Body.Should().NotContain("Lending");
    }

    #endregion
}
