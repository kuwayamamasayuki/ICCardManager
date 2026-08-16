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
    /// （Issue #1793 で <c>SystemManageViewModel</c> の 2 か所を <c>IDialogService</c> へ移したが、
    /// 直呼びが再び持ち込まれても検出できるようにする）。
    /// </summary>
    private static readonly Regex ModalCallPattern = new Regex(
        @"(_dialogService\.Show\w+|MessageBox\.Show)\s*\(");

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
    /// その呼び出しが「遅延実行される（ラムダの中にある）」か
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
    /// 寿命を縮める（<c>.claude/rules/testing.md</c>「違反の確定を早まらない」）。
    /// </para>
    /// </remarks>
    private static bool IsDeferred(string code, int callIndex, IReadOnlyList<(int Start, int End)> lambdaBlocks)
    {
        if (IsInsideAny(lambdaBlocks, callIndex))
        {
            return true;
        }

        // 式形式のラムダ（`() => _dialogService.ShowXxx(...)`）: 直前の非空白が "=>"
        var i = callIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(code[i]))
        {
            i--;
        }

        return i >= 1 && code[i] == '>' && code[i - 1] == '=';
    }

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
            var lambdaBlocks = TestSourceInspection.ExtractLambdaBlockBodies(code);

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

        var scopeTotal = files.Sum(f =>
            TestSourceInspection.ExtractUsingScopeBodies(f.Code, "BeginBusy").Count);
        scopeTotal.Should().BeGreaterThan(5, "BeginBusy スコープの総数が極端に少ないのは抽出の不具合");
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

        DetectViolations(violating).Should().Be(1, "SuspendBusy で囲まれていない呼び出しを検出すること");
        DetectViolations(compliant).Should().Be(0, "SuspendBusy で囲めば違反ではない");
        DetectViolations(outsideScope).Should().Be(0, "BeginBusy スコープの外は対象外");
        DetectViolations(deferredExpression).Should().Be(0,
            "式形式の遅延ラムダ（Issue #1784 の方式）は実行時点で IsBusy=false のため違反ではない");
        DetectViolations(deferredBlock).Should().Be(0, "ブロック形式の遅延ラムダも同様");
        DetectViolations(cancellableScope).Should().Be(1,
            "BeginCancellableBusy も処理中スコープなので検査対象に含めること");
    }

    private static int DetectViolations(string source)
    {
        var code = TestSourceInspection.ToCodeOnlyPreservingLines(source);
        var busyScopes = ExtractBusyScopes(code);
        var suspendScopes = TestSourceInspection.ExtractUsingScopeBodies(code, "SuspendBusy");
        var lambdaBlocks = TestSourceInspection.ExtractLambdaBlockBodies(code);

        return ModalCallPattern.Matches(code)
            .Cast<Match>()
            .Count(m => IsInsideAny(busyScopes, m.Index)
                        && !IsInsideAny(suspendScopes, m.Index)
                        && !IsDeferred(code, m.Index, lambdaBlocks));
    }
}
