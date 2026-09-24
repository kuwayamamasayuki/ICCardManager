using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Tests.Infrastructure;
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

    /// <remarks>
    /// Issue #2106: 旧版は <c>faulted.Exception</c> / <c>IsFaulted</c>（入力 Task 自身の性質）しか見ておらず、
    /// しかも <c>Task.Exception</c> の読み取りはそれ自体が例外を観測済みにするため、
    /// <c>ObserveTask</c> の本体を空にしても緑だった。Task へ一切触れずに手放し、GC 後の
    /// <see cref="TaskScheduler.UnobservedTaskException"/> がその例外について発火しないことを、
    /// 観測しない対照の Task が発火することと対で観測する（<c>DispatcherObservationTests</c> と同じ方法）。
    /// </remarks>
    [Fact]
    public void ObserveTask_例外を観測済みにしてUnobservedTaskExceptionを発生させないこと()
    {
        // Arrange
        var sut = new WpfDispatcherService(new Mock<ILogger<WpfDispatcherService>>().Object);
        using var monitor = new UnobservedTaskExceptionMonitor();

        // Act: Task への参照は下請けメソッドの中だけに閉じ、テスト側からは例外だけを持つ
        var observedException = CreateFaultedTaskAndObserve(sut);
        var controlException = UnobservedTaskExceptionMonitor.CreateAbandonedFaultedTask();

        monitor.CollectUntilRaised(controlException);

        // Assert: 未観測のままだと GC 契機で TaskScheduler.UnobservedTaskException が発火し、
        // App.xaml.cs のハンドラが「バックグラウンド処理エラー」ダイアログを
        // 操作と無関係なタイミングで表示してしまう。
        monitor.WasRaised(controlException).Should().BeTrue(
            "対照（ObserveTask へ渡さない失敗 Task）は発火するはず。発火しないなら GC が回っておらず本テストは何も検証していない");
        monitor.WasRaised(observedException).Should().BeFalse(
            "ObserveTask へ渡した失敗 Task の例外は観測済みになり、UnobservedTaskException を発生させない");
    }

    /// <summary>
    /// 失敗 Task を作って <c>ObserveTask</c> へ渡し、Task への参照を残さずに例外だけを返す。
    /// </summary>
    /// <remarks>
    /// 呼び出し側のスタックに Task が残ると GC で回収されず、ファイナライザが走らない。
    /// インライン化されると同じ理由で参照が延命され得るため禁止する。
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CreateFaultedTaskAndObserve(WpfDispatcherService sut)
    {
        var exception = new InvalidOperationException("observed-" + Guid.NewGuid().ToString("N"));
        sut.ObserveTask(Task.FromException(exception));
        return exception;
    }
}
