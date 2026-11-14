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
/// 抑制は、両画面が受け取った <c>INavigationService</c> を <c>ModalCardReadingSuppressingNavigationService</c> で、
/// <c>IStaffAuthService</c> を <c>ModalCardReadingSuppressingStaffAuthService</c> で包むことで掛けている。
/// 挙動は <c>MainViewModelTests</c> / <c>HistoryPanelViewModelTests</c> の「Issue #2484」region が
/// 代表のダイアログで表明するが、<b>包みを通らない表示</b>（<c>Window</c> を直接生成して <c>ShowDialog()</c>、
/// <c>MessageBox</c> の直呼び）を足されると、その表示だけ黙って抑制が漏れる。ここではその経路を列挙して塞ぐ。
/// </para>
/// <para>
/// 破れる経路: ①包みを外す（コンストラクタで受け取った値をそのまま保持する） ②<c>Window.ShowDialog()</c> の直呼び
/// ③<c>MessageBox.Show</c> / <c>OwnedMessageBox.Show</c> の直呼び ④<c>ErrorDialogHelper.Show*</c>（MessageBox を出す）。
/// ⑤コンストラクタ引数を包みの生成以外で使う（別のフィールド・ローカルへ渡して直接表示する）
/// ⑥包む対象の型のフィールド・プロパティを別に持つ（PR #2491 のレビューで ⑤⑥ を追加。3 回目で職員認証を対象に加えた）。
/// ②は <c>BeginModalCardReadingSuppression</c> の <c>using</c> の内側なら認める（<c>MergeHistoryDialog</c> が該当）。
/// </para>
/// <para>
/// <b>検査が見ない経路</b>（PR #2491 のレビュー 2 回目で明記）: ①ダイアログを出す型のメンバーは <c>INavigationService</c> /
/// <c>IDialogService</c>（とその実装）と <c>IStaffAuthService</c> だけを見る。それ以外のサービスの中でモーダルを出す形、
/// <c>Window</c> 型のフィールドを持って後で表示する形は拾わない（<c>.ShowDialog(</c> の直呼びは②で拾う）。
/// ②<c>App.Current.ServiceProvider</c> などの DI からその場でサービスを取り出して表示する形は拾わない
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

    /// <summary>画面が包んで保持するサービス 1 種類の名前の組</summary>
    public sealed class WrappedService
    {
        public WrappedService(string label, string fieldName, string parameterName, string interfaceName, string memberTypePattern, string wrapperType)
        {
            Label = label;
            FieldName = fieldName;
            ParameterName = parameterName;
            InterfaceName = interfaceName;
            WrapperType = wrapperType;
            // `==` は代入ではないので拾わない（PR #2491 のレビュー 3 回目）
            Assignment = new Regex(@"\b" + Regex.Escape(fieldName) + @"\s*=(?!=)\s*([^;]+);", RegexOptions.Compiled);
            ParameterUse = new Regex(@"\b" + Regex.Escape(parameterName) + @"\b", RegexOptions.Compiled);
            ParameterDeclaration = new Regex(
                @"\b" + Regex.Escape(interfaceName) + @"\??\s+" + Regex.Escape(parameterName) + @"\b", RegexOptions.Compiled);
            // 引数の宣言（後ろが `,` / `)`）は拾わない
            MemberDeclaration = new Regex(@"\b(?:" + memberTypePattern + @")\??\s+(\w+)\s*(?:;|=|\{)", RegexOptions.Compiled);
        }

        public string Label { get; }
        public string FieldName { get; }
        public string ParameterName { get; }
        public string InterfaceName { get; }
        public string WrapperType { get; }
        public Regex Assignment { get; }
        public Regex ParameterUse { get; }
        public Regex ParameterDeclaration { get; }
        public Regex MemberDeclaration { get; }

        public string WrapperConstruction => "new " + WrapperType + "(";

        public override string ToString() => Label;
    }

    internal static readonly WrappedService Navigation = new(
        "ナビゲーション", "_navigationService", "navigationService", "INavigationService",
        "I?NavigationService|I?DialogService", "ModalCardReadingSuppressingNavigationService");

    internal static readonly WrappedService StaffAuth = new(
        "職員認証", "_staffAuthService", "staffAuthService", "IStaffAuthService",
        "I?StaffAuthService", "ModalCardReadingSuppressingStaffAuthService");

    /// <summary><paramref name="service"/> のフィールドへの代入の右辺を列挙する。</summary>
    internal static IReadOnlyList<string> FindAssignments(string codeOnly, WrappedService service)
        => service.Assignment.Matches(codeOnly).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();

    /// <summary>
    /// コンストラクタ引数を、包みの生成（<c>new 包みの型(...)</c> の引数）の外で使っている箇所を列挙する。引数の宣言は除く。
    /// </summary>
    internal static IReadOnlyList<string> FindRawParameterUses(string codeOnly, WrappedService service)
    {
        var wrapperArguments = new List<(int Start, int End)>();
        var construction = service.WrapperConstruction;
        var from = 0;
        while ((from = codeOnly.IndexOf(construction, from, StringComparison.Ordinal)) >= 0)
        {
            var open = from + construction.Length - 1;
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

        var declarations = service.ParameterDeclaration.Matches(codeOnly).Cast<Match>()
            .Select(m => m.Index + m.Length - service.ParameterName.Length)
            .ToHashSet();

        return service.ParameterUse.Matches(codeOnly).Cast<Match>()
            .Where(m => !declarations.Contains(m.Index))
            .Where(m => !wrapperArguments.Any(a => a.Start < m.Index && m.Index < a.End))
            .Select(m => Excerpt(codeOnly, m.Index))
            .ToList();
    }

    /// <summary>
    /// 包んだフィールド以外の、<paramref name="service"/> の型のフィールド・プロパティ・ローカルの名前を列挙する。
    /// </summary>
    internal static IReadOnlyList<string> FindOtherMembers(string codeOnly, WrappedService service)
        => service.MemberDeclaration.Matches(codeOnly).Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Where(name => name != service.FieldName)
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

    public static IEnumerable<object[]> ScreenServices()
    {
        foreach (var screen in new[] { "MainViewModel", "HistoryPanelViewModel" })
        {
            yield return new object[] { screen, nameof(Navigation) };
            yield return new object[] { screen, nameof(StaffAuth) };
        }
    }

    private static WrappedService ServiceOf(string name) => name == nameof(Navigation) ? Navigation : StaffAuth;

    private static IReadOnlyList<ProductionSourceFiles.SourceFile> FilesOf(string screen)
        => screen == "MainViewModel" ? MainViewModelSourceFiles.All : HistoryPanelViewModelSourceFiles.All;

    /// <summary>
    /// 画面が保持するサービスは包みであること。ナビゲーションは両画面が必ず持つ。職員認証は持つ画面（履歴パネル）だけ見る
    /// （持たない画面に代入が無いのは正しい）。
    /// </summary>
    [Theory]
    [MemberData(nameof(ScreenServices))]
    public void 画面が保持するサービスは抑制の包みであること(string screen, string serviceName)
    {
        var service = ServiceOf(serviceName);
        var files = FilesOf(screen);
        files.Should().NotBeEmpty("前提: 構成ファイルが見つかる");

        var assignments = files.SelectMany(f => FindAssignments(f.CodeOnly, service)).ToList();
        var holdsField = files.Any(f => Regex.IsMatch(
            f.CodeOnly, @"\b" + Regex.Escape(service.InterfaceName) + @"\??\s+" + Regex.Escape(service.FieldName) + @"\s*;"));

        if (service == Navigation || holdsField)
        {
            assignments.Should().ContainSingle($"{service.FieldName} へ代入するのはコンストラクタの 1 か所だけ")
                .Which.Should().StartWith(service.WrapperConstruction,
                    $"受け取った {service.InterfaceName} をそのまま保持すると、その表示で抑制が漏れる");
        }
        else
        {
            assignments.Should().BeEmpty($"{service.FieldName} を持たない画面に代入は無い");
        }
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
    [MemberData(nameof(ScreenServices))]
    public void 受け取ったサービスは包みの生成にしか使わないこと(string screen, string serviceName)
    {
        var service = ServiceOf(serviceName);
        var findings = FilesOf(screen)
            .SelectMany(f => FindRawParameterUses(f.CodeOnly, service).Select(e => $"{f.RelativePath}: {e}"))
            .ToList();

        findings.Should().BeEmpty(
            $"コンストラクタ引数 {service.ParameterName} を包みの外で使うと、包みを通らずに表示できる経路が残る");
    }

    [Theory]
    [MemberData(nameof(ScreenServices))]
    public void ダイアログを出せるサービス型のメンバーは包んだフィールドだけであること(string screen, string serviceName)
    {
        var service = ServiceOf(serviceName);
        var findings = FilesOf(screen)
            .SelectMany(f => FindOtherMembers(f.CodeOnly, service).Select(n => $"{f.RelativePath}: {n}"))
            .ToList();

        findings.Should().BeEmpty(
            $"{service.InterfaceName} の型を別に持つと、包みを通らずに表示できる（{service.FieldName} だけを持つこと）");
    }

    /// <summary>
    /// 代入・引数の使い道・メンバーの検査の固定（既知の入力。#1786）。
    /// </summary>
    [Fact]
    public void 検査は受け取ったサービスの包み以外の使い道と別のサービス型のメンバーを検出すること()
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
        const string comparison = "void A() { if (_navigationService == null) { } _navigationService = new ModalCardReadingSuppressingNavigationService(n, B); }";
        const string rawAuth =
            "class C { private readonly IStaffAuthService _staffAuthService; C(IStaffAuthService staffAuthService) { _staffAuthService = staffAuthService; } }";
        const string wrappedAuth =
            "class C { private readonly IStaffAuthService _staffAuthService; C(IStaffAuthService staffAuthService) { " +
            "_staffAuthService = new ModalCardReadingSuppressingStaffAuthService(staffAuthService, () => Host.Begin()); } }";

        FindRawParameterUses(wrapped, Navigation).Should().BeEmpty("宣言と包みの引数（nameof を含む）は認める");
        FindRawParameterUses(leaked, Navigation).Should().HaveCount(1, "包みの外での使用は違反");
        FindOtherMembers(wrapped, Navigation).Should().BeEmpty("_navigationService と引数の宣言は認める");
        FindOtherMembers(otherField, Navigation).Should().Equal("_dialogs");
        FindOtherMembers(otherProperty, Navigation).Should().Equal("Navigation");
        FindOtherMembers(otherLocal, Navigation).Should().Equal("raw");
        FindAssignments(comparison, Navigation).Should().Equal(
            new[] { "new ModalCardReadingSuppressingNavigationService(n, B)" }, "`==` の比較は代入として拾わない");
        FindAssignments(rawAuth, StaffAuth).Should().Equal(new[] { "staffAuthService" }, "職員認証の代入も拾う");
        FindRawParameterUses(rawAuth, StaffAuth).Should().HaveCount(1, "包まずに保持するのは違反");
        FindRawParameterUses(wrappedAuth, StaffAuth).Should().BeEmpty("包みの引数は認める");
    }
}
