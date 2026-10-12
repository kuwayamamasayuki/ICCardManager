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

    /// <summary>操作者セッションを開始してよい唯一のファイル（認証結果の <c>Enter()</c>）。</summary>
    private const string SessionOwnerFileName = "StaffAuthResult.cs";

    [Fact]
    public void 職員認証の結果はその場でEnterしてusingで受けること()
    {
        var violations = EnumerateProductionSources()
            .Select(f => (File: f, Code: ReadCode(f)))
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
    /// 対の表明: 検査が実際の呼び出しを拾っていること（抽出が 0 件に縮んでも上の検査は緑になるため）。
    /// </summary>
    [Fact]
    public void 検査は本番コードの認証の呼び出しとセッションの開始を拾っていること()
    {
        var sources = EnumerateProductionSources().ToList();

        var historyEdit = sources.Single(f => Path.GetFileName(f) == "HistoryPanelViewModel.Edit.cs");
        ScopedAuthCallPattern.Matches(ReadCode(historyEdit)).Count.Should().Be(3,
            "履歴の追加・削除・変更の 3 つの認証が決まった形として拾えていること");

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
