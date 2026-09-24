using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Tests.Infrastructure;
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

    /// <remarks>
    /// Issue #2106: 旧版は <c>faulted.Exception</c> / <c>IsFaulted</c>（入力 Task 自身の性質）しか
    /// 見ておらず、<c>Observe</c> の本体を空にしても緑だった。しかも <c>Task.Exception</c> の読み取りは
    /// それ自体が例外を観測済みにするため、テストが検証対象の効果を自分で作っていた。
    /// ここでは入力 Task へ一切触れずに手放し、GC とファイナライザを回して
    /// <c>TaskScheduler.UnobservedTaskException</c> が「その例外について」発火しないことを観測する。
    /// 同じ GC の中で、観測されない対照の Task が実際に発火することも表明する
    /// （対照が発火しないなら、GC が回っていないだけで何も検証していない）。
    /// </remarks>
    [Fact]
    public void Observe_例外を観測済みにしてUnobservedTaskExceptionを発生させないこと()
    {
        // Arrange
        var (log, sink) = CreateSink();

        using var monitor = new UnobservedTaskExceptionMonitor();

        // Act: Task への参照は下請けメソッドの中だけに閉じ、テスト側からは例外だけを持つ
        var observedException = CreateFaultedTaskAndObserve(sink);
        var controlException = UnobservedTaskExceptionMonitor.CreateAbandonedFaultedTask();

        monitor.CollectUntilRaised(controlException);

        // Assert: 未観測のままだと GC 契機で TaskScheduler.UnobservedTaskException が発火し、
        // App.xaml.cs のハンドラが操作と無関係なタイミングでダイアログを表示してしまう
        monitor.WasRaised(controlException).Should().BeTrue(
            "対照（Observe へ渡さない失敗 Task）は発火するはず。発火しないなら GC が回っておらず本テストは何も検証していない");
        monitor.WasRaised(observedException).Should().BeFalse(
            "Observe へ渡した失敗 Task の例外は観測済みになり、UnobservedTaskException を発生させない");
        log.Should().ContainSingle().Which.Exception.Should().BeSameAs(observedException);
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

    /// <remarks>
    /// Issue #2106: 記録は <c>ContinueWith</c> の中で走るため、そこで投げた例外は
    /// 呼び出し元へは届かず<b>継続の Task に閉じ込められる</b>。旧版の <c>NotThrow</c> は
    /// try/catch を消しても緑だった。閉じ込められた例外は誰も観測しないので、
    /// GC 契機で <c>UnobservedTaskException</c> として（操作と無関係なタイミングで）表面化する。
    /// ここではその発火が「記録の失敗で投げた例外について」起きないことを観測する。
    /// </remarks>
    [Fact]
    public void Observe_記録そのものが失敗しても例外を漏らさないこと()
    {
        // Arrange: ログ出力自体も失敗し得る（ファイルログの書き込み失敗等）。
        // ここで二次例外を漏らすと、本クラスが防いでいるはずの「無言の失敗」を
        // このクラス自身が作ることになる（development-conventions.md Issue #1745）。
        using var monitor = new UnobservedTaskExceptionMonitor();
        var loggingFailure = new UnauthorizedAccessException("ログファイルへ書き込めません");
        var sinkCalls = 0;

        // Act
        Action act = () => CreateFaultedTaskAndObserve((_, _) =>
        {
            sinkCalls++;
            throw loggingFailure;
        });
        act.Should().NotThrow();
        var controlException = UnobservedTaskExceptionMonitor.CreateAbandonedFaultedTask();

        monitor.CollectUntilRaised(controlException);

        // Assert
        sinkCalls.Should().Be(1, "記録先が実際に呼ばれて失敗したこと（呼ばれていなければ本テストは何も検証していない）");
        monitor.WasRaised(controlException).Should().BeTrue(
            "対照は発火するはず。発火しないなら GC が回っておらず本テストは何も検証していない");
        monitor.WasRaised(loggingFailure).Should().BeFalse(
            "記録の失敗を継続の中で握りつぶさないと、継続の Task が未観測の例外を抱えて GC 契機で発火する");
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

    /// <summary>
    /// 失敗 Task を作って <c>Observe</c> へ渡し、Task への参照を残さずに例外だけを返す。
    /// </summary>
    /// <remarks>
    /// 呼び出し側のスタックに Task が残ると GC で回収されず、ファイナライザが走らない。
    /// インライン化されると同じ理由で参照が延命され得るため禁止する。
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CreateFaultedTaskAndObserve(Action<Exception, string> sink)
    {
        var exception = new InvalidOperationException("observed-" + Guid.NewGuid().ToString("N"));
        DispatcherObservation.Observe(Task.FromException(exception), "職員証の認証", sink);
        return exception;
    }
}
