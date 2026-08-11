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
/// （<c>ErrorDialogHelper</c> → <c>MessageBox.Show</c>）は、ユーザーが [OK] を押すまで
/// プロセス全体のファイナライザを停止させる。
/// </para>
/// <para>
/// ハンドラ本体（<c>UnobservedTaskExceptionHandler</c>）の挙動は
/// <see cref="Common.UnobservedTaskExceptionHandlerTests"/> が検証する。
/// <c>App</c> は WPF <c>Application</c> のため単体テストから駆動できず、
/// 配線側の回帰はソーステキストの静的検証で固定する
/// （<c>CardManageDialogStatusAreaLayoutTests</c> 等と同じ流儀）。
/// 検査はコメントを除去したコードのみを対象とし、規約の理由をコメントに書けるようにする。
/// </para>
/// </remarks>
public class AppUnobservedTaskExceptionConventionTests
{
    private const string HandlerSignature = "private void OnUnobservedTaskException";
    private const string WiringSignature = "private UnobservedTaskExceptionHandler CreateUnobservedTaskExceptionHandler";

    private static string ReadAppSource()
        => File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs"));

    /// <summary>
    /// コメント（<c>//</c> 行コメント・<c>/* */</c> ブロックコメント）を除去する。
    /// 「禁止された要素の不在」検査がコメント中の正当な言及
    /// （例: 「同期 Invoke を使わない」という戒め）を誤検出しないようにするため。
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\r\n]*", "");
    }

    /// <summary>
    /// シグネチャ文字列から始まるメソッドの本体（<c>{ }</c> 含む）を波括弧の対応で取り出す。
    /// </summary>
    private static string ExtractMethodBody(string source, string signatureMarker)
    {
        var start = source.IndexOf(signatureMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0,
            $"App.xaml.cs に「{signatureMarker}」が存在するはず。リネームした場合は本テストの定数も更新すること");

        var braceStart = source.IndexOf('{', start);
        braceStart.Should().BeGreaterThan(start, "メソッド本体の開始波括弧が見つかるはず");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
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
                    return source.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"「{signatureMarker}」の本体の波括弧が閉じていない。");
    }

    [Fact]
    public void メソッド本体抽出_既知のサンプルから正しく取り出せること()
    {
        // 検査ロジック自体を既知のサンプル入力で固定する（development-conventions.md）。
        // 実ソース側の抽出が空振りしても、この検証が抽出器の劣化と実ソースの変化を切り分ける
        const string sample = @"
            class C
            {
                // Dispatcher.Invoke( をコメントで言及しても検出対象にしない
                private void Target()
                {
                    if (true) { Inner(); }
                }
                private void Other() { Forbidden(); }
            }";

        var body = ExtractMethodBody(sample, "private void Target");
        var stripped = StripComments(body);

        stripped.Should().Contain("Inner();", "対象メソッドの本体を取り出せていること");
        stripped.Should().NotContain("Forbidden", "次のメソッドまで巻き込んでいないこと");
        stripped.Should().NotContain("Dispatcher.Invoke(", "コメントが除去されていること");
    }

    [Fact]
    public void OnUnobservedTaskException_同期ディスパッチとモーダル表示を含まないこと()
    {
        var body = StripComments(ExtractMethodBody(ReadAppSource(), HandlerSignature));

        // 同期 Invoke はファイナライザスレッドを UI 処理の完了までブロックする
        body.Should().NotContain("Dispatcher.Invoke(",
            "ファイナライザスレッドからの同期ディスパッチは、モーダル表示が閉じられるまで" +
            "プロセス全体のファイナライザを停止させる（Issue #1742）");

        // ErrorDialogHelper.ShowError は内部で Dispatcher.Invoke（同期）＋ MessageBox.Show（モーダル）を行う
        body.Should().NotContain("ErrorDialogHelper", "モーダルダイアログ経路を使わない");
        body.Should().NotContain("MessageBox", "モーダルダイアログ経路を使わない");
    }

    [Fact]
    public void OnUnobservedTaskException_最初に観測済みにしてからハンドラへ委譲すること()
    {
        var body = StripComments(ExtractMethodBody(ReadAppSource(), HandlerSignature));

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
        var body = StripComments(ExtractMethodBody(ReadAppSource(), WiringSignature));

        // シャットダウンガード: Dispatcher 停止後のディスパッチは実行されず、例外の温床になるだけ
        body.Should().Contain("HasShutdownStarted", "アプリ終了中の発火に備えたガードを持つこと");
        body.Should().Contain("HasShutdownFinished", "アプリ終了後の発火に備えたガードを持つこと");

        // 非同期ディスパッチ: BeginInvoke はファイナライザスレッドをブロックしない
        body.Should().Contain("BeginInvoke", "UI スレッドへは非同期でポストすること");
        body.Should().NotContain("Dispatcher.Invoke(", "同期ディスパッチへ退行させない");

        // 非モーダル通知: トーストはユーザーの操作を要求せず、ファイナライザ停止の原因にならない
        body.Should().Contain("IToastNotificationService", "非モーダルなトースト通知を使うこと");
        body.Should().NotContain("ErrorDialogHelper", "モーダルダイアログ経路へ退行させない");
        body.Should().NotContain("MessageBox", "モーダルダイアログ経路へ退行させない");
    }
}
