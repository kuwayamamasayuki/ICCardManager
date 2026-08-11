using System;
using System.Collections.Generic;
using System.IO;
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

        AssertDelegatesToSharedImport(body, ImportSignature);
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

        AssertDelegatesToSharedImport(body, ExecuteImportSignature);
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
        var source = ReadViewModelSource();

        CountOccurrences(source, invocation).Should().Be(
            1,
            $"{invocation} が複数箇所にあると、引数を増やす修正のたびに適用漏れが起こるため");
    }

    /// <summary>
    /// コマンド本体が結果処理（サービス呼び出し・監査ログ・完了通知）を持たず、共通メソッドを呼ぶこと
    /// </summary>
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
        body.Should().Contain(
            DialogInvocation,
            $"{signature} が検査対象のダイアログ表示を1つも含まないなら、本テストは何も検査していない");

        foreach (var index in IndexesOf(body, DialogInvocation))
        {
            var preceding = body.Substring(0, index).TrimEnd();

            preceding.Should().EndWith(
                "=>",
                $"{signature} の {DialogInvocation} は遅延ラムダ（`() => ...`）に載せること。"
                + "その場で呼ぶと BeginBusy スコープ内で実行され、"
                + "完了ダイアログの背後にプログレスバーが残る（Issue #1784）");
        }
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
        var body = ExtractMethodBody(ReadViewModelSource(), RunImportSignature);

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
    private static string ExtractBusyScopeBody(string methodBody)
    {
        const string BusyScopeHeader = "using (BeginBusy(";

        var headerIndex = methodBody.IndexOf(BusyScopeHeader, StringComparison.Ordinal);
        headerIndex.Should().BeGreaterOrEqualTo(
            0,
            $"{RunImportSignature} が {BusyScopeHeader} を持つこと（処理中表示を外したら本テストも見直すこと）");

        return ExtractBlockAt(methodBody, headerIndex);
    }

    /// <summary>
    /// 指定位置以降の最初の <c>{</c> から対応する <c>}</c> までを取り出す
    /// </summary>
    private static string ExtractBlockAt(string source, int fromIndex)
    {
        var openIndex = source.IndexOf('{', fromIndex);
        openIndex.Should().BeGreaterOrEqualTo(0, "ブロックの開始位置が見つかること");

        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
        }

        throw new InvalidOperationException("ブロックの終端が見つかりませんでした");
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
    /// メソッドシグネチャ直後の <c>{</c> から対応する <c>}</c> までを本体として取り出す
    /// </summary>
    private static string ExtractMethodBody(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        signatureIndex.Should().BeGreaterOrEqualTo(
            0,
            $"検査対象 {signature} がソース中に存在すること（改名したら本テストも追随すること）");

        var openIndex = source.IndexOf('{', signatureIndex + signature.Length);
        openIndex.Should().BeGreaterOrEqualTo(0, $"{signature} の本体開始位置が見つかること");

        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
        }

        throw new InvalidOperationException($"{signature} の本体終端が見つかりませんでした");
    }

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
