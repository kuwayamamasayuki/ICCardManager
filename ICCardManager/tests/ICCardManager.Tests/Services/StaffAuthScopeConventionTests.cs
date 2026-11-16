using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2260: 職員認証は「認証した 1 操作」に限る。その構造が崩れる経路を静的に固定する。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装は認証の成功時に操作者セッションを開始するだけで、終える呼び出し元が本番コードに 1 つも無かった。
/// セッションは 5 分間残り、その間に別の職員が行った<b>認証の無い操作</b>（カードの編集・払い戻し・
/// 明細の保存など）が、直前に認証した職員の名前で 6 年保存の操作ログに記録されていた。
/// </para>
/// <para>
/// 是正後はセッションを認証した画面の非同期の流れ（<c>AsyncLocal</c>）に結び付け、開始を認証結果の
/// <c>Enter()</c>、終了を認証結果の破棄に置いた。この性質を破れる経路は次の 3 つで、それぞれを検査する。
/// (1) 認証結果を <c>using</c> で受けない呼び出し（<c>var r = await …</c>・戻り値の破棄）— 破棄されずセッションが残る。
/// (2) <c>Enter()</c> を呼び出しの式の中で呼ばない形 — セッションが開始されず認証した操作が「GUI操作」になる。
///     また <c>async</c> のヘルパーの内側で開始すると、ヘルパーから戻った画面の流れには残らない。
/// (3) 認証結果の外から操作者セッションを開始する呼び出し（<c>BeginSession</c> の直接呼び出し）— 終える責任者がいない。
/// 走査対象は本番ソース全体から導出する（ファイル名で列挙すると、認証を要求する画面が増えたときに静かに漏れる）。
/// </para>
/// </remarks>
public class StaffAuthScopeConventionTests
{
    /// <summary>認証の呼び出し（宣言 <c>RequestAuthenticationAsync(string …)</c> はドットを伴わないので一致しない）。</summary>
    private static readonly Regex AuthCallPattern = new Regex(@"\.RequestAuthenticationAsync\(");

    /// <summary>
    /// 認証結果を、その場で <c>Enter()</c> してから <c>using</c> 宣言・<c>using</c> 文で受ける呼び出し。
    /// </summary>
    private static readonly Regex ScopedAuthCallPattern = new Regex(
        @"using\s*(?:\(\s*)?var\s+\w+\s*=\s*\(\s*await\s+[\w.]+\.RequestAuthenticationAsync\(\s*""[^""]*""\s*\)\s*\)\s*\?\.\s*Enter\(\s*\)");

    /// <summary>操作者セッションの開始。</summary>
    private static readonly Regex BeginSessionCallPattern = new Regex(@"\.BeginSession\(");

    /// <summary>認証結果の <c>Enter()</c> の呼び出し（宣言 <c>public StaffAuthResult Enter()</c> はドットを伴わないので一致しない）。</summary>
    private static readonly Regex EnterCallPattern = new Regex(@"\.\s*Enter\(\s*\)");

    /// <summary>
    /// Issue #2494: 保持している認証から作り直した結果を、その場で <c>Enter()</c> してから <c>using</c> で受ける呼び出し
    /// （<c>using var r = (await TakeHeldAuthenticationAsync())?.Enter();</c>）。
    /// </summary>
    private static readonly Regex ScopedHeldAuthCallPattern = new Regex(
        @"using\s*(?:\(\s*)?var\s+\w+\s*=\s*\(\s*await\s+TakeHeldAuthenticationAsync\(\s*\)\s*\)\s*\?\.\s*Enter\(\s*\)");

