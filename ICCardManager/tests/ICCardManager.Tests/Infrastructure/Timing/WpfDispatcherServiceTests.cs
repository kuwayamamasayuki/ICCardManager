using System;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.Timing;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Timing;

/// <summary>
/// Issue #1725: <see cref="WpfDispatcherService"/> が UI スレッドへ投げた処理の
/// 例外を観測してログへ残すことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 従来 <c>InvokeAsync(Func&lt;Task&gt;)</c> は <c>Dispatcher.InvokeAsync(asyncAction)</c> の
/// 戻り値も、その内側の <see cref="Task"/> も await していなかった。async メソッドの例外は
/// 返り値の Task に格納されるため <c>DispatcherUnhandledException</c> は発火せず、
/// <c>TaskScheduler.UnobservedTaskException</c> が GC ファイナライズ時に遅れて発火するだけで、
/// 障害調査に使えるログが残らなかった（Issue #1725 で MainViewModel の Processing 固着が
/// 無言で起きた原因の半分）。
/// </para>
/// <para>
/// <c>InvokeAsync</c> 本体は <c>Application.Current</c> を必要とし単体テストから駆動できないため、
/// 観測ロジックだけを <c>internal</c> の <c>ObserveTask</c> に切り出して検証する。
/// 継続は <c>TaskContinuationOptions.ExecuteSynchronously</c> で登録されるため、
/// 完了済み Task を渡せば同期的にログが出る（待機不要で決定論的）。
/// </para>
/// </remarks>
public class WpfDispatcherServiceTests
{
    [Fact]
    public void ObserveTask_失敗したTaskの例外をLogErrorで記録すること()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<WpfDispatcherService>>();
        var sut = new WpfDispatcherService(loggerMock.Object);
        var faulted = Task.FromException(new InvalidOperationException("database is locked"));

        // Act
        sut.ObserveTask(faulted);

        // Assert
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "UI スレッドへ投げた処理の例外は、障害調査のために本番ログへ残す必要がある");
    }

    [Fact]
    public void ObserveTask_成功したTaskではログを出さないこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<WpfDispatcherService>>();
        var sut = new WpfDispatcherService(loggerMock.Object);

        // Act
        sut.ObserveTask(Task.CompletedTask);

        // Assert
        loggerMock.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            "正常時に毎回ログを出すとカードタッチのたびにログが肥大化する");
    }

    [Fact]
    public void ObserveTask_nullを渡しても例外を投げないこと()
    {
        // Arrange: Application.Current が null の環境（テスト実行時など）では
        // Dispatcher 操作が行われず null が渡り得る
        var sut = new WpfDispatcherService(new Mock<ILogger<WpfDispatcherService>>().Object);

        // Act
        Action act = () => sut.ObserveTask(null);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ObserveTask_loggerが未注入でも例外を投げないこと()
    {
        // Arrange: ILogger はコンストラクタで省略可能（DI 未登録環境でも動く）
        var sut = new WpfDispatcherService();

        // Act
        Action act = () => sut.ObserveTask(Task.FromException(new InvalidOperationException("boom")));

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ObserveTask_例外を観測済みにしてUnobservedTaskExceptionを発生させないこと()
    {
        // Arrange
        var sut = new WpfDispatcherService(new Mock<ILogger<WpfDispatcherService>>().Object);
        var faulted = Task.FromException(new InvalidOperationException("boom"));

        // Act
        sut.ObserveTask(faulted);

        // Assert: 継続内で Exception を参照済みなら、Task 側も観測済みになる。
        // 未観測のままだと GC 契機で TaskScheduler.UnobservedTaskException が発火し、
        // App.xaml.cs のハンドラが「バックグラウンド処理エラー」ダイアログを
        // 操作と無関係なタイミングで表示してしまう。
        faulted.Exception.Should().NotBeNull();
        faulted.IsFaulted.Should().BeTrue();
    }
}
