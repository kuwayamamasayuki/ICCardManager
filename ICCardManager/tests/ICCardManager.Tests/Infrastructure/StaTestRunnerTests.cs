using System;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// <see cref="StaTestRunner"/> の検証（Issue #2083）。
/// </summary>
/// <remarks>
/// 旧実装（2 箇所に複製されていた <c>RunOnSta</c>）は、打ち切られたときに
/// 「STA スレッドが時間内に完了すること」としか残さなかったため、
/// ログだけを見た人が検証対象の不具合と読み違えた。
/// ここでは「STA で走ること」「例外がそのまま伝わること」に加えて、
/// <b>打ち切り時に何が残るか</b>を固定する。
/// </remarks>
[Collection(StaThreadCollection.Name)]
public class StaTestRunnerTests
{
    [Fact]
    public void アクションがSTAのバックグラウンドスレッドで実行されること()
    {
        ApartmentState? apartment = null;
        bool? isBackground = null;
        var ranOnCallerThread = true;
        var callerThreadId = Thread.CurrentThread.ManagedThreadId;

        StaTestRunner.Run(() =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            isBackground = Thread.CurrentThread.IsBackground;
            ranOnCallerThread = Thread.CurrentThread.ManagedThreadId == callerThreadId;
        });

        apartment.Should().Be(ApartmentState.STA, "WPF の Window / FlowDocument は STA でしか扱えない");
        isBackground.Should().BeTrue("ハングしてもプロセスの終了を妨げないこと");
        ranOnCallerThread.Should().BeFalse("呼び出し元スレッド（MTA）で実行してはならない");
    }

    [Fact]
    public void アクションの例外を呼び出し元へスタックトレース付きで伝播すること()
    {
        Action act = () => StaTestRunner.Run(() => throw new InvalidOperationException("検証対象の失敗"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("検証対象の失敗")
            .Which.StackTrace.Should().Contain(nameof(アクションの例外を呼び出し元へスタックトレース付きで伝播すること),
                "ExceptionDispatchInfo で投げ直し、STA スレッド上の発生位置を失わないこと");
    }

    [Fact]
    public void 打ち切り時の失敗メッセージに経過時間と完了した段階が残ること()
    {
        using var release = new ManualResetEventSlim(false);
        Exception? failure = null;

        try
        {
            StaTestRunner.Run(
                stages =>
                {
                    stages.Complete("Window の生成");
                    release.Wait(TimeSpan.FromSeconds(30));
                },
                TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            release.Set();
        }

        failure.Should().NotBeNull("上限を超えたら打ち切って失敗させること");
        failure!.Message.Should().Contain("Window の生成", "どの段階まで進んでいたかを残すこと");
        failure.Message.Should().Contain("秒で打ち切り", "実測の経過時間を残すこと");
        failure.Message.Should().Contain("検証対象の不具合を意味しない",
            "ログだけを見た人が検証対象の故障と読み違えないこと");
    }

    [Fact]
    public void 打ち切り時に段階が1つも完了していなければその旨を示すこと()
    {
        var reason = StaTestRunner.BuildTimeoutReason(
            TimeSpan.FromSeconds(31.4), StaTestRunner.DefaultCompletionTimeout, new StaStageLog());

        reason.Should().Contain("（1 段階も完了していない）",
            "段階が空のときに「完了した段階: 」で途切れないこと");
        reason.Should().Contain("31.4", "経過時間は小数第 1 位まで残す");
        reason.Should().Contain("30 秒以内", "上限そのものも併記する（値を増やす前に競合を疑わせるため）");
    }

    [Fact]
    public void 完了した段階は完了順に連結されること()
    {
        var log = new StaStageLog();
        log.Complete("Window の生成");
        log.Complete("6 メソッドの呼び出し");

        log.CompletedStages.Should().Equal("Window の生成", "6 メソッドの呼び出し");
        log.Describe().Should().Be("Window の生成 → 6 メソッドの呼び出し");
    }

    [Fact]
    public void 段階名が空なら記録を拒否すること()
    {
        var log = new StaStageLog();

        Action act = () => log.Complete("   ");

        act.Should().Throw<ArgumentException>()
            .WithMessage("段階名が空です。*", "空の段階名は診断の役に立たない");
    }

    [Fact]
    public void アクション未指定なら実行前に拒否すること()
    {
        Action act = () => StaTestRunner.Run((Action)null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