    /// <summary>
    /// 操作者セッションを伴う認証結果を作る呼び出し（Issue #2494 のレビュー 1 回目）。作ってよいファイルを固定する:
    /// 認証の内部コンストラクタ <c>new StaffAuthResult(…)</c>（引数あり）・<c>CreateAuthenticatedResult</c>・
    /// 保持の取り出し <c>TakeHeldAuthenticationAsync</c>。引数の無い <c>new StaffAuthResult()</c> / 初期化子はセッションを
    /// 伴わないので対象外。
    /// </summary>
    private static readonly (Regex Pattern, string Description, string[] AllowedRelativePaths)[] SessionResultFactories =
    {
        (new Regex(@"\bnew\s+StaffAuthResult\s*\(\s*[^)\s]"), "new StaffAuthResult(…)（引数あり）",
            new[] { Path.Combine("Services", "StaffAuthResult.cs"), Path.Combine("Services", "StaffAuthService.cs") }),
        (new Regex(@"\bCreateAuthenticatedResult\s*\("), "CreateAuthenticatedResult",
            new[] { Path.Combine("Services", "StaffAuthService.cs") }),
        (new Regex(@"\bTakeHeldAuthenticationAsync\s*\("), "TakeHeldAuthenticationAsync",
            new[] { Path.Combine("ViewModels", "StaffManageViewModel.AuthenticationHold.cs") }),
        (new Regex(@"\.?\bReissueForNextOperation\s*\("), "ReissueForNextOperation",
            new[] { Path.Combine("Services", "StaffAuthResult.cs"), Path.Combine("ViewModels", "StaffManageViewModel.AuthenticationHold.cs") }),
    };

    /// <summary>保持している認証から次の 1 操作の結果を作る呼び出し（Issue #2494）。</summary>
    private static readonly Regex ReissueCallPattern = new Regex(@"\.ReissueForNextOperation\(");

    /// <summary>保持している認証から結果を作ってよい唯一のファイル（職員管理画面の認証の保持。Issue #2494）。</summary>
    private static readonly string ReissueOwnerRelativePath = Path.Combine("ViewModels", "StaffManageViewModel.AuthenticationHold.cs");

    /// <summary>操作者セッションを開始してよい唯一のファイル（認証結果の <c>Enter()</c>）。</summary>
    private const string SessionOwnerFileName = "StaffAuthResult.cs";

