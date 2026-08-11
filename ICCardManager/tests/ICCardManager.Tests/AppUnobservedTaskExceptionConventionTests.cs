using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1742: <c>App.xaml.cs</c> の未観測 Task 例外ハンドラが
/// 「ファイナライザスレッドをブロックしない」形で配線されていることを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// <c>TaskScheduler.UnobservedTaskException</c> はファイナライザスレッドで発火するため、
/// ハンドラ内の同期ディスパッチ（<c>Dispatcher.Invoke</c>）＋モーダル表示
/// （<c>ErrorDialogHelper</c> の Show 系 → <c>MessageBox.Show</c>）は、ユーザーが [OK] を押すまで
/// プロセス全体のファイナライザを停止させる。
/// </para>
/// <para>
/// ハンドラ本体（<c>UnobservedTaskExceptionHandler</c>）の挙動は
/// <see cref="Common.UnobservedTaskExceptionHandlerTests"/> が検証する。
/// <c>App</c> は WPF <c>Application</c> のため単体テストから駆動できず、
/// 配線側の回帰はソーステキストの静的検証で固定する
/// （<c>CardManageDialogStatusAreaLayoutTests</c> 等と同じ流儀）。
/// 検査は <see cref="TestSourceInspection.ToCodeOnly"/> でコメントと文字列リテラルの中身を
/// 除去した「コードのみ」を対象とし、規約の理由をコメントに書けるようにしつつ、
/// コメント・文字列内の波括弧が抽出を狂わせないようにする。
/// </para>
/// </remarks>
public class AppUnobservedTaskExceptionConventionTests
{
    private const string HandlerSignature = "private void OnUnobservedTaskException";
    private const string WiringSignature = "private UnobservedTaskExceptionHandler CreateUnobservedTaskExceptionHandler";

    /// <summary>
    /// 同期ディスパッチの検出パターン。固定の部分文字列
    /// （<c>"Dispatcher.Invoke("</c>）では空白の挿入や中間変数
    /// （<c>var d = app.Dispatcher; d.Invoke(...)</c>）で迂回できるため、
    /// 「メンバー呼び出しとしての <c>.Invoke(</c>」を語形ゆれ込みで照合する。
    /// <c>BeginInvoke(</c> / <c>InvokeAsync(</c> は直前が <c>.</c> でない・直後が <c>(</c> でないため一致しない。
    /// </summary>
    private static readonly Regex SyncInvokePattern = new(@"\.Invoke\s*\(", RegexOptions.Compiled);

    private static string ReadAppCodeOnly()
        => TestSourceInspection.ToCodeOnly(
            File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs")));

    [Fact]
    public void メソッド本体抽出_コメントや文字列に波括弧や禁止トークンがあっても正しく取り出せること()
    {
        // 検査ロジック自体を既知のサンプル入力で固定する（development-conventions.md）。
        // 実ソース側の抽出が空振りしても、この検証が抽出器の劣化と実ソースの変化を切り分ける。
        // サンプルには迂回経路になり得る要素を仕込む:
        //   ①コメント内の閉じ波括弧（生ソースへの波括弧対応だと本体が途中で切れる）
        //   ②文字列リテラル内の "//"（正規表現ベースの除去だと同一行の後続コードが消える）
        //   ③文字列リテラル内の閉じ波括弧
        //   ④コメント内の禁止トークン（コード扱いすると戒めのコメントが書けなくなる）
        const string sample = @"
            class C
            {
                private void Target()
                {
                    // } このコメントの閉じ波括弧で抽出が切れてはいけない
                    /* Dispatcher.Invoke( をコメントで言及しても検出対象にしない */
                    var s = ""literal with } and // not a comment""; Marker();
                    if (true) { Inner(); }
                }
                private void Other() { Forbidden(); }
            }";

        var body = TestSourceInspection.ExtractMethodBody(TestSourceInspection.ToCodeOnly(sample), "private void Target");

        body.Should().Contain("Inner();", "対象メソッドの本体を取り出せていること");
        body.Should().Contain("Marker();", "文字列リテラル内の // を行コメント扱いして同一行の後続コードを消してはいけない");
        body.Should().NotContain("Forbidden", "次のメソッドまで巻き込んでいないこと");
        body.Should().NotContain("literal with", "文字列リテラルの中身は検査対象から除外されていること");
        SyncInvokePattern.IsMatch(body).Should().BeFalse("コメント内の言及は検出対象にしないこと");
    }

