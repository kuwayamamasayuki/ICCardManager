using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="ExistingInstanceLocator"/> の単体テスト（Issue #1910）
/// </summary>
/// <remarks>
/// 「どのプロセスを前面へ出すか」の判断だけを純関数へ切り出しているため、
/// 実際に複数プロセスを起動せずに選択規則を網羅できる。
/// </remarks>
public class ExistingInstanceLocatorTests
{
    private const int CurrentProcessId = 100;
    private const int CurrentSessionId = 1;

    private static InstanceWindowCandidate Candidate(int processId, int sessionId, int windowHandle)
        => new(processId, sessionId, new IntPtr(windowHandle));

    private static InstanceWindowCandidate? Select(params InstanceWindowCandidate[] candidates)
        => ExistingInstanceLocator.SelectActivationTarget(candidates, CurrentProcessId, CurrentSessionId);

    [Fact]
    public void 同一セッションの起動済みインスタンスを選ぶこと()
    {
        var target = Select(
            Candidate(CurrentProcessId, CurrentSessionId, 0x1111),
            Candidate(200, CurrentSessionId, 0x2222));

        target.Should().NotBeNull();
        target!.Value.ProcessId.Should().Be(200);
        target.Value.MainWindowHandle.Should().Be(new IntPtr(0x2222));
    }

    [Fact]
    public void 自分自身は選ばないこと()
    {
        // 自プロセスはウィンドウを持つ（起動途中でも Application が生成する）ため、
        // 除外しないと「自分を前面に出して成功した」と誤判定して案内も出せなくなる。
        var target = Select(Candidate(CurrentProcessId, CurrentSessionId, 0x1111));

        target.Should().BeNull();
    }

    [Fact]
    public void 別セッションのインスタンスは選ばないこと()
    {
        // SetForegroundWindow はセッションをまたげない。選ぶと「前面化に成功した」ことにして
        // 案内も出さない無言の失敗になる。
        var target = Select(Candidate(200, CurrentSessionId + 1, 0x2222));

        target.Should().BeNull();
    }

    [Fact]
    public void メインウィンドウが未生成のインスタンスは選ばないこと()
    {
        // 先行インスタンスが起動直後で画面をまだ出していない場合。
        var target = Select(Candidate(200, CurrentSessionId, 0));

        target.Should().BeNull();
    }

    [Fact]
    public void 候補が複数あるときはプロセスIDの小さい先行インスタンスを選ぶこと()
    {
        var target = Select(
            Candidate(300, CurrentSessionId, 0x3333),
            Candidate(200, CurrentSessionId, 0x2222),
            Candidate(400, CurrentSessionId, 0x4444));

        target!.Value.ProcessId.Should().Be(200);
    }

    [Fact]
    public void 除外条件は重ねて評価されること()
    {
        // 「別セッションだがウィンドウを持つ」「同一セッションだがウィンドウが無い」を
        // 同時に与えても、どちらも選ばれないことを固定する。
        var target = Select(
            Candidate(200, CurrentSessionId + 1, 0x2222),
            Candidate(300, CurrentSessionId, 0));

        target.Should().BeNull();
    }

    [Fact]
    public void 候補が空またはnullなら選ばれないこと()
    {
        ExistingInstanceLocator.SelectActivationTarget(
            new List<InstanceWindowCandidate>(), CurrentProcessId, CurrentSessionId)
            .Should().BeNull();

        ExistingInstanceLocator.SelectActivationTarget(null, CurrentProcessId, CurrentSessionId)
            .Should().BeNull();
    }
}