    [Fact]
    public void 職員認証の結果はその場でEnterしてusingで受けること()
    {
        var violations = EnumerateProductionSources()
            .Select(f => (File: f, Code: ReadCode(f)))
            // Issue #2484: 職員認証の包み（委譲）の 1 ファイルだけを除く（中身が委譲だけであることは下の検査が表明する）
            .Where(x => !StaffAuthDelegationWrapper.IsWrapperFile(x.File))
            .Select(x => (x.File, Calls: AuthCallPattern.Matches(x.Code).Count, Scoped: ScopedAuthCallPattern.Matches(x.Code).Count))
            .Where(x => x.Calls != x.Scoped)
            .Select(x => $"  - {RelativePath(x.File)}: 呼び出し {x.Calls} 件のうち決まった形のもの {x.Scoped} 件")
            .ToList();

        violations.Should().BeEmpty(
            "認証結果を using で受けないと、操作が終わっても操作者セッションが残る。Enter() を呼ばないと" +
            "セッションが開始されず、認証した操作が「GUI操作」として記録される（Issue #2260）。" +
            "`using var authResult = (await _staffAuthService.RequestAuthenticationAsync(\"…\"))?.Enter();` の形で書くこと。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void 操作者セッションは認証結果の外から開始しないこと()
    {
        var violations = EnumerateProductionSources()
            .Where(f => !string.Equals(Path.GetFileName(f), SessionOwnerFileName, StringComparison.Ordinal))
            .Where(f => BeginSessionCallPattern.IsMatch(ReadCode(f)))
            .Select(f => $"  - {RelativePath(f)}")
            .ToList();

        violations.Should().BeEmpty(
            "操作者セッションは認証結果の Enter() だけが開始し、その終了を認証結果の破棄へ結び付けている（Issue #2260）。" +
            "ほかの場所から開始すると、終える責任者のいないセッションが残る。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Issue #2494: 認証結果の <c>Enter()</c> は、どこから得た結果でもその場で <c>using</c> で受けること。
    /// </summary>
    /// <remarks>
    /// 職員管理画面は認証した職員を画面が閉じるまで保持し、次の操作の結果を作り直して <c>Enter()</c> する。
    /// 認証の呼び出し（<c>RequestAuthenticationAsync</c>）を経ない <c>Enter()</c> は上の検査に数えられないため、
    /// <c>Enter()</c> の呼び出しそのものを数えて、すべてが決まった形（認証の呼び出し・保持の取り出し）であることを表明する。
    /// using で受けないと、操作と操作の間に操作者セッションが残り、この画面の外の認証の無い操作が拾う（Issue #2260）。
    /// </remarks>
    [Fact]
    public void 認証結果のEnterはすべてその場でusingで受けること()
    {
        var violations = EnumerateProductionSources()
            .Select(f => (File: f, Code: ReadCode(f)))
            .Select(x => (x.File,
                Enters: EnterCallPattern.Matches(x.Code).Count,
                Scoped: ScopedAuthCallPattern.Matches(x.Code).Count + ScopedHeldAuthCallPattern.Matches(x.Code).Count))
            .Where(x => x.Enters != x.Scoped)
            .Select(x => $"  - {RelativePath(x.File)}: Enter() {x.Enters} 件のうち決まった形のもの {x.Scoped} 件")
            .ToList();

        violations.Should().BeEmpty(
            "認証結果の Enter() は、その場で using で受けること（操作が終わったら操作者セッションを閉じる。Issue #2260 / #2494）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Issue #2494: 保持している認証から結果を作るのは、職員管理画面の認証の保持の 1 ファイルだけであること。
    /// </summary>
    [Fact]
    public void 保持している認証から結果を作るのは職員管理画面の保持だけであること()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var owner = Path.GetFullPath(Path.Combine(root, ReissueOwnerRelativePath));
        var callers = EnumerateProductionSources()
            .Where(f => ReissueCallPattern.IsMatch(ReadCode(f)))
            .Select(Path.GetFullPath)
            .ToList();

        callers.Should().Equal(new[] { owner },
            "認証した職員の保持は、削除・差し替えを除く職員管理画面の登録・変更・復元に限るとユーザーが決めた（Issue #2494）。" +
            "ほかの画面で保持すると、認証した職員と別の職員の操作の取り違え（Issue #2260）が画面の外へ広がる");

        var ownerCode = ReadCode(owner);
        ScopedHeldAuthCallPattern.Matches(ownerCode).Count.Should().Be(1,
            "保持の取り出しが決まった形として拾えていること（拾えなければ上の Enter() の検査が空振りする）");
        EnterCallPattern.Matches(ownerCode).Count.Should().Be(1);
    }

    /// <summary>
    /// Issue #2494（レビュー 1 回目）: セッションを伴う認証結果を作る経路（内部コンストラクタ・<c>CreateAuthenticatedResult</c>・
    /// 保持の取り出し・作り直し）は、決まったファイルからしか使わないこと。
    /// </summary>
    /// <remarks>
    /// <c>Enter()</c> の検査は「using で受けているか」しか見ないので、画面のファイルで認証結果を作り直して using で受ければ、
    /// 認証の要求を経ずに操作者を名乗れる。作る経路そのものを置き場所で縛る。
    /// </remarks>
    [Fact]
    public void セッションを伴う認証結果を作る経路は決まったファイルだけが使うこと()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var sources = EnumerateProductionSources().Select(f => (File: f, Code: ReadCode(f))).ToList();

        var violations = new List<string>();
        foreach (var (pattern, description, allowed) in SessionResultFactories)
        {
            var allowedFull = allowed.Select(a => Path.GetFullPath(Path.Combine(root, a))).ToList();
            violations.AddRange(sources
                .Where(x => pattern.IsMatch(x.Code))
                .Where(x => !allowedFull.Contains(Path.GetFullPath(x.File), StringComparer.OrdinalIgnoreCase))
                .Select(x => $"  - {RelativePath(x.File)}: {description}"));
        }

        violations.Should().BeEmpty(
            "操作者セッションを伴う認証結果は、認証サービス（認証の要求）と職員管理画面の保持だけが作る（Issue #2260 / #2494）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// 対の表明: 作る経路の検査が実際の置き場所を拾い、迂回の形（画面のファイルでの作り直し）を違反と数えること。
    /// </summary>
    [Fact]
    public void 作る経路の検査は実際の置き場所を拾い迂回の形を数えること()
    {
        var root = TestPaths.GetProductionSourceRoot();
        foreach (var (pattern, description, allowed) in SessionResultFactories)
        {
            allowed.Any(a => pattern.IsMatch(ReadCode(Path.Combine(root, a)))).Should().BeTrue(
                $"{description} を許可したファイルで実際に拾えていること（拾えなければ検査が空振りする）");
        }

        const string bypass =
            "var r = StaffAuthService.CreateAuthenticatedResult(_context, idm, name);\n" +
            "var s = new StaffAuthResult(idm, name, _context);\n" +
            "var t = await TakeHeldAuthenticationAsync();\n" +
            "var u = _held.ReissueForNextOperation(name);\n" +
            "var ok = new StaffAuthResult();\n" +
            "var ok2 = new StaffAuthResult { Idm = idm };\n";
        SessionResultFactories.Count(f => f.Pattern.IsMatch(bypass)).Should().Be(4, "4 つの経路をそれぞれ拾う");
        SessionResultFactories.Sum(f => f.Pattern.Matches(bypass).Count).Should().Be(4,
            "引数の無いコンストラクタ・初期化子はセッションを伴わないので数えない");
    }

    /// <summary>
    /// Issue #2494: Enter() の検査を既知のサンプル入力で固定する。
    /// </summary>
    [Fact]
    public void Enterの検査はusingで受けない形を違反として数えること()
    {
        const string sample =
            "using var a = (await TakeHeldAuthenticationAsync())?.Enter();\n" +
            "using var e = (await AnyOtherAsync())?.Enter();\n" +
            "using var b = (await _staffAuthService.RequestAuthenticationAsync(\"職員の登録\"))?.Enter();\n" +
            "var c = (await TakeHeldAuthenticationAsync())?.Enter();\n" +
            "using var d = _held.ReissueForNextOperation(name).Enter();\n" +
            "public StaffAuthResult Enter()\n";

        EnterCallPattern.Matches(sample).Count.Should().Be(5, "宣言は呼び出しとして数えない");
        (ScopedAuthCallPattern.Matches(sample).Count + ScopedHeldAuthCallPattern.Matches(sample).Count).Should().Be(2,
            "using で受けない形・await の結果でない形・保持の取り出し以外の引数無しの await は決まった形として数えない");
    }

    /// <summary>
    /// 対の表明: 検査が実際の呼び出しを拾っていること（抽出が 0 件に縮んでも上の検査は緑になるため）。
    /// </summary>
    [Fact]
    public void 検査は本番コードの認証の呼び出しとセッションの開始を拾っていること()
    {
        var sources = EnumerateProductionSources().ToList();

        var historyEdit = sources.Single(f => Path.GetFileName(f) == "HistoryPanelViewModel.Edit.cs");
        ScopedAuthCallPattern.Matches(ReadCode(historyEdit)).Count.Should().Be(2,
            "履歴の追加・変更の 2 つの認証が決まった形として拾えていること" +
            "（Issue #2475 で UI から到達しない「履歴の削除」の認証をコマンドごと削除した）");

        var owner = sources.Single(f => Path.GetFileName(f) == SessionOwnerFileName);
        BeginSessionCallPattern.IsMatch(ReadCode(owner)).Should().BeTrue(
            "認証結果がセッションを開始する呼び出しを拾えていること");
    }

    /// <summary>
    /// 検査ロジックを既知のサンプル入力で固定する（実データの状態に関わらず空振りを検出できるように）。
    /// </summary>
    [Fact]
    public void 検査は決まった形でない呼び出しを違反として数えること()
    {
        const string sample =
            "using var a = (await _staffAuthService.RequestAuthenticationAsync(\"履歴の削除\"))?.Enter();\n" +
            "using (var b = (await _staffAuthService.RequestAuthenticationAsync(\"履歴の変更\"))?.Enter())\n{\n}\n" +
            "using var c = await _staffAuthService.RequestAuthenticationAsync(\"職員の削除\");\n" +
            "var d = (await _staffAuthService.RequestAuthenticationAsync(\"履歴の追加\"))?.Enter();\n" +
            "await _staffAuthService.RequestAuthenticationAsync(\"履歴の統合\");\n" +
            "_ = await _staffAuthService.RequestAuthenticationAsync(\"履歴の分割\");\n" +
            "Task<StaffAuthResult?> RequestAuthenticationAsync(string operationDescription);\n";

        AuthCallPattern.Matches(sample).Count.Should().Be(6, "宣言は呼び出しとして数えない");
        ScopedAuthCallPattern.Matches(sample).Count.Should().Be(2,
            "Enter() してから using 宣言・using 文で受ける形だけを数える（Enter の無い using・using の無い Enter は違反）");
    }

    /// <summary>
    /// コメントアウトした呼び出しは数えない（規約の説明に書いた例が違反として検出される極性の反転を避ける）。
    /// </summary>
    [Fact]
    public void 検査はコメント中の呼び出しを数えないこと()
    {
        const string sample =
            "// var r = await _staffAuthService.RequestAuthenticationAsync(\"職員の削除\");\n" +
            "/* _operatorContext.BeginSession(idm, name); */\n" +
            "using var authResult = (await _staffAuthService.RequestAuthenticationAsync(\"履歴の分割\"))?.Enter();\n";

        var code = TestSourceInspection.RemoveCommentsPreservingLines(sample);

        AuthCallPattern.Matches(code).Count.Should().Be(1);
        ScopedAuthCallPattern.Matches(code).Count.Should().Be(1);
        BeginSessionCallPattern.IsMatch(code).Should().BeFalse();
    }

    /// <summary>
    /// 上の検査から除いた職員認証の包み（Issue #2484）は委譲だけであること: 呼び出しはちょうど 1 件で、受け取った操作名を
    /// そのまま渡す（認証結果を受けて使う呼び出しを足すと、この検査が赤になる）。
    /// </summary>
    [Fact]
    public void 除外した職員認証の包みは受け取った操作名をそのまま1回だけ渡すこと()
    {
        var wrapper = EnumerateProductionSources().Where(StaffAuthDelegationWrapper.IsWrapperFile).ToList();

        wrapper.Should().ContainSingle("除外するファイルが存在すること（名前を変えたら除外の定義も直す）");
        StaffAuthDelegationWrapper.FindDelegationViolations(ReadCode(wrapper[0])).Should().BeEmpty();
    }

    /// <summary>
    /// 包みの検査と除外の範囲を既知の入力で固定する（PR #2491 のレビュー 4 回目）。
    /// </summary>
    [Fact]
    public void 包みの検査は委譲以外の呼び出しを検出し除外は1ファイルだけであること()
    {
        const string delegation =
            "public async Task<StaffAuthResult?> RequestAuthenticationAsync(string operationDescription)\n" +
            "{ using (_beginSuppression()) { return await _inner.RequestAuthenticationAsync(operationDescription); } }";
        const string extraCall = delegation +
            "\nasync Task A() { var r = await _inner.RequestAuthenticationAsync(\"履歴の統合\"); }";
        const string replacedName =
            "public async Task<StaffAuthResult?> RequestAuthenticationAsync(string operationDescription)\n" +
            "{ return await _inner.RequestAuthenticationAsync(\"固定の操作名\"); }";

        StaffAuthDelegationWrapper.FindDelegationViolations(delegation).Should().BeEmpty("委譲だけなら認める");
        StaffAuthDelegationWrapper.FindDelegationViolations(extraCall).Should().NotBeEmpty("委譲以外の呼び出しを足したら違反");
        StaffAuthDelegationWrapper.FindDelegationViolations(replacedName).Should().NotBeEmpty("受け取った操作名を渡さないのは違反");

        var root = TestPaths.GetProductionSourceRoot();
        StaffAuthDelegationWrapper.IsWrapperFile(Path.Combine(root, StaffAuthDelegationWrapper.RelativePath)).Should().BeTrue();
        StaffAuthDelegationWrapper.IsWrapperFile(Path.Combine(root, "ViewModels", "CardManageViewModel.cs")).Should().BeFalse(
            "入れ子の IStaffAuthService の実装を置いた画面のファイルは除外しない（形ではなくファイルで除く）");
        StaffAuthDelegationWrapper.IsWrapperFile(Path.Combine(root, "Services", "ModalCardReadingSuppressingStaffAuthService.cs"))
            .Should().BeFalse("同じ名前の別の場所のファイルは除外しない");
    }

    private static string ReadCode(string path)
        => TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));

    private static IEnumerable<string> EnumerateProductionSources()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var separator = Path.DirectorySeparatorChar;
        return Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private static string RelativePath(string path)
        => path.Substring(TestPaths.GetProductionSourceRoot().Length).TrimStart(Path.DirectorySeparatorChar);
}
