using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2484: メイン画面・履歴パネルのモーダルが、表示中のカード読み取りの抑制を通ることを静的に固定する。
/// </summary>
/// <remarks>
/// <para>
/// 抑制は、両画面が受け取った <c>INavigationService</c> を <c>ModalCardReadingSuppressingNavigationService</c> で
/// 包むことで掛けている。挙動は <c>MainViewModelTests</c> / <c>HistoryPanelViewModelTests</c> の「Issue #2484」region が
/// 代表のダイアログで表明するが、<b>包みを通らない表示</b>（<c>Window</c> を直接生成して <c>ShowDialog()</c>、
/// <c>MessageBox</c> の直呼び）を足されると、その表示だけ黙って抑制が漏れる。ここではその経路を列挙して塞ぐ。
/// </para>
/// <para>
/// 破れる経路: ①包みを外す（コンストラクタで受け取った値をそのまま保持する） ②<c>Window.ShowDialog()</c> の直呼び
/// ③<c>MessageBox.Show</c> / <c>OwnedMessageBox.Show</c> の直呼び ④<c>ErrorDialogHelper.Show*</c>（MessageBox を出す）。
/// ⑤コンストラクタ引数 <c>navigationService</c> を包みの生成以外で使う（別のフィールド・ローカルへ渡して直接表示する）
/// ⑥<c>INavigationService</c> / <c>IDialogService</c> 型のフィールド・プロパティを別に持つ（PR #2491 のレビューで ⑤⑥ を追加）。
/// ②は <c>BeginModalCardReadingSuppression</c> の <c>using</c> の内側なら認める（<c>MergeHistoryDialog</c> が該当）。
/// 職員認証（<c>IStaffAuthService</c>）は認証ダイアログ自身が抑制を取る（<c>CardReadingSource.Authentication</c>）ので対象外。
/// </para>
/// <para>
/// <b>検査が見ない経路</b>（PR #2491 のレビュー 2 回目で明記）: ①ダイアログを出す型のメンバーは <c>INavigationService</c> /
/// <c>IDialogService</c>（とその実装）だけを見る。<c>IStaffAuthService</c> のような別のサービスの中でモーダルを出す形、
/// <c>Window</c> 型のフィールドを持って後で表示する形は拾わない（<c>.ShowDialog(</c> の直呼びは②で拾う）。
/// ②<c>App.Current.ServiceProvider</c> などの DI からその場で <c>INavigationService</c> を取り出して表示する形は拾わない
/// （#853 で ViewModel は <c>App.Current.ServiceProvider</c> に依存しない方針）。③<c>ShowDialog</c> 以外の名前でモーダルを
/// 出すメソッド（独自の拡張メソッド等）は拾わない。これらを足すときは、表示の範囲を <c>BeginModalCardReadingSuppression</c> の
/// <c>using</c> で囲み、ここへ検査を足すこと。
/// </para>
/// </remarks>
public class ModalCardReadingSuppressionConventionTests
{
    /// <summary>
    /// <c>Window.ShowDialog()</c> と、引数付きの <c>ShowDialog(owner)</c>（<c>SaveFileDialog</c> / <c>OpenFileDialog</c> 等）。
    /// 包み（<c>_navigationService</c>）を受け手にした呼び出しは除く（型引数を推論させた <c>_navigationService.ShowDialog(configure)</c> を含む）。
    /// </summary>
    private static readonly Regex DirectWindowShowDialog = new(@"(?<!\b_navigationService\s*)\.ShowDialog\s*\(", RegexOptions.Compiled);

    private static readonly Regex DirectMessageBox = new(
        @"MessageBox\s*\.\s*Show\s*\(|ErrorDialogHelper\s*\.\s*Show\w*\s*\(", RegexOptions.Compiled);

    private static readonly Regex NavigationAssignment = new(@"\b_navigationService\s*=\s*([^;]+);", RegexOptions.Compiled);

    private const string WrapperConstruction = "new ModalCardReadingSuppressingNavigationService(";

    private static readonly Regex NavigationParameterUse = new(@"\bnavigationService\b", RegexOptions.Compiled);

    private static readonly Regex NavigationParameterDeclaration = new(
        @"\bINavigationService\??\s+navigationService\b", RegexOptions.Compiled);

    /// <summary>
    /// <c>INavigationService</c> / <c>IDialogService</c>（とその実装）型のフィールド・プロパティ・ローカルの宣言。
    /// 引数の宣言（後ろが <c>,</c> / <c>)</c>）は拾わない。
    /// </summary>
    private static readonly Regex DialogServiceMemberDeclaration = new(
        @"\b(?:I?NavigationService|I?DialogService)\??\s+(\w+)\s*(?:;|=|\{)", RegexOptions.Compiled);

