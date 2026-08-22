using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// Issue #1819: ヘルスチェック失敗を本番ログへ残すかの判定を検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前はヘルスチェックの catch が <c>LogTrace</c> のみで、
/// <c>appsettings.json</c> の <c>Logging:LogLevel:Default = Information</c> により
/// 本番のログファイルには一切出力されなかった。購読者例外（<c>MainViewModel</c> の
/// 接続状態ハンドラー）で UI 警告の更新が毎回失敗しても痕跡がゼロになる。
/// </para>
/// <para>
/// ヘルスチェックは 10 秒周期で走るため毎回 Warning を出すとログが肥大化する。
/// 「発生した事実」（初回）と「続いている事実」（一定間隔）だけを残す判定を
/// 実機 felicalib に依存しない純粋関数として検証する。
/// </para>
/// </remarks>
public class FelicaCardReaderHealthCheckLoggingTests
{
    [Fact]
    public void ShouldLogHealthCheckFailure_初回は出力すること()
    {
        FelicaCardReader.ShouldLogHealthCheckFailure(1).Should().BeTrue(
            "失敗が始まった事実は必ず本番ログへ残す");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(29)]
    public void ShouldLogHealthCheckFailure_間隔未満の連続失敗は出力しないこと(int count)
    {
        FelicaCardReader.ShouldLogHealthCheckFailure(count).Should().BeFalse(
            "10 秒周期で毎回出すとログが肥大化し、他の事象が埋もれる");
    }

    [Theory]
    [InlineData(FelicaCardReader.HealthCheckFailureLogIntervalCount)]
    [InlineData(FelicaCardReader.HealthCheckFailureLogIntervalCount * 2)]
    [InlineData(FelicaCardReader.HealthCheckFailureLogIntervalCount * 10)]
    public void ShouldLogHealthCheckFailure_一定間隔ごとに出力すること(int count)
    {
        FelicaCardReader.ShouldLogHealthCheckFailure(count).Should().BeTrue(
            "失敗が続いている事実も残さないと、いつまで継続したか分からない");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldLogHealthCheckFailure_失敗していない場合は出力しないこと(int count)
    {
        FelicaCardReader.ShouldLogHealthCheckFailure(count).Should().BeFalse(
            "失敗回数 0 以下（＝失敗していない）で出力すると、正常時にも警告が出る");
    }

    [Fact]
    public void HealthCheckFailureLogIntervalCount_10秒周期で数分に1回の水準であること()
    {
        // 10 秒周期 × 30 回 = 300 秒（5 分）。
        // 短すぎるとログが肥大化し、長すぎると継続中の障害を見落とす。
        FelicaCardReader.HealthCheckFailureLogIntervalCount.Should().BeInRange(6, 360,
            "ヘルスチェック周期 10 秒に対して 1 分～1 時間に 1 回の水準に収める");
    }
}
