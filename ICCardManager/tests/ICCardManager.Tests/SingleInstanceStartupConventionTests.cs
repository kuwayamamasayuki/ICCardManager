using System.IO;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// 二重起動防止が起動シーケンスへ正しく配線されていることの静的検査（Issue #1910）
/// </summary>
/// <remarks>
/// <para>
/// <c>App.OnStartup</c> は WPF の <c>Application</c> のライフサイクルに載っており、
/// xUnit（MTA・<c>Application.Current</c> 不在）から実行できない。
/// 配線の regression は挙動テストでは固定できないため、ソーステキストで表明する。
/// </para>
/// <para>
/// 「禁止された形の不在」だけでなく「正しい形の存在」も対で検査する。
/// 不在だけを見ると、判定ごと消した実装でも緑になる
/// （<c>SafeRollbackConventionTests</c> / <c>SqliteConnectionStringConventionTests</c> と同じ方針）。
/// </para>
/// </remarks>
public class SingleInstanceStartupConventionTests
{
    private static string ReadAppSource()
        => File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs"));

    private static string ReadCodeOnlyAppSource()
        => TestSourceInspection.ToCodeOnlyPreservingLines(ReadAppSource());

    [Fact]
    public void 起動時に二重起動の判定を行っていること()
    {
        var code = ReadCodeOnlyAppSource();

        code.Should().Contain("TryAcquireSingleInstanceLock()",
            "OnStartup が二重起動を判定していること");
        code.Should().Contain("SingleInstanceGuard.Acquire(AppConstants.SingleInstanceMutexName)",
            "判定は AppConstants の固定名で行うこと（名前を変えると旧版との間で二重起動できる）");
    }

    [Fact]
    public void 二重起動を検出したら起動を中止すること()
    {
        var code = ReadCodeOnlyAppSource();
        var body = TestSourceInspection.ExtractMethodBody(code, "protected override async void OnStartup");

        body.Should().Contain("if (!TryAcquireSingleInstanceLock())",
            "判定結果を握りつぶさないこと");
        body.Should().Contain("Shutdown(0);",
            "二重起動時は異常終了（Shutdown(1)）ではなく正常終了として終わること");
    }

    [Fact]
    public void 一時テンプレートの回収は二重起動の判定より後に行うこと()
    {
        // TemplateResolver.CleanupTempFiles は %TEMP% の ICCardManager_Template_*.xlsx を
        // 消すため、判定より前に呼ぶと 2 つ目のプロセスが「起動中の 1 つ目が帳票作成に
        // 使用中の一時ファイル」を削除する。判定の前に破壊的な処理を置かないこと。
        var code = ReadCodeOnlyAppSource();

        var guardIndex = code.IndexOf("TryAcquireSingleInstanceLock()", System.StringComparison.Ordinal);
        var cleanupIndex = code.IndexOf("TemplateResolver.CleanupTempFiles()", System.StringComparison.Ordinal);

        guardIndex.Should().BeGreaterThan(0, "判定の呼び出しが見つかること（検査が空振りしていないこと）");
        cleanupIndex.Should().BeGreaterThan(0, "回収の呼び出しが見つかること（検査が空振りしていないこと）");
        cleanupIndex.Should().BeGreaterThan(guardIndex,
            "一時テンプレートの回収は二重起動の判定より後に置くこと");
    }

    [Fact]
    public void 起動を中止すると決めた時点でミューテックスを解放していること()
    {
        // 案内ダイアログの表示中は職員が閉じるまでブロックする。その間ハンドルを
        // 握ったままだと、先行インスタンスが終了してもカーネルオブジェクトが
        // こちらのハンドルで生き残り、次の起動が「既に起動しています」と誤判定される。
        var code = ReadCodeOnlyAppSource();
        var body = TestSourceInspection.ExtractMethodBody(code, "private bool TryAcquireSingleInstanceLock");

        var disposeIndex = body.IndexOf("_singleInstanceGuard.Dispose()", System.StringComparison.Ordinal);
        var noticeIndex = body.IndexOf("OwnedMessageBox.Show(", System.StringComparison.Ordinal);

        disposeIndex.Should().BeGreaterThan(0, "中止経路で解放していること（検査が空振りしていないこと）");
        noticeIndex.Should().BeGreaterThan(0, "案内の表示が見つかること（検査が空振りしていないこと）");
        disposeIndex.Should().BeLessThan(noticeIndex, "解放は案内ダイアログの表示より前に行うこと");
    }

    [Fact]
    public void 案内文言は前面化の結果で選ぶこと()
    {
        // ミューテックスの Status は DACL の所有者（別ユーザーか）しか表さない。
        // 同じユーザーの 2 セッション（リモートデスクトップ・簡易切り替え）では
        // 「タスクバーで切り替えてください」がこのセッションで実行できない指示になる。
        var code = ReadCodeOnlyAppSource();
        var body = TestSourceInspection.ExtractMethodBody(code, "private bool TryAcquireSingleInstanceLock");

        body.Should().Contain("ActivationOutcome.ActivationRefused",
            "同一セッションに切り替え先がある場合を区別すること");
        body.Should().Contain("SingleInstanceNotice.BuildWindowNotFoundMessage()",
            "切り替え先が見つからない場合に専用の案内を出すこと");
    }

    [Fact]
    public void 終了時にミューテックスを解放していること()
    {
        var code = ReadCodeOnlyAppSource();
        var body = TestSourceInspection.ExtractMethodBody(code, "protected override void OnExit");

        body.Should().Contain("_singleInstanceGuard?.Dispose()",
            "正常終了では明示的に解放し、次の起動を待たせないこと");
    }
}
