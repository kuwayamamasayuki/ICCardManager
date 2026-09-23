using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// インポート経路の構造規約を、ソーステキスト上で固定する
/// （結果処理の共有＝Issue #1785、結果ダイアログの遅延表示＝Issue #1784）
/// </summary>
/// <remarks>
/// <para>
/// <c>ImportAsync</c>（直接インポート）は <c>ExecuteImportAsync</c>（プレビュー経由）の全文複製で、
/// 約115行中8行しか差が無かった。結果処理・監査記録の修正を毎回2箇所へ適用する必要があり、
/// Issue #1741 では実際に一方の経路だけが壊れていた。
/// </para>
/// <para>
/// この回帰は挙動テストでは表明できない。両経路が同じ結果を返すことを何件テストしても、
/// 「コードが複製されている」状態そのものは緑のまま通るためである
/// （複製された2つの実装が偶然どちらも正しい間は落ちない）。
/// 検出したいのは「同じ修正を2箇所へ適用する義務が復活したこと」なので、
/// 本プロジェクトのレイアウト規約テストと同じくソーステキストを検査する。
/// </para>
/// <para>
/// <c>ImportAsync</c> は <c>OpenFileDialog</c> をコマンド内で生成するため単体テストから起動できず、
/// 挙動側の担保は共通メソッド <c>RunImportAsync</c> を直接呼ぶ
/// <see cref="DataExportImportViewModelTests"/> の「直接インポート経路」リージョンが担う。
/// </para>
/// </remarks>
public class DataExportImportViewModelImportPathSharingTests
{
    private const string ExecuteImportSignature = "public async Task ExecuteImportAsync()";
    private const string ImportSignature = "public async Task ImportAsync()";

    /// <summary>
    /// 直接インポートコマンドが、インポート元の決定だけを行い共通メソッドへ委譲していること
    /// </summary>
    [Fact]
    public void ImportAsync_は結果処理を複製せず共通メソッドへ委譲すること()
    {
        var body = ExtractMethodBody(ReadViewModelSource(), ImportSignature);

        // 抽出の妥当性: この経路はファイルダイアログでインポート元を選ぶ
        body.Should().Contain(
            "OpenFileDialog",
            "抽出範囲が想定どおり ImportAsync の本体であること");

        AssertDelegatesToSharedImport(TestSourceInspection.ToCodeOnly(body), ImportSignature);
    }

    /// <summary>
    /// プレビュー経由のインポートコマンドが、入力検証だけを行い共通メソッドへ委譲していること
    /// </summary>
    [Fact]
    public void ExecuteImportAsync_は結果処理を複製せず共通メソッドへ委譲すること()
    {
        var body = ExtractMethodBody(ReadViewModelSource(), ExecuteImportSignature);

        // 抽出の妥当性: この経路はプレビュー未実行・バリデーションエラーを門前で弾く
        body.Should().Contain(
            "プレビューを実行してください",
            "抽出範囲が想定どおり ExecuteImportAsync の本体であること");

        AssertDelegatesToSharedImport(TestSourceInspection.ToCodeOnly(body), ExecuteImportSignature);
    }

    /// <summary>
    /// データ種別ごとのインポートサービス呼び分けが、ファイル全体で1箇所だけであること
    /// </summary>
    /// <remarks>
    /// 複製が復活すると各呼び出しが2箇所になる。Issue #511（台帳の対象カードIDm）のように
    /// 引数が増える修正は、この呼び分けの箇所数だけ適用漏れの機会が生まれる。
    /// </remarks>
    [Theory]
    [InlineData("_importService.ImportCardsAsync(")]
    [InlineData("_importService.ImportStaffAsync(")]
    [InlineData("_importService.ImportLedgersAsync(")]
    [InlineData("_importService.ImportLedgerDetailsAsync(")]
    public void インポートサービスの呼び分けはソース全体で1箇所であること(string invocation)
    {
        // コメントを数えない。「呼び分けは RunImportAsync の _importService.ImportCardsAsync( だけ」のような
        // 説明コメントが 1 件と数えられると、実コードの呼び出しを消しても 1 箇所のまま緑になる（Issue #2101）。
        var source = TestSourceInspection.ToCodeOnly(ReadViewModelSource());

        CountOccurrences(source, invocation).Should().Be(
            1,
            $"{invocation} が複数箇所にあると、引数を増やす修正のたびに適用漏れが起こるため");
    }

    /// <summary>
    /// コマンド本体が結果処理（サービス呼び出し・監査ログ・完了通知）を持たず、共通メソッドを呼ぶこと
    /// </summary>
    /// <param name="body">コメントと文字列リテラルを除いた本体（<see cref="TestSourceInspection.ToCodeOnly"/> 済み）。</param>
    /// <param name="signature">報告用のシグネチャ。</param>
    private static void AssertDelegatesToSharedImport(string body, string signature)
    {
        body.Should().Contain(
            "RunImportAsync(",
            $"{signature} はインポート本体を共通メソッドへ委譲すること");
        body.Should().NotContain(
            "_importService.",
            $"{signature} がインポートサービスを直接呼ぶと、データ種別の呼び分けが再び複製される");
        body.Should().NotContain(
            "TryLogImportAsync",
            $"{signature} が監査ログを記録すると、記録内容の修正が2箇所必要になる（Issue #1741 の再発）");
        body.Should().NotContain(
            "_dialogService.Show",
            $"{signature} が完了・エラー通知を持つと、文言の修正が2箇所必要になる");
    }

