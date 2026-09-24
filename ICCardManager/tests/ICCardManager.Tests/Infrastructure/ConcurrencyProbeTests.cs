using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// <see cref="ConcurrencyProbe"/> のテスト（Issue #2108 で共通化）。
/// </summary>
/// <remarks>
/// 利用側は「最大同時実行数が 1 であること」を表明する。計測器が重なりを数えられなくなると
/// その表明は常に緑になるので、重なったときに 2 以上を数えることを対で固定する。
/// </remarks>
public class ConcurrencyProbeTests
{
    [Fact]
    public async Task 直列に呼ぶと最大同時実行数は1であること()
    {
        var probe = new ConcurrencyProbe();

        (await probe.TrackAsync(1)).Should().Be(1);
        (await probe.TrackAsync("a")).Should().Be("a");

        probe.MaxConcurrentCalls.Should().Be(1);
    }

    [Fact]
    public async Task 並列に呼ぶと重なった数を数えること()
    {
        var probe = new ConcurrencyProbe();

        var results = await Task.WhenAll(probe.TrackAsync(1), probe.TrackAsync(2), probe.TrackAsync(3));

        results.Should().Equal(1, 2, 3);
        // 3 件とも保持区間へ同期的に入るので通常は 3 になるが、呼び出し元のスレッドが保持時間（20ms）以上
        // 横取りされると 1 件目が先に抜け得る。守りたいのは「重なりを 2 以上として数えられる」ことなので下限で見る
        probe.MaxConcurrentCalls.Should().BeGreaterThan(1,
            "Task.WhenAll で同時に起動した呼び出しは保持区間が重なるので、1 を超えて数えること");
    }
}