    [Fact]
    public void 同期Invoke検出パターン_語形ゆれや中間変数による迂回を検出できること()
    {
        // 検出パターン自体の有効性を固定する。固定の部分文字列照合へ退化させると
        // 空白の挿入や中間変数で静かに迂回できるようになる
        SyncInvokePattern.IsMatch("Dispatcher.Invoke(() => { })").Should().BeTrue();
        SyncInvokePattern.IsMatch("Dispatcher.Invoke (action)").Should().BeTrue("空白の挿入で迂回できてはいけない");
        SyncInvokePattern.IsMatch("var d = app.Dispatcher; d.Invoke(action);").Should().BeTrue("中間変数で迂回できてはいけない");

        SyncInvokePattern.IsMatch("Dispatcher.BeginInvoke(action)").Should().BeFalse("非同期の BeginInvoke は許容される");
        SyncInvokePattern.IsMatch("Dispatcher.InvokeAsync(action)").Should().BeFalse("非同期の InvokeAsync は許容される");
    }

    [Fact]
    public void OnUnobservedTaskException_同期ディスパッチとモーダル表示を含まないこと()
    {
        var body = TestSourceInspection.ExtractMethodBody(ReadAppCodeOnly(), HandlerSignature);

        // 同期 Invoke はファイナライザスレッドを UI 処理の完了までブロックする
        SyncInvokePattern.IsMatch(body).Should().BeFalse(
            "ファイナライザスレッドからの同期ディスパッチは、モーダル表示が閉じられるまで" +
            "プロセス全体のファイナライザを停止させる（Issue #1742）");

        // ErrorDialogHelper の Show 系は内部で Dispatcher.Invoke（同期）＋ MessageBox.Show（モーダル）を行う。
        // ハンドラ本体は SetObserved と委譲だけを行うべきなので、ここでは LogException も含め全面禁止
        body.Should().NotContain("ErrorDialogHelper", "ハンドラ本体は SetObserved と委譲だけを行う");
        body.Should().NotContain("MessageBox", "モーダルダイアログ経路を使わない");
    }

    [Fact]
    public void OnUnobservedTaskException_最初に観測済みにしてからハンドラへ委譲すること()
    {
        var body = TestSourceInspection.ExtractMethodBody(ReadAppCodeOnly(), HandlerSignature);

        body.Should().Contain("SetObserved()",
            "観測済みにしないと未観測例外がプロセスを異常終了させる（.NET Framework の既定挙動）");
        body.Should().Contain("_unobservedTaskExceptionHandler",
            "テスト可能な UnobservedTaskExceptionHandler へ委譲する（Common.UnobservedTaskExceptionHandlerTests が挙動を検証）");

        var setObservedIndex = body.IndexOf("SetObserved()", StringComparison.Ordinal);
        var delegateIndex = body.IndexOf("_unobservedTaskExceptionHandler", StringComparison.Ordinal);
        setObservedIndex.Should().BeLessThan(delegateIndex,
            "以降の処理で何が起きてもプロセスを守れるよう、SetObserved を最初に呼ぶ");
    }

    [Fact]
    public void ハンドラ配線_シャットダウンガード付きの非同期ディスパッチと非モーダル通知を用いること()
    {
        var body = TestSourceInspection.ExtractMethodBody(ReadAppCodeOnly(), WiringSignature);

        // シャットダウンガード: Dispatcher 停止後のディスパッチは実行されず、例外の温床になるだけ
        body.Should().Contain("HasShutdownStarted", "アプリ終了中の発火に備えたガードを持つこと");
        body.Should().Contain("HasShutdownFinished", "アプリ終了後の発火に備えたガードを持つこと");

        // 非同期ディスパッチ: BeginInvoke はファイナライザスレッドをブロックしない
        body.Should().Contain("BeginInvoke", "UI スレッドへは非同期でポストすること");
        SyncInvokePattern.IsMatch(body).Should().BeFalse("同期ディスパッチへ退行させない");

        // 非モーダル通知: トーストはユーザーの操作を要求せず、ファイナライザ停止の原因にならない。
        // 文言は例外種別に応じて ToUserMessage で組み立てる（固定文言だと管理者へ原因が伝わらない）
        body.Should().Contain("IToastNotificationService", "非モーダルなトースト通知を使うこと");
        body.Should().Contain("ToUserMessage", "例外種別に応じた文言を通知に載せること（固定文言へ退行させない）");
        body.Should().NotContain("ErrorDialogHelper.Show", "モーダルダイアログ経路へ退行させない");
        body.Should().NotContain("MessageBox", "モーダルダイアログ経路へ退行させない");

        // DI 非依存のフォールバックログ: ロガー未初期化の時期（DI コンテナ構築前）の発火でも
        // error_YYYYMMDD.log へ痕跡が残ること（LogException はダイアログ非表示のログ専用経路）
        body.Should().Contain("ErrorDialogHelper.LogException",
            "ILogger が使えない時期の発火を無痕跡にしない（旧実装は ErrorDialogHelper が DI 非依存でログしていた）");
    }
}
