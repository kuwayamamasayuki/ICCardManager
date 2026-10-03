using System.Diagnostics;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Tests.Infrastructure;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2225: スレッドプールが同期的な待ちで塞がっていても、UNC の到達確認が到達できる共有を
/// 「到達できる」と判定すること。
/// </summary>
/// <remarks>
/// <para>
/// 到達確認は以前 <c>Task.Run</c>（スレッドプール）で起動し、上限（5 秒）付きで同期的に待っていた。
/// プールが塞がっていると確認は空きが出るまで始まらず、到達できる共有でも上限に達して
/// 「ネットワーク共有に到達できません」と誤報し得た（#2213 の DB の疎通確認と同じ形）。
/// <c>ValidateBackupPathAsync</c> は検証全体をプールで走らせるため、プールのスレッドがプールの確認を待つ入れ子にもなっていた。
/// </para>
/// <para>
/// プールを塞ぐため、他のテストと並列に走らせない（<see cref="ThreadPoolSaturationCollection"/>）。
/// 確認の本体は即座に true を返すものへ差し替え、起動の遅れだけを見る。
/// </para>
/// </remarks>
[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class PathValidatorUncReachabilityThreadPoolTests
{
    /// <summary>確認の上限（本番の既定値と同じ 5 秒）。</summary>
    private const int TimeoutMs = PathValidator.DefaultUncTimeoutMs;

    /// <summary>
    /// 塞がったと判定した後に足す予備の塞ぎ役の数。上限（5 秒）の間に足されるスレッド（約 10 本）を十分に上回る数を並べる
    /// （余裕が少ないと修正前の Task.Run の確認が上限内に始まって緑になる）。
    /// </summary>
    private const int ReserveBlockers = 40;

    [Fact]
    public void 到達確認はスレッドプールが塞がっていても到達できる共有を到達できると判定すること()
    {
        // Arrange
        var checker = PathValidator.CreateUncReachabilityChecker(_ => true);
        using var saturator = ThreadPoolSaturator.Saturate(ReserveBlockers);
        saturator.IsSaturated.Should().BeTrue(
            "前提: プールが塞がっていて、Task.Run の処理が始まらないこと（塞げていなければ本テストは何も検証しない）");

        // Act
        var stopwatch = Stopwatch.StartNew();
        var reachable = checker(@"\\server\share", TimeoutMs);
        stopwatch.Stop();

        // Assert
        reachable.Should().BeTrue("確認は専用スレッドで走るため、プールの空きを待たずに始まる（Issue #2225）");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(TimeoutMs);

        // 対の表明: 確認を待っている間もプールは塞がったままだった
        saturator.SentinelStarted.Should().BeFalse("確認を待っている間も、プールが塞がったままであること");
    }
}