    /// <summary>
    /// コンストラクタ引数 <c>navigationService</c> を、包みの生成（<c>new ModalCardReadingSuppressingNavigationService(...)</c> の
    /// 引数）の外で使っている箇所を列挙する。引数の宣言は除く。
    /// </summary>
    internal static IReadOnlyList<string> FindRawNavigationParameterUses(string codeOnly)
    {
        var wrapperArguments = new List<(int Start, int End)>();
        var from = 0;
        while ((from = codeOnly.IndexOf(WrapperConstruction, from, StringComparison.Ordinal)) >= 0)
        {
            var open = from + WrapperConstruction.Length - 1;
            var depth = 0;
            for (var i = open; i < codeOnly.Length; i++)
            {
                if (codeOnly[i] == '(')
                {
                    depth++;
                }
                else if (codeOnly[i] == ')' && --depth == 0)
                {
                    wrapperArguments.Add((open, i));
                    break;
                }
            }

            from = open + 1;
        }

        var declarations = NavigationParameterDeclaration.Matches(codeOnly).Cast<Match>()
            .Select(m => m.Index + m.Length - "navigationService".Length)
            .ToHashSet();

        return NavigationParameterUse.Matches(codeOnly).Cast<Match>()
            .Where(m => !declarations.Contains(m.Index))
            .Where(m => !wrapperArguments.Any(a => a.Start < m.Index && m.Index < a.End))
            .Select(m => Excerpt(codeOnly, m.Index))
            .ToList();
    }

    /// <summary>
    /// <c>_navigationService</c> 以外の、ダイアログを出せるサービス型のフィールド・プロパティ・ローカルの名前を列挙する。
    /// </summary>
    internal static IReadOnlyList<string> FindOtherDialogServiceMembers(string codeOnly)
        => DialogServiceMemberDeclaration.Matches(codeOnly).Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(name => name != "_navigationService")
            .ToList();

    /// <summary>
    /// 抑制の <c>using</c> の外にある直接のモーダル表示を列挙する（検査の本体。下のサンプル入力で検査自体を固定する）。
    /// </summary>
    internal static IReadOnlyList<string> FindUnsuppressedDirectModals(string codeOnly)
    {
        var scopes = TestSourceInspection.ExtractUsingScopeBodies(codeOnly, "BeginModalCardReadingSuppression");
        var findings = new List<string>();

        foreach (Match match in DirectWindowShowDialog.Matches(codeOnly))
        {
            if (!scopes.Any(s => s.Start <= match.Index && match.Index < s.End))
            {
                findings.Add(Excerpt(codeOnly, match.Index));
            }
        }

        // MessageBox は包みの中（DialogService）で出すもの。画面から直接出す理由が無いので、抑制の内側でも認めない
        foreach (Match match in DirectMessageBox.Matches(codeOnly))
        {
            findings.Add(Excerpt(codeOnly, match.Index));
        }

        return findings;
    }

    private static string Excerpt(string source, int index)
    {
        var start = Math.Max(0, index - 60);
        return source.Substring(start, Math.Min(source.Length - start, 100)).Replace(Environment.NewLine, " ");
    }

    public static IEnumerable<object[]> ScreenSources()
    {
        yield return new object[] { "MainViewModel" };
        yield return new object[] { "HistoryPanelViewModel" };
    }

    private static IReadOnlyList<ProductionSourceFiles.SourceFile> FilesOf(string screen)
        => screen == "MainViewModel" ? MainViewModelSourceFiles.All : HistoryPanelViewModelSourceFiles.All;

    [Theory]
    [MemberData(nameof(ScreenSources))]
    public void 画面が保持するナビゲーションは抑制の包みであること(string screen)
    {
        var files = FilesOf(screen);
        files.Should().NotBeEmpty("前提: 構成ファイルが見つかる");

        var assignments = files
            .SelectMany(f => NavigationAssignment.Matches(f.CodeOnly).Cast<Match>())
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        assignments.Should().ContainSingle("_navigationService へ代入するのはコンストラクタの 1 か所だけ")
            .Which.Should().StartWith("new ModalCardReadingSuppressingNavigationService(",
                "受け取った INavigationService をそのまま保持すると、すべてのモーダルで抑制が漏れる");
    }

    [Theory]
    [MemberData(nameof(ScreenSources))]
    public void 包みを通らないモーダルは抑制のusingの内側にあること(string screen)
    {
        var findings = FilesOf(screen)
            .SelectMany(f => FindUnsuppressedDirectModals(f.CodeOnly).Select(e => $"{f.RelativePath}: {e}"))
            .ToList();

        findings.Should().BeEmpty(
            "INavigationService を通らないモーダルは、表示の範囲を BeginModalCardReadingSuppression の using で囲むこと");
    }

