using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 「BeginBusy スコープの内側でモーダルダイアログを表示するときは SuspendBusy で囲む」
/// という規約をソーステキスト上の静的検査で固定する（Issue #1793）。
/// </summary>
/// <remarks>
/// <para>
/// <c>IDialogService</c> の実装は同期モーダル（<c>MessageBox.Show</c>）で、職員が閉じるまで
/// 呼び出しスレッドをブロックする。そのため <c>BusyScope.Dispose()</c> が走らず
/// <c>IsBusy=true</c> のまま残り、各画面の全面オーバーレイと不確定 <c>ProgressBar</c> が
/// モーダル表示の背後で回り続ける。確認ダイアログでは、職員は「処理が続いているのか」を
/// 判断できない状態で決定を迫られる。
/// </para>
/// <para>
/// <b>本検査は「呼び出しが構文上そのスコープの内側にある」ものしか見えない。</b>
/// ダイアログ表示をヘルパーメソッドへ切り出すと、ヘルパー側の本体には <c>BeginBusy</c> が
/// 無いため検査を素通りする（実際 <c>CardManageViewModel</c> の
/// <c>ShowRegistrationModeDialog</c> / <c>NotifyDeleteConflictAsync</c> がこの形）。
/// ヘルパー経由の経路は <c>BusyScopeDialogBehaviorTests</c> の挙動テストが
/// 「ダイアログ呼び出し時点の <c>IsBusy</c>」を捕捉して守る。<b>静的検査と挙動テストは
/// どちらか一方では不十分で、対で置くこと。</b>
/// </para>
/// <para>
/// 走査対象は<b>ファイル名で列挙しない</b>（同じ形を持つ画面が追加されたときに静かに漏れる。
/// <c>.claude/rules/development-conventions.md</c> #1786「ガードを書くときは経路を列挙する」）。
/// <c>ViewModels</c> 配下で <c>BeginBusy</c> を使うファイルすべてを対象に導出する。
/// </para>
/// </remarks>
public class BusyScopeDialogConventionTests
{
    /// <summary>
    /// モーダル表示とみなす呼び出し。<c>MessageBox.Show</c> の直呼びも対象に含める
    /// （Issue #1793 で <c>SystemManageViewModel</c> の一部を <c>IDialogService</c> へ移したが、
    /// 直呼びが再び持ち込まれても検出できるようにする）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>モーダルは <c>IDialogService</c> 経由だけではない。</b><c>INavigationService.ShowDialog&lt;T&gt;()</c> は
    /// <c>Window.ShowDialog()</c> を呼ぶ同期モーダルで、<c>OpenFileDialog</c> / <c>SaveFileDialog</c> /
    /// <c>FolderBrowserDialog</c> の <c>ShowDialog()</c> も同じ。<c>_dialogService.Show*</c> だけを見ると
    /// <c>ReportViewModel</c> の印刷プレビュー（<c>_navigationService.ShowDialog&lt;PrintPreviewDialog&gt;</c>）が
    /// <b>スコープ内にあるのに 1 件も検出されない</b>ため、<c>X.ShowDialog[Async]&lt;T&gt;(</c> 形も対象にする。
    /// </para>
    /// <para>
    /// <b><c>_navigationService.Show*</c> も対象に含める（Issue #1837）。</b>
    /// <c>INavigationService</c> は <c>IDialogService</c> を継承しており、
    /// <c>ShowInformation</c> / <c>ShowWarning</c> / <c>ShowError</c> / <c>ShowConfirmation</c> /
    /// <c>ShowWarningConfirmation</c> / <c>ShowThreeWayConfirmation</c> はいずれも同期モーダルである。
    /// #1837 は <c>MessageBox.Show</c> の直呼び（本パターンで検出できていた形）を
    /// <c>_navigationService.Show*</c> へ移したため、<c>_dialogService.Show*</c> だけを見ると
    /// <b>是正済みの <c>ReportViewModel</c> の 2 か所（<c>SuspendBusy</c> で囲んだテンプレートエラー・
    /// 帳票作成エラー）が検査対象から静かに落ち</b>、<c>SuspendBusy</c> を外す退行を検出できなくなる。
    /// </para>
    /// <para>
    /// フィールド名で限定するのは、<c>_toastNotificationService.ShowError</c> のような
    /// <b>非モーダル</b>の通知を誤検出しないため（誤検出はガード自体の寿命を縮める）。
    /// </para>
    /// </remarks>
    private static readonly Regex ModalCallPattern = new Regex(
        @"((?:_dialogService|_navigationService)\.Show\w+|MessageBox\.Show|\w+\.ShowDialog\w*)\s*(?:<[^<>()]*>\s*)?\(");

