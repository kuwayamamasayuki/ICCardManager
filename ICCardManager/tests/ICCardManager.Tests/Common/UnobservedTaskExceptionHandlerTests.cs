using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1742: <see cref="UnobservedTaskExceptionHandler"/> の挙動テスト。
/// </summary>
/// <remarks>
/// <para>
/// <c>TaskScheduler.UnobservedTaskException</c> はファイナライザスレッドで発火する。
/// ハンドラが同期ディスパッチ（<c>Dispatcher.Invoke</c>）でモーダルダイアログを開くと、
/// ユーザーが [OK] を押すまでプロセス全体のファイナライザが停止し、
/// ハンドラから例外が漏れるとプロセスが異常終了する。
/// </para>
/// <para>
/// 本テストは抽出したハンドラ本体の 2 つの性質を固定する:
/// ①通知は「UI スレッドへの非同期ポスト」経由でのみ実行される（呼び出しスレッドをブロックしない）、
/// ②ハンドラおよびポストしたアクションから例外が一切漏れない。
/// <c>App.xaml.cs</c> 側の配線（シャットダウンガード・BeginInvoke・トースト通知）は
/// <see cref="AppUnobservedTaskExceptionConventionTests"/> が静的検証で固定する。
/// </para>
/// </remarks>
public class UnobservedTaskExceptionHandlerTests
{
    private readonly Mock<ILogger> _loggerMock = new();
    private readonly List<Action> _postedActions = new();
    private readonly List<Exception> _notifiedExceptions = new();

    private UnobservedTaskExceptionHandler CreateHandler(
        Func<ILogger?>? getLogger = null,
        Func<bool>? isUiAvailable = null,
        Action<Action>? postToUi = null,
        Action<Exception>? notifyError = null)
    {
        return new UnobservedTaskExceptionHandler(
            getLogger ?? (() => _loggerMock.Object),
            isUiAvailable ?? (() => true),
            postToUi ?? _postedActions.Add,
            notifyError ?? _notifiedExceptions.Add);
    }

    private void VerifyErrorLogged(Exception expected, string because)
    {
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.Is<Exception>(e => ReferenceEquals(e, expected)),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            because);
    }

    [Fact]
    public void Handle_通知はUIスレッドへの非同期ポスト経由でのみ実行され最初の内部例外が渡されること()
    {
        var inner1 = new InvalidOperationException("1件目");
        var inner2 = new InvalidOperationException("2件目");
        var handler = CreateHandler();

        handler.Handle(new AggregateException(inner1, inner2));

        // ポストしたアクションを実行するまで通知は行われない
        // （＝呼び出しスレッド上で同期的に通知していないことの表明）
        _notifiedExceptions.Should().BeEmpty(
            "通知はポストされたアクションの中でのみ実行されるべき（呼び出しスレッドをブロックしない）");
        _postedActions.Should().ContainSingle();

        _postedActions[0]();

        _notifiedExceptions.Should().ContainSingle()
            .Which.Should().BeSameAs(inner1, "従来実装と同じく最初の内部例外を表示対象とする");
    }

    [Fact]
    public void Handle_UIが利用できない場合は通知をポストしないこと()
    {
        var handler = CreateHandler(isUiAvailable: () => false);

        handler.Handle(new AggregateException(new InvalidOperationException()));

        _postedActions.Should().BeEmpty(
            "Dispatcher がシャットダウン済みの場合にポストしても実行されず、例外の温床になるだけ");
    }

    [Fact]
    public void Handle_集約例外と全ての内部例外をログへ記録すること()
    {
        var inner1 = new InvalidOperationException("1件目");
        var inner2 = new TimeoutException("2件目");
        var aggregate = new AggregateException(inner1, inner2);
        var handler = CreateHandler();

        handler.Handle(aggregate);

        VerifyErrorLogged(aggregate, "集約例外そのものを記録しないと未観測例外の発生自体を追跡できない");
        VerifyErrorLogged(inner1, "内部例外を記録しないと障害の原因を特定できない");
        VerifyErrorLogged(inner2, "2件目以降の内部例外も等しく記録する");
    }

    [Fact]
    public void Handle_通知処理の例外はポストしたアクションの外へ漏れずログへ記録されること()
    {
        var notifyException = new InvalidOperationException("トースト表示失敗");
        var handler = CreateHandler(notifyError: _ => throw notifyException);

        handler.Handle(new AggregateException(new TimeoutException()));

        _postedActions.Should().ContainSingle();
        var act = () => _postedActions[0]();

        // Dispatcher 上で実行されるアクションから例外が漏れると
        // DispatcherUnhandledException へ波及し、二次エラーダイアログが出る
        act.Should().NotThrow();
        VerifyErrorLogged(notifyException, "通知の失敗も無言で握りつぶさない");
    }

    [Fact]
    public void Handle_UIディスパッチの例外はHandleの外へ漏れずログへ記録されること()
    {
        var postException = new InvalidOperationException("ディスパッチ失敗");
        var handler = CreateHandler(postToUi: _ => throw postException);

        var act = () => handler.Handle(new AggregateException(new TimeoutException()));

        // ファイナライザスレッドへ例外が漏れるとプロセスが異常終了する
        act.Should().NotThrow();
        VerifyErrorLogged(postException, "ディスパッチの失敗も無言で握りつぶさない");
    }

    [Fact]
    public void Handle_UI可用性判定の例外はHandleの外へ漏れずログへ記録されること()
    {
        var guardException = new InvalidOperationException("判定失敗");
        var handler = CreateHandler(isUiAvailable: () => throw guardException);

        var act = () => handler.Handle(new AggregateException(new TimeoutException()));

        act.Should().NotThrow();
        VerifyErrorLogged(guardException, "ガード判定の失敗も無言で握りつぶさない");
    }

    [Fact]
    public void Handle_ロガー取得が失敗しても通知は行われること()
    {
        var handler = CreateHandler(getLogger: () => throw new InvalidOperationException("ロガー未初期化"));

        var act = () => handler.Handle(new AggregateException(new TimeoutException()));

        // SetupGlobalExceptionHandlers は DI コンテナ構築前に呼ばれるため、
        // ロガーが取得できない時期にも発火し得る。ログ経路の障害が通知経路を道連れにしない
        act.Should().NotThrow();
        _postedActions.Should().ContainSingle("ログに残せなくてもユーザーへの通知は行うべき");
    }

    [Fact]
    public void Handle_例外がnullの場合は通知もポストも行わず例外を漏らさないこと()
    {
        var handler = CreateHandler();

        var act = () => handler.Handle(null!);

        act.Should().NotThrow();
        _postedActions.Should().BeEmpty();
        _notifiedExceptions.Should().BeEmpty();
    }
}