    /// <summary>
    /// 検査自体の固定（実データが違反ゼロでも空振りしていないことを、既知の入力で確かめる。#1786）。
    /// </summary>
    [Fact]
    public void 検査は抑制の外の直接表示を検出し内側は認めること()
    {
        const string violating = "void A() { var d = new X(); d.ShowDialog(); }";
        const string suppressed = "void A() { var d = new X(); using (Host.BeginModalCardReadingSuppression()) { r = d.ShowDialog(); } }";
        const string declared = "void A() { using var s = BeginModalCardReadingSuppression(); d.ShowDialog(); }";
        const string messageBox = "void A() { using (BeginModalCardReadingSuppression()) { OwnedMessageBox.Show(o, m, t, b, i); } }";
        const string errorHelper = "void A() { ErrorDialogHelper.ShowError(ex, t); }";
        const string navigation = "void A() { _navigationService.ShowDialog<X>(); _navigationService.ShowDialog(configure); _navigationService.ShowConfirmation(m, t); }";
        const string fileDialog = "void A() { var d = new SaveFileDialog(); if (d.ShowDialog(owner) == true) { } }";

        FindUnsuppressedDirectModals(violating).Should().HaveCount(1, "抑制の外の ShowDialog() は違反");
        FindUnsuppressedDirectModals(suppressed).Should().BeEmpty("抑制の using の内側は認める");
        FindUnsuppressedDirectModals(declared).Should().BeEmpty("using 宣言の範囲も内側");
        FindUnsuppressedDirectModals(messageBox).Should().HaveCount(1, "MessageBox の直呼びは抑制の内側でも認めない");
        FindUnsuppressedDirectModals(errorHelper).Should().HaveCount(1, "ErrorDialogHelper も MessageBox を出す");
        FindUnsuppressedDirectModals(navigation).Should().BeEmpty("包みを通る表示は対象外");
        FindUnsuppressedDirectModals(fileDialog).Should().HaveCount(1, "引数付きの ShowDialog(owner) も直接の表示");
    }

    [Theory]
    [MemberData(nameof(ScreenSources))]
    public void 受け取ったナビゲーションは包みの生成にしか使わないこと(string screen)
    {
        var findings = FilesOf(screen)
            .SelectMany(f => FindRawNavigationParameterUses(f.CodeOnly).Select(e => $"{f.RelativePath}: {e}"))
            .ToList();

        findings.Should().BeEmpty(
            "コンストラクタ引数 navigationService を包みの外で使うと、包みを通らずに表示できる経路が残る");
    }

    [Theory]
    [MemberData(nameof(ScreenSources))]
    public void ダイアログを出せるサービス型のメンバーは包んだナビゲーションだけであること(string screen)
    {
        var findings = FilesOf(screen)
            .SelectMany(f => FindOtherDialogServiceMembers(f.CodeOnly).Select(n => $"{f.RelativePath}: {n}"))
            .ToList();

        findings.Should().BeEmpty(
            "INavigationService / IDialogService 型を別に持つと、包みを通らずに表示できる（_navigationService だけを持つこと）");
    }

    /// <summary>
    /// 上の 2 つの検査の固定（既知の入力。#1786）。
    /// </summary>
    [Fact]
    public void 検査は受け取ったナビゲーションの包み以外の使い道と別のサービス型のメンバーを検出すること()
    {
        const string wrapped =
            "class C { private readonly INavigationService _navigationService; " +
            "C(INavigationService navigationService, IFoo foo) { _navigationService = new ModalCardReadingSuppressingNavigationService(" +
            "navigationService ?? throw new ArgumentNullException(nameof(navigationService)), () => Host.Begin()); } }";
        const string leaked =
            "class C { C(INavigationService navigationService) { _navigationService = new ModalCardReadingSuppressingNavigationService(" +
            "navigationService, Begin); _raw = navigationService; } }";
        const string otherField = "class C { private readonly IDialogService _dialogs; private readonly INavigationService _navigationService; }";
        const string otherProperty = "class C { public INavigationService Navigation { get; } }";
        const string otherLocal = "void A() { INavigationService raw = Resolve(); }";

        FindRawNavigationParameterUses(wrapped).Should().BeEmpty("宣言と包みの引数（nameof を含む）は認める");
        FindRawNavigationParameterUses(leaked).Should().HaveCount(1, "包みの外での使用は違反");
        FindOtherDialogServiceMembers(wrapped).Should().BeEmpty("_navigationService と引数の宣言は認める");
        FindOtherDialogServiceMembers(otherField).Should().Equal("_dialogs");
        FindOtherDialogServiceMembers(otherProperty).Should().Equal("Navigation");
        FindOtherDialogServiceMembers(otherLocal).Should().Equal("raw");
    }
}
