using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2143: メイン画面の終了確認と、使い方ガイドの再タッチ案内の結線を固定する。
/// </summary>
/// <remarks>
/// <para>
/// 確認の<b>判断と文言</b>は <c>MainViewModelTests</c> の「Issue #2143」region が挙動テストで固定する。
/// ここで見るのは、単体テストから踏めない経路（<c>Application.Current.Shutdown()</c>・
/// <c>Window</c> のメッセージフック）の<b>結線</b>である。<c>Window</c> は STA 依存で xUnit から生成できない。
/// </para>
/// <para>
/// <b>検査できない範囲</b>: ✕・Alt+F4 で実際に確認が出るか、「いいえ」で画面が残るか、
/// OS のサインアウトで確認が出ないかは実機で手動検証する。
/// </para>
/// </remarks>
public class ExitConfirmationConventionTests
{
    private static string ReadCodeOnly(string relativePath)
        => TestSourceInspection.ToCodeOnly(File.ReadAllText(ViewSourceLocator.Resolve(relativePath)));

    /// <summary>
    /// 「終了」ボタン（<c>ExitCommand</c>）は、確認してから終了すること。
    /// </summary>
    /// <remarks>
    /// 「確認の存在」だけを見ると、確認の結果を無視して終了する実装（<c>ConfirmExit();</c> の後に無条件の
    /// <c>Shutdown()</c>）でも緑になる。確認が Shutdown より前にあり、かつ「いいえ」で戻る分岐を持つことを対で見る。
    /// </remarks>
    [Fact]
    public void Exitは確認の結果で分岐してからShutdownすること()
    {
        var body = TestSourceInspection.ExtractMethodBody(
            ReadCodeOnly("ViewModels/MainViewModel.cs"), "public void Exit()");

        var confirmIndex = body.IndexOf("if (!ConfirmExit())", StringComparison.Ordinal);
        var shutdownIndex = body.IndexOf("Shutdown()", StringComparison.Ordinal);

        confirmIndex.Should().BeGreaterOrEqualTo(0, "終了ボタンは確認の結果で分岐すること");
        shutdownIndex.Should().BeGreaterThan(confirmIndex, "確認より先に終了してはならない");
        body.Substring(confirmIndex, shutdownIndex - confirmIndex).Should().Contain("return;",
            "「いいえ」を選んだら終了せずに戻ること");
    }

    /// <summary>
    /// メイン画面の ✕・Alt+F4 は <c>WM_SYSCOMMAND(SC_CLOSE)</c> のフックで、「終了」ボタンと同じ確認を通すこと。
    /// </summary>
    /// <remarks>
    /// <b>対の表明</b>: <c>Closing</c> では確認しないこと。<c>Closing</c> は OS のサインアウト・シャットダウンや
    /// 「終了」ボタンの <c>Application.Shutdown()</c>（確認済み）でも発生するため、そこで尋ねると確認が二重になり、
    /// OS の終了を確認ダイアログで止めてしまう。
    /// </remarks>
    [Fact]
    public void メイン画面の閉じる操作はSC_CLOSEのフックで終了ボタンと同じ確認を通すこと()
    {
        var code = ReadCodeOnly("Views/MainWindow.xaml.cs");

        TestSourceInspection.ExtractMethodBody(code, "protected override void OnSourceInitialized")
            .Should().Contain("AddHook(ConfirmUserCloseHook)", "ウィンドウのメッセージフックを掛けること");

        var hook = TestSourceInspection.ExtractMethodBody(code, "private IntPtr ConfirmUserCloseHook");
        hook.Should().Contain("BusyCloseGuard.IsUserCloseCommand(msg, wParam)",
            "利用者の閉じる操作（SC_CLOSE）の判定は BusyCloseGuard と共有する（判定を 2 か所に書かない）");
        // 極性まで照合する。`ConfirmExit()` の存在だけを見ると、「はい」で握り潰す（＝閉じられなくなる）
        // 反転した実装でも緑になる（コードレビューで検出）
        hook.Should().Contain("if (!_viewModel.ConfirmExit())", "確認は「終了」ボタンと同じ 1 つを通し、「いいえ」で分岐する");
        hook.Should().Contain("handled = true", "「いいえ」なら閉じる操作を握り潰す");

        // 対の表明: 確認はフックの中だけにある。Closing（既存のハンドラーでも OnClosing の新設でも）で確認すると
        // OS の終了や確認済みの終了まで止める。ファイル全体で数えるので、確認を置く場所を増やす形を網羅する
        var outsideHook = code.Replace(hook, string.Empty);
        outsideHook.Should().NotContain("ConfirmExit", "確認は SC_CLOSE のフックの中だけに置く");
    }

    /// <summary>
    /// 使い方ガイドの再タッチ案内は、秒数を直書きせず設定値から組み立てた文言を表示すること。
    /// </summary>
    /// <remarks>
    /// 秒数は <c>AppOptions.RetouchWindowSeconds</c> で変更できる。「30秒」を XAML に直書きすると、
    /// 設定を変えたときに案内だけが実際の判定と食い違う。旧見出し「誤操作の修正」は、
    /// 誤った記録が消えると読まれた（30 秒ルールは逆の操作を新たに記録するだけで、元の記録は残る）。
    /// </remarks>
    [Fact]
    public void 使い方ガイドの再タッチ案内は設定値から組み立てた文言を表示すること()
    {
        var xaml = XamlElementInspection.StripXmlComments(
            File.ReadAllText(ViewSourceLocator.Resolve("Views/MainWindow.xaml")));

        xaml.Should().Contain("Text=\"{Binding RetouchGuideText", "案内は ViewModel が設定値から組み立てた文言を表示する");
        xaml.Should().NotContain("誤操作の修正", "再タッチは取り消しではないので「修正」と書かない");
        xaml.Should().NotContain("30秒以内", "秒数は設定で変わるので直書きしない");
    }
}