    /// <summary>
    /// 処理中スコープの範囲を列挙する。
    /// </summary>
    /// <remarks>
    /// <c>BeginBusy</c> と <c>BeginCancellableBusy</c> は別名のため、<b>両方を対象にする</b>。
    /// 片方だけを見ると、キャンセル可能スコープの内側に持ち込まれたモーダルを見逃す。
    /// </remarks>
    private static IReadOnlyList<(int Start, int End)> ExtractBusyScopes(string code)
        => TestSourceInspection.ExtractUsingScopeBodies(code, "BeginBusy")
            .Concat(TestSourceInspection.ExtractUsingScopeBodies(code, "BeginCancellableBusy"))
            .ToList();

    private static IReadOnlyList<string> GetViewModelFiles()
        => Directory.GetFiles(
            Path.Combine(TestPaths.GetProductionSourceRoot(), "ViewModels"),
            "*.cs",
            SearchOption.AllDirectories);

    /// <summary>
    /// 検査対象（BeginBusy スコープを持つ ViewModel）を導出する
    /// </summary>
    private static IReadOnlyList<(string Path, string Code)> GetFilesWithBusyScopes()
    {
        var result = new List<(string, string)>();

        foreach (var path in GetViewModelFiles())
        {
            var code = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(path));
            if (ExtractBusyScopes(code).Count > 0)
            {
                result.Add((path, code));
            }
        }