    #region 結果ダイアログを BeginBusy スコープの外で表示し続けること（Issue #1784）

    // 結果ダイアログを `using (BeginBusy("インポート中..."))` の内側で表示すると、
    // IDialogService の実装（同期モーダルの MessageBox.Show）が職員の OK まで
    // 呼び出しスレッドをブロックするため BusyScope.Dispose() が走らず、
    // 処理中オーバーレイと不確定 ProgressBar が完了ダイアログの背後で回り続ける。
    //
    // この規約は挙動テスト（UT-028 No.40〜44）だけでは守れない。既存5件は
    // 自分の担当分岐しか通らないため、6つ目の分岐が追加されてそこだけスコープ内で
    // 直接 ShowXxx を呼んでも、テストは全て緑のまま通り現象がその分岐でだけ再発する。
    // 検出したいのは「新しい分岐が規約から外れたこと」なので、ソーステキストを検査する。

    private const string RunImportSignature = "internal async Task RunImportAsync(string sourceFilePath)";
    private const string HandleImportResultSignature = "private async Task<Action> HandleImportResultAsync(";
    private const string DialogInvocation = "_dialogService.Show";
    private const string DeferredDialogInvoke = "pendingResultDialog?.Invoke()";

    /// <summary>
    /// インポート本体と結果処理の中で、ダイアログ表示が必ず遅延ラムダに載っていること
    /// </summary>
    /// <remarks>
    /// 直接呼び出し（<c>_dialogService.ShowXxx(...)</c>）は、それが書かれた地点で
    /// その場のスコープのまま実行される。ラムダに載せる形を強制すれば、実行地点は
    /// <see cref="DataExportImportViewModel.RunImportAsync"/> 末尾の1箇所へ必ず集約される。
    /// </remarks>
    [Theory]
    [InlineData(RunImportSignature, "インポート中...")]
    [InlineData(HandleImportResultSignature, "importCommitted")]
    public void インポート経路のダイアログ表示は遅延ラムダに載せること(
        string signature, string extractionMarker)
    {
        var body = ExtractMethodBody(ReadViewModelSource(), signature);

        // 抽出の妥当性: 範囲が縮んで空振りしていないこと
        body.Should().Contain(
            extractionMarker,
            $"抽出範囲が想定どおり {signature} の本体であること");

        var code = TestSourceInspection.ToCodeOnly(body);
        code.Should().Contain(
            DialogInvocation,
            $"{signature} が検査対象のダイアログ表示を1つも含まないなら、本テストは何も検査していない");

        FindImmediateDialogInvocations(code).Should().BeEmpty(
            $"{signature} の {DialogInvocation} は、代入または return で保持する遅延ラムダ"
            + "（`pending = () => ...` / `return () => ...`）に載せること。"
            + "その場で呼ぶ（引数として渡したラムダを含む）と BeginBusy スコープ内で実行され、"
            + "完了ダイアログの背後にプログレスバーが残る（Issue #1784）");
    }

    /// <summary>
    /// <see cref="FindImmediateDialogInvocations"/> の判定をサンプル入力で固定する（Issue #2101）。
    /// </summary>
    /// <remarks>
    /// 旧実装は「呼び出しの直前が <c>=&gt;</c> なら遅延」とみなしていたため、
    /// <c>Task.Run(() =&gt; _dialogService.ShowError(…))</c> のように<b>引数として渡した（その場で走る）
    /// ラムダ</b>も合格にしていた。遅延とみなすのは、代入の右辺または <c>return</c> で保持されるラムダだけ
    /// （判定は <see cref="TestSourceInspection.IsHeldLambdaHead"/> に 1 つだけ置き、
    /// <c>BusyScopeDialogConventionTests</c> と共有する）。
    /// </remarks>
    [Theory]
    // 遅延（Issue #1784 の方式）
    [InlineData("pendingResultDialog = () => _dialogService.ShowError(m, \"インポートエラー\");", false)]
    [InlineData("return () => _dialogService.ShowInformation(m, \"インポート完了\");", false)]
    [InlineData("return () => { _dialogService.ShowWarning(m, \"t\"); };", false)]
    [InlineData("pending = () =>\n    _dialogService.ShowWarning(\n        m, \"t\");", false)]
    // 規約の理由を書いたコメント・文字列は検査しない（極性の反転。#1692）
    [InlineData("// _dialogService.ShowError(m) を直接呼ばないこと\nreturn () => _dialogService.ShowError(m, \"t\");", false)]
    [InlineData("SetStatus(\"_dialogService.ShowError は呼ばない\", true);", false)]
    // その場で実行される
    [InlineData("_dialogService.ShowError(m, \"インポートエラー\");", true)]
    [InlineData("Task.Run(() => _dialogService.ShowError(m, \"t\"));", true)]
    [InlineData("Dispatcher.Invoke(() => { _dialogService.ShowError(m, \"t\"); });", true)]
    [InlineData("new Action(() => _dialogService.ShowError(m, \"t\"))();", true)]
    [InlineData("button.Click += (s, e) => _dialogService.ShowError(m, \"t\");", true)]
    public void 即時実行のダイアログ表示の判定がサンプル入力で固定されていること(string statement, bool expectedImmediate)
    {
        var code = TestSourceInspection.ToCodeOnly("void M()\n{\n" + statement + "\n}");

        FindImmediateDialogInvocations(code).Any().Should().Be(expectedImmediate);
    }

