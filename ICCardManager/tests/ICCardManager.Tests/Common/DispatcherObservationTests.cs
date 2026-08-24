using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1873: View コードビハインドが <c>Dispatcher</c> へ投げた処理の例外を
/// 必ず観測してログへ残すことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// <c>InvokeAsyncObserved</c> 本体は <c>Dispatcher</c>（STA 依存）を必要とし xUnit から駆動できないため、
/// 観測ロジックだけを <c>internal</c> の <c>Observe</c> に切り出して検証する
/// （development-conventions.md Issue #1794「判断を純関数へ切り出す」と同じ形）。
/// 継続は <c>TaskContinuationOptions.ExecuteSynchronously</c> で登録されるため、
/// 完了済み Task を渡せば同期的に記録される（待機不要で決定論的）。
/// </para>
/// <para>
/// 「継ぎ目を通っているか」（View が実際にこのヘルパーを使っているか）は
/// <c>CardReadDispatchConventionTests</c> がソーステキストの静的検査で固定する。
/// </para>
/// </remarks>
public class DispatcherObservationTests
{
    private static (List<(Exception Exception, string Operation)> Log, Action<Exception, string> Sink) CreateSink()
    {
        var log = new List<(Exception, string)>();
        return (log, (ex, op) => log.Add((ex, op)));
    }

    [Fact]
    public void Observe_失敗したTaskの例外を操作名つきで記録すること()
    {
        // Arrange
        var (log, sink) = CreateSink();
        var faulted = Task.FromException(new InvalidOperationException("database is locked"));

        // Act
        DispatcherObservation.Observe(faulted, "職員証の認証", sink);

        // Assert: 障害調査を先に進めるため、例外そのものと操作名の両方が要る
        // （development-conventions.md Issue #1730）
        log.Should().HaveCount(1);
        log[0].Operation.Should().Be("職員証の認証");
        // Task.Exception が返す AggregateException は一度も throw されていないため StackTrace が null。
        // そのまま記録すると ErrorDialogHelper のログには空のスタックトレースと SYS999 しか残らないため、
        // 実際の失敗要因まで解いてから記録すること。
        log[0].Exception.Should().BeOfType<InvalidOperationException>();
        log[0].Exception!.Message.Should().Be("database is locked");
    }

    [Fact]
    public void Observe_成功したTaskではログを出さないこと()
    {
        // Arrange
        var (log, sink) = CreateSink();

        // Act
        DispatcherObservation.Observe(Task.CompletedTask, "職員証の認証", sink);

        // Assert: 正常時に毎回記録するとカードタッチのたびにログが肥大化する
        log.Should().BeEmpty();
    }

    [Fact]
    public void Observe_例外を観測済みにしてUnobservedTaskExceptionを発生させないこと()
    {
        // Arrange
        var (_, sink) = CreateSink();
        var faulted = Task.FromException(new InvalidOperationException("boom"));

        // Act
        DispatcherObservation.Observe(faulted, "職員証の認証", sink);

        // Assert: 未観測のままだと GC 契機で TaskScheduler.UnobservedTaskException が発火し、
        // App.xaml.cs のハンドラが操作と無関係なタイミングでダイアログを表示してしまう
        faulted.Exception.Should().NotBeNull();
        faulted.IsFaulted.Should().BeTrue();
    }

    [Fact]
    public void Observe_nullのTaskを渡しても例外を投げないこと()
    {
        // Arrange: Dispatcher 操作が行われない環境では null が渡り得る
        var (log, sink) = CreateSink();

        // Act
        Action act = () => DispatcherObservation.Observe(null, "職員証の認証", sink);

        // Assert
        act.Should().NotThrow();
        log.Should().BeEmpty();
    }

    [Fact]
    public void Observe_記録そのものが失敗しても例外を漏らさないこと()
    {
        // Arrange: ログ出力自体も失敗し得る（ファイルログの書き込み失敗等）。
        // ここで二次例外を漏らすと、本クラスが防いでいるはずの「無言の失敗」を
        // このクラス自身が作ることになる（development-conventions.md Issue #1745）。
        var faulted = Task.FromException(new InvalidOperationException("boom"));

        // Act
        Action act = () => DispatcherObservation.Observe(
            faulted,
            "職員証の認証",
            (_, _) => throw new UnauthorizedAccessException("ログファイルへ書き込めません"));

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Observe_記録先がnullなら例外を投げること()
    {
        // Arrange: 記録先の省略は「観測しているつもりで無言」を生む。
        // 既定値で黙って通さず、呼び出し側の誤りとして落とす（Issue #1820）
        Action act = () => DispatcherObservation.Observe(Task.CompletedTask, "職員証の認証", null);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void InvokeAsyncObserved_dispatcherがnullなら例外を投げること()
    {
        // Arrange & Act
        Action syncOverload = () =>
            DispatcherObservation.InvokeAsyncObserved(null, () => { }, "職員証の認証");
        Action asyncOverload = () =>
            DispatcherObservation.InvokeAsyncObserved(null, () => Task.CompletedTask, "職員証の認証");

        // Assert
        syncOverload.Should().Throw<ArgumentNullException>();
        asyncOverload.Should().Throw<ArgumentNullException>();
    }
}
