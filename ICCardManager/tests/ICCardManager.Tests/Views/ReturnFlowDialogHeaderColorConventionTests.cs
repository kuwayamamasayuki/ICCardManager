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

    /// <remarks>
    /// アクセス修飾子だけを起点にすると <c>partial void On…Changed</c>（`[ObservableProperty]` の
    /// 変更ハンドラー。`MainViewModel` に実在）を 1 件も拾えない（コードレビューで検出）。
    /// 修飾子の集合を広げ、1 つ以上あることだけを要求する。
    /// </remarks>
    private static readonly Regex MethodDeclarationRegex = new(
        @"(?m)^[ \t]*(?:(?:private|protected|internal|public|static|async|partial|virtual|override|sealed)[ \t]+)+"
        + @"[^\r\n;()=]*?\b(?<name>\w+)[ \t]*\(");

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
        => CollectMethodBodies(codeOnly, out _);

    /// <param name="overloadedNames">
    /// 同名の宣言が 2 つ以上あった名前。マーカーの引き当ては先頭一致なので
    /// <b>1 つ目の宣言しか見えない</b>。呼び出し側はこれと到達集合の交わりが空であることを
    /// 表明し、オーバーロードが増えた日に<b>黙って見落とすのではなく赤くなる</b>ようにする
    /// （コードレビューで検出）。
    /// </param>
    private static IReadOnlyDictionary<string, string> CollectMethodBodies(
        string codeOnly, out IReadOnlyCollection<string> overloadedNames)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var overloaded = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in MethodDeclarationRegex.Matches(codeOnly))
        {
            var name = match.Groups["name"].Value;
            if (bodies.ContainsKey(name))
            {
                overloaded.Add(name);
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

        overloadedNames = overloaded;
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

    /// <remarks>
    /// <para>
    /// ダイアログを開く形は 1 つではない。<c>INavigationService.ShowDialogAsync&lt;T&gt;</c> のほか、
    /// DI から解決して <c>ShowDialog()</c> を呼ぶ形（<c>IncompleteBusStopDialog.xaml.cs</c> に実在）
    /// があり、型引数は <c>using</c> があれば <c>Views.Dialogs.</c> の修飾を持たない。
    /// どちらの形でも拾えるようにする（コードレビューで検出）。
    /// </para>
    /// <para>
    /// 型引数が <c>Dialog</c> で終わることを要求して、無関係なジェネリック呼び出しを拾わない。
    /// </para>
    /// </remarks>
    private static readonly Regex ShowDialogRegex = new(
        @"(?:ShowDialogAsync|GetRequiredService|GetService)<(?:Views\.Dialogs\.)?(?<dialog>\w+Dialog)>");

    /// <summary>
    /// <paramref name="entryPoint"/> から到達するメソッドを推移的に辿り、開かれるダイアログ名を集める。
    /// </summary>
    private static IReadOnlyList<string> CollectReachableDialogs(string codeOnly, string entryPoint)
    {
        var methods = CollectMethodBodies(codeOnly, out var overloadedNames);
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

        // 到達した名前にオーバーロードがあると、2 つ目以降の本体を黙って見落とす。
        // 「見落とし得る状態」になった時点で赤くする（コードレビューで検出）。
        visited.Intersect(overloadedNames, StringComparer.Ordinal).Should().BeEmpty(
            "到達するメソッドに同名のオーバーロードが無いこと。"
                + "増えた場合は引き当てを引数の数まで見る形へ広げること");

        return dialogs.ToList();
    }

    #endregion

    #region ヘッダーの切り出し

    /// <summary>ダイアログ上部の説明ヘッダー（<c>Grid.Row="0"</c> の Border）。</summary>
    /// <param name="Body">
    /// ヘッダーの<b>開始タグと本体の両方</b>。本体だけを見ると、開始タグの属性
    /// （<c>BorderBrush="{DynamicResource LendingForegroundBrush}"</c> 等）で暖色を戻す形が
    /// 素通りする（コードレビューで検出）。
    /// </param>
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

        return new Header(background!, foreground!, border.StartTag + border.Body);
    }

    /// <summary>一覧の金額欄（<c>Text</c> が金額にバインドされた TextBlock）を返す。</summary>
    private static XamlElementInspection.XamlElement? FindAmountTextBlock(string strippedXaml)
        => XamlElementInspection.EnumerateElements(strippedXaml, "TextBlock")
            .FirstOrDefault(e =>
            {
                var text = XamlElementInspection.GetAttribute(e.StartTag, "Text");
                var property = XamlElementInspection.GetBindingPropertyName(text);
                return property != null
                    && (property.EndsWith("AmountDisplay", StringComparison.Ordinal)
                        || property.EndsWith("ExpenseDisplay", StringComparison.Ordinal));
            });

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
    public void 金額欄は検査の対象外であること()
    {
        // #2079 の判断: 金額の強調色は「貸出」のシグナルではなく、LedgerDetailDialog /
        // LedgerRowEditDialog と共用の「金額の強調色」。ヘッダーだけを見る検査であることを
        // 対で固定する — 走査をファイル全体へ広げた実装は、アプリ全体の金額表現を巻き込んで
        // 赤くなる（規約が推奨しない方向へ修正者を誘導する。#1786）。
        //
        // ここで固定するのは「金額欄がヘッダーの外にあり、本検査が触れていない」ことだけで、
        // 金額欄が特定のブラシであることは固定しない。リテラルで留めると、金額専用の
        // ブラシを新設する（＝このブラシの二重用途を解消する）変更でこのテストが赤になり、
        // 規約が推奨する方向の修正を妨げる（コードレビューで検出）。
        var codeOnly = ReadMainViewModelCodeOnly();

        foreach (var dialog in CollectReachableDialogs(codeOnly, ReturnFlowEntryPoint))
        {
            var xaml = File.ReadAllText(
                ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", $"{dialog}.xaml")));
            var stripped = XamlElementInspection.StripXmlComments(xaml);

            var amount = FindAmountTextBlock(stripped);
            amount.Should().NotBeNull("{0} の一覧に金額欄が存在すること", dialog);

            ExtractHeader(xaml, dialog).Body.Should().NotContain(
                amount!.StartTag,
                "{0} の金額欄はヘッダーの外にあること（本検査の対象外であることの根拠）",
                dialog);
        }
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
    public void 宣言の走査がアクセス修飾子を持たないメソッドにも届いていること()
    {
        // `[ObservableProperty]` の変更ハンドラーは `partial void On…Changed` で、
        // アクセス修飾子を持たない。ここから非同期処理を起こす形（`_ = XxxAsync()`、#1996）が
        // あるため、走査から落ちると呼び出し関係が途切れる（コードレビューで検出）。
        var codeOnly = ReadMainViewModelCodeOnly();
        var methods = CollectMethodBodies(codeOnly);

        var partialHandlers = Regex.Matches(codeOnly, @"(?m)^[ \t]*partial void[ \t]+(?<name>\w+)[ \t]*\(")
            .Cast<Match>()
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        partialHandlers.Should().NotBeEmpty(
            "MainViewModel に partial なハンドラーが実在すること（消えたら本テストの前提を書き直す）");
        methods.Keys.Should().Contain(
            partialHandlers, "アクセス修飾子を持たない宣言も走査対象に含まれること");
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

        // 開始タグの属性で暖色を戻す形も検出すること。本体だけを走査する実装では
        // 背景・見出しの検査を通り抜ける（コードレビューで検出）
        const string warmBorderOnly = @"<Window><Grid>
    <Border Grid.Row=""0"" Background=""{DynamicResource ReturnBackgroundBrush}""
            BorderBrush=""{DynamicResource LendingForegroundBrush}"" BorderThickness=""2"">
        <StackPanel>
            <TextBlock Text=""見出し"" Foreground=""{DynamicResource ReturnForegroundBrush}""/>
        </StackPanel>
    </Border>
</Grid></Window>";

        var warmBorder = ExtractHeader(warmBorderOnly, "sample");
        ResourceKeyOf(warmBorder.Background).Should().Be(ReturnBackground, "背景と見出しは規約どおりでも");
        ResourceKeyOf(warmBorder.TitleForeground).Should().Be(ReturnForeground);
        warmBorder.Body.Should().Contain("Lending", "開始タグの属性に残った暖色を検出すること");
    }

    #endregion
}