        return result;
    }

    private static int ToLineNumber(string code, int index)
        => code.Take(index).Count(c => c == '\n') + 1;

    /// <summary>
    /// 「変数へ代入される／<c>return</c> される」ラムダの直前形。末尾が <c>=&gt;</c> であること。
    /// </summary>
    /// <remarks>
    /// <b>「ラムダの中にある」だけでは遅延実行の根拠にならない。</b>
    /// <c>Dispatcher.InvokeAsync(async () =&gt; { ... })</c> や <c>Task.Run(() =&gt; { ... })</c> の本体は
    /// その場（＝<c>IsBusy=true</c> のまま）で走るため、ラムダを一律に除外すると
    /// <b>ガードが fail-open になる</b>（<c>.claude/rules/testing.md</c>「ガードの検出漏れは緑になる」）。
    /// 除外してよいのは Issue #1784 の遅延 <c>Action</c> 方式、すなわち
    /// <c>pending = () =&gt; ...</c> / <c>return () =&gt; ...</c> のように<b>後で呼ぶために保持される</b>形だけ。
    /// </remarks>
    private static readonly Regex HeldLambdaTailPattern = new Regex(
        @"(?:(?<![=!<>+\-*/%&|^])=|\breturn\b)\s*(?:async\s+)?(?:\(\s*\)|\w+|\([^()]*\))\s*=>\s*\z");

    /// <summary>
    /// <paramref name="index"/> の直前が「保持されるラムダの本体開始位置」か
    /// </summary>
    private static bool IsHeldForLaterInvocation(string code, int index)
    {
        var from = Math.Max(0, index - 160);
        return HeldLambdaTailPattern.IsMatch(code.Substring(from, index - from));
    }

    /// <summary>
    /// その呼び出しが「遅延実行される（後で呼ぶために保持されたラムダの中にある）」か
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1784 の <c>DataExportImportViewModel</c> は、結果ダイアログを
    /// <c>pendingResultDialog = () =&gt; _dialogService.ShowError(...)</c> のように <c>Action</c> へ保持し、
    /// <c>BeginBusy</c> スコープを抜けてから <c>Invoke()</c> する。<b>構文上はスコープの内側にあるが、
    /// 実行時点では <c>IsBusy=false</c> が確定している</b>ため違反ではない。
    /// </para>
    /// <para>
    /// この除外を入れないと、<b>規約を守っている実装（#1784）で赤になる</b>。誤検出はガード自体の
    /// 寿命を縮める（<c>.claude/rules/testing.md</c>「違反の確定を早まらない」）。ただし
    /// <b>除外を「ラムダ全般」へ広げると今度は fail-open になる</b>ため、
    /// <see cref="HeldLambdaTailPattern"/> で保持されるラムダに限定する。
    /// </para>
    /// </remarks>
    private static bool IsDeferred(string code, int callIndex, IReadOnlyList<(int Start, int End)> heldLambdaBlocks)
        => IsInsideAny(heldLambdaBlocks, callIndex) || IsHeldForLaterInvocation(code, callIndex);

    /// <summary>
    /// 後で呼ぶために保持されるラムダ（ブロック本体）の範囲を列挙する
    /// </summary>
    private static IReadOnlyList<(int Start, int End)> ExtractHeldLambdaBlocks(string code)
        => TestSourceInspection.ExtractLambdaBlockBodies(code)
            .Where(b => IsHeldForLaterInvocation(code, b.Start))
            .ToList();

    /// <summary>
    /// 指定位置を含む最も内側のスコープがあるか
    /// </summary>
    private static bool IsInsideAny(IReadOnlyList<(int Start, int End)> scopes, int index)
        => scopes.Any(s => s.Start <= index && index <= s.End);

    [Fact]
    public void BeginBusyスコープ内のモーダル表示はSuspendBusyで囲まれていること()
    {
        var violations = new List<string>();

        foreach (var (path, code) in GetFilesWithBusyScopes())
        {
            var busyScopes = ExtractBusyScopes(code);
            var suspendScopes = TestSourceInspection.ExtractUsingScopeBodies(code, "SuspendBusy");
            var lambdaBlocks = ExtractHeldLambdaBlocks(code);

            foreach (Match call in ModalCallPattern.Matches(code))
            {
                if (!IsInsideAny(busyScopes, call.Index))
                {
                    continue;
                }

                if (IsInsideAny(suspendScopes, call.Index) || IsDeferred(code, call.Index, lambdaBlocks))
                {
                    continue;
                }

                violations.Add(
                    $"{Path.GetFileName(path)}:{ToLineNumber(code, call.Index)} {call.Groups[1].Value}");
            }
        }

        violations.Should().BeEmpty(
            "BeginBusy スコープの内側でモーダルを表示すると、全面オーバーレイと不確定 ProgressBar が " +
            "ダイアログの背後で回り続ける（Issue #1793）。SuspendBusy で囲むこと。" +
            "戻り値を使わない結果表示も同じ（イディオムを 1 つに統一している）。違反: " +
            string.Join(" / ", violations));
    }

    [Fact]
    public void 検査対象が空振りしていないこと()
    {
        // 対象の絞り込みが壊れた（BeginBusy の表記ゆれ等で 0 件になった）ときに
        // 上の検査が緑のまま無力化するのを防ぐ。
        var files = GetFilesWithBusyScopes();

        files.Should().NotBeEmpty("BeginBusy を使う ViewModel が 1 つも見つからないのは絞り込みの不具合");

        // 数え方は検出側（ExtractBusyScopes）と揃える。BeginBusy だけを数えると、
        // BeginCancellableBusy／using 宣言形しか持たないファイルの抽出が壊れても気付けない。
        var scopeTotal = files.Sum(f => ExtractBusyScopes(f.Code).Count);
        scopeTotal.Should().BeGreaterThan(5, "処理中スコープの総数が極端に少ないのは抽出の不具合");

        // using 宣言形（`using var busyScope = BeginCancellableBusy(...)`）が
        // 抽出から落ちていないことを実データで表明する（落ちると違反が 1 件も検査されない）。
        files.Sum(f => TestSourceInspection.ExtractUsingScopeBodies(f.Code, "BeginCancellableBusy").Count)
            .Should().BeGreaterThan(0, "BeginCancellableBusy のスコープが 0 件なのは抽出の不具合");
    }

    [Fact]
    public void 是正済みの3画面がSuspendBusyを実際に使っていること()
    {
        // Issue #1793 の対象画面。SuspendBusy の呼び出しが消えた（＝別の形へ戻された）ことを検出する。
        // 上の違反検査は「BeginBusy スコープ内のモーダルが無くなった」場合も緑になるため、
        // 正しい置き換えが残っていることを別途表明する。
        var expected = new[]
        {
            "CardManageViewModel.cs",
            "StaffManageViewModel.cs",
            "SystemManageViewModel.cs"
        };

        foreach (var name in expected)
        {
            var path = GetViewModelFiles().Single(p => Path.GetFileName(p) == name);
            var code = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(path));

            TestSourceInspection.ExtractUsingScopeBodies(code, "SuspendBusy")
                .Should().NotBeEmpty($"{name} は Issue #1793 で SuspendBusy による是正を入れた画面");
        }
    }

    [Fact]
    public void 検査ロジックが既知のサンプル入力で違反を検出できること()
    {
        // 実データが空でも空振り検出が働くよう、検査ロジック自体をサンプルで固定する
        // （.claude/rules/development-conventions.md #1786）。
        const string violating = @"
void M()
{
    using (BeginBusy(""保存中...""))
    {
        _dialogService.ShowConfirmation(""ok?"", ""t"");
    }
}";
        const string compliant = @"
void M()
{
    using (BeginBusy(""保存中...""))
    {
        using (SuspendBusy())
        {
            _dialogService.ShowConfirmation(""ok?"", ""t"");
        }
    }
}";
        const string outsideScope = @"
void M()
{
    _dialogService.ShowConfirmation(""ok?"", ""t"");
    using (BeginBusy(""保存中...""))
    {
    }
}";

        // Issue #1784 の遅延 Action 方式（スコープ内で組み立て、スコープ外で Invoke）
        const string deferredExpression = @"
void M()
{
    Action pending = null;
    using (BeginBusy(""保存中...""))
    {
        pending = () => _dialogService.ShowError(""ng"", ""t"");
    }
    pending?.Invoke();
}";
        const string deferredBlock = @"
void M()
{
    Action pending = null;
    using (BeginBusy(""保存中...""))
    {
        pending = () => { _dialogService.ShowError(""ng"", ""t""); };
    }
    pending?.Invoke();
}";
        const string cancellableScope = @"
void M()
{
    using (BeginCancellableBusy(""処理中...""))
    {
        _dialogService.ShowConfirmation(""ok?"", ""t"");
    }
}";

        // using 宣言形（波括弧を伴わない）。スコープは宣言位置から囲みブロックの末尾まで。
        // 対象外にすると ReportViewModel.CreateReportsAsync が丸ごと検査から外れる。
        const string usingDeclarationScope = @"
void M()
{
    using var busyScope = BeginCancellableBusy(""処理中..."");
    MessageBox.Show(""ng"", ""t"");
}";

        // INavigationService 経由のモーダル（Window.ShowDialog）。
        // _dialogService.Show* だけを見ると印刷プレビューが検出できない。
        const string navigationDialog = @"
void M()
{
    using (BeginBusy(""準備中...""))
    {
        _navigationService.ShowDialog<PrintPreviewDialog>(d =>
        {
            d.Owner = null;
        });
    }
}";

        // INavigationService は IDialogService を継承しており、_navigationService.Show* も同期モーダル。
        // Issue #1837 で MessageBox.Show の直呼びがこの形へ移ったため、対象に含めないと
        // 是正済みの ReportViewModel の 2 か所が検査から静かに落ちる。
        const string navigationMessageBox = @"
void M()
{
    using (BeginBusy(""帳票を作成中...""))
    {
        _navigationService.ShowError(""ng"", ""t"");
    }
}";

        // 非モーダルの通知（トースト）は誤検出しないこと（対の表明）。
        // 誤検出すると規約を守っている実装で赤になり、ガード自体の寿命を縮める。
        const string toastNotification = @"
void M()
{
    using (BeginBusy(""処理中...""))
    {
        _toastNotificationService.ShowError(""ng"", ""t"");
    }
}";

        // その場で実行されるラムダ（Dispatcher / Task.Run）は遅延ではない。
        // 一律にラムダを除外すると、ここが素通りして fail-open になる。
        const string immediateLambda = @"
void M()
{
    using (BeginBusy(""保存中...""))
    {
        Dispatcher.InvokeAsync(() => { _dialogService.ShowError(""ng"", ""t""); });
    }
}";

        DetectViolations(violating).Should().Be(1, "SuspendBusy で囲まれていない呼び出しを検出すること");
        DetectViolations(compliant).Should().Be(0, "SuspendBusy で囲めば違反ではない");
        DetectViolations(outsideScope).Should().Be(0, "BeginBusy スコープの外は対象外");
        DetectViolations(deferredExpression).Should().Be(0,
            "式形式の遅延ラムダ（Issue #1784 の方式）は実行時点で IsBusy=false のため違反ではない");
        DetectViolations(deferredBlock).Should().Be(0, "ブロック形式の遅延ラムダも同様");
        DetectViolations(cancellableScope).Should().Be(1,
            "BeginCancellableBusy も処理中スコープなので検査対象に含めること");
        DetectViolations(usingDeclarationScope).Should().Be(1,
            "using 宣言形（using var x = Factory();）のスコープも検査対象に含めること");
        DetectViolations(navigationDialog).Should().Be(1,
            "INavigationService.ShowDialog<T>() も同期モーダルなので検査対象に含めること");
        DetectViolations(immediateLambda).Should().Be(1,
            "その場で実行されるラムダ（Dispatcher / Task.Run）は遅延ではないので除外しないこと");
        DetectViolations(navigationMessageBox).Should().Be(1,
            "_navigationService.Show* も同期モーダルなので検査対象に含めること（Issue #1837）");
        DetectViolations(toastNotification).Should().Be(0,
            "非モーダルのトースト通知は誤検出しないこと（Issue #1837 の対の表明）");
    }

    private static int DetectViolations(string source)
    {
        var code = TestSourceInspection.ToCodeOnlyPreservingLines(source);
        var busyScopes = ExtractBusyScopes(code);
        var suspendScopes = TestSourceInspection.ExtractUsingScopeBodies(code, "SuspendBusy");
        var lambdaBlocks = ExtractHeldLambdaBlocks(code);

        return ModalCallPattern.Matches(code)
            .Cast<Match>()
            .Count(m => IsInsideAny(busyScopes, m.Index)
                        && !IsInsideAny(suspendScopes, m.Index)
                        && !IsDeferred(code, m.Index, lambdaBlocks));
    }
}