    /// <summary>
    /// 保持される遅延ラムダに載っていない（＝書かれた地点で実行される）ダイアログ表示の位置を列挙する。
    /// </summary>
    /// <param name="codeOnly"><see cref="TestSourceInspection.ToCodeOnly"/> 済みのテキスト。</param>
    internal static IReadOnlyList<int> FindImmediateDialogInvocations(string codeOnly)
    {
        var heldBlocks = TestSourceInspection.ExtractHeldLambdaBlockBodies(codeOnly);

        return IndexesOf(codeOnly, DialogInvocation)
            .Where(index => !TestSourceInspection.IsHeldLambdaHead(codeOnly, index)
                            && !heldBlocks.Any(b => b.Start <= index && index <= b.End))
            .ToList();
    }

    /// <summary>
    /// 遅延させたダイアログの実行地点が、BeginBusy スコープの外にあること
    /// </summary>
    /// <remarks>
    /// ラムダに載せても実行地点がスコープの内側にあれば意味がない。
    /// 「載せ方」と「実行地点」の両方を固定しないと規約は成立しない。
    /// </remarks>
    [Fact]
    public void 遅延させた結果ダイアログの実行地点はBeginBusyスコープの外にあること()
    {
        var body = TestSourceInspection.ToCodeOnly(ExtractMethodBody(ReadViewModelSource(), RunImportSignature));

        CountOccurrences(body, DeferredDialogInvoke).Should().Be(
            1,
            $"結果ダイアログの実行地点は {RunImportSignature} にただ1つであること");

        var busyScopeBody = ExtractBusyScopeBody(body);

        // 抽出の妥当性: スコープ本体にインポートサービスの呼び分けが含まれること
        busyScopeBody.Should().Contain(
            "_importService.",
            "抽出範囲が想定どおり BeginBusy スコープの本体であること");

        busyScopeBody.Should().NotContain(
            DeferredDialogInvoke,
            "スコープ内で実行するとラムダに載せた意味が無く、"
            + "モーダル表示中 IsBusy=true のままプログレスバーが残る（Issue #1784）");
    }

    /// <summary>
    /// <c>using (BeginBusy(...))</c> の本体（対応する <c>}</c> まで）を取り出す
    /// </summary>
    /// <param name="codeOnlyMethodBody"><see cref="TestSourceInspection.ToCodeOnly"/> 済みのメソッド本体。</param>
    private static string ExtractBusyScopeBody(string codeOnlyMethodBody)
    {
        var scopes = TestSourceInspection.ExtractUsingScopeBodies(codeOnlyMethodBody, "BeginBusy");
        scopes.Should().ContainSingle(
            $"{RunImportSignature} が BeginBusy スコープをちょうど 1 つ持つこと（処理中表示を変えたら本テストも見直すこと）");

        var (start, end) = scopes[0];
        return codeOnlyMethodBody.Substring(start, end - start + 1);
    }

    private static IEnumerable<int> IndexesOf(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            yield return index;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
    }

    #endregion

    private static string ReadViewModelSource()
        => File.ReadAllText(
            ViewSourceLocator.Resolve(Path.Combine("ViewModels", "DataExportImportViewModel.cs")));

    /// <summary>
    /// メソッド本体（<c>{ }</c> 含む）を、コメントを除き文字列リテラルを残したまま取り出す。
    /// </summary>
    /// <remarks>
    /// 抽出は <see cref="TestSourceInspection.ExtractMethodBodyPreservingLiterals"/> に寄せる（Issue #2101）。
    /// 生のソースで波括弧を数える私的コピーは、コメントや文字列の中の <c>{</c> <c>}</c> で抽出範囲が
    /// 黙って伸び縮みし、doc コメント中のシグネチャ文字列を本体と取り違える。リテラルを残すのは、
    /// 抽出の妥当性を文言（「プレビューを実行してください」「インポート中...」）で確かめるため。
    /// 禁止トークンの検査は呼び出し側で <see cref="TestSourceInspection.ToCodeOnly"/> を通してから行う。
    /// </remarks>
    private static string ExtractMethodBody(string source, string signature)
        => TestSourceInspection.ExtractMethodBodyPreservingLiterals(source, signature);

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
