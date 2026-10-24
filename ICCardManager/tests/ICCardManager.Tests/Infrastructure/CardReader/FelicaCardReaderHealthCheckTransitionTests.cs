using System.IO;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// Issue #2298: ヘルスチェックが接続状態をどう遷移させるかの判定と、
/// 起動時に PaSoRi が未接続でも接続の監視が始まることを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は <c>StartReadingAsync</c> が PaSoRi 未接続を検出すると（Issue #1428）、
/// ヘルスチェックタイマーを起動する前に <c>return</c> していた。起動後に抜き差しした場合は
/// ヘルスチェックが自動で復帰させるのに、<b>起動時に未接続だった場合だけ</b>「再接続」ボタンを
/// 押すまで認識されなかった。
/// </para>
/// <para>
/// 判定（何もしない／切断にする／回復にする／読み取りを始める）は実機 felicalib に依存しない純関数
/// <see cref="FelicaCardReader.DecideHealthCheckAction"/> として検証する。タイマーの起動は private かつ
/// 実機依存のため、ソーステキストの静的検査で固定する（抽出範囲の妥当性も併せて表明する。#1794）。
/// </para>
/// </remarks>
public class FelicaCardReaderHealthCheckTransitionTests
{
    #region 判定（純関数）

    [Fact]
    public void DecideHealthCheckAction_起動時に未接続で読み取り前のまま接続を検出したら_読み取りを始めること()
    {
        FelicaCardReader.DecideHealthCheckAction(
                isAvailable: true, CardReaderConnectionState.Disconnected, isReading: false)
            .Should().Be(FelicaCardReader.HealthCheckAction.StartReading,
                "ポーリングを起動しないまま接続中と通知すると、「接続中」なのにカードを読まない状態になる");
    }

    [Fact]
    public void DecideHealthCheckAction_読み取り中に切断から回復したら_回復として通知するだけであること()
    {
        FelicaCardReader.DecideHealthCheckAction(
                isAvailable: true, CardReaderConnectionState.Disconnected, isReading: true)
            .Should().Be(FelicaCardReader.HealthCheckAction.MarkRecovered,
                "使用中の抜き差しではポーリングが動き続けているので、起動し直さない（従来の挙動）");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecideHealthCheckAction_接続中に見えなくなったら_切断として通知すること(bool isReading)
    {
        FelicaCardReader.DecideHealthCheckAction(
                isAvailable: false, CardReaderConnectionState.Connected, isReading)
            .Should().Be(FelicaCardReader.HealthCheckAction.MarkDisconnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecideHealthCheckAction_切断中で見えないままなら_何もしないこと(bool isReading)
    {
        FelicaCardReader.DecideHealthCheckAction(
                isAvailable: false, CardReaderConnectionState.Disconnected, isReading)
            .Should().Be(FelicaCardReader.HealthCheckAction.None,
                "未接続が続く間は 10 秒ごとに状態変化の通知を繰り返さない");
    }

    [Fact]
    public void DecideHealthCheckAction_接続中で見えていれば_何もしないこと()
    {
        FelicaCardReader.DecideHealthCheckAction(
                isAvailable: true, CardReaderConnectionState.Connected, isReading: true)
            .Should().Be(FelicaCardReader.HealthCheckAction.None);
    }

    /// <summary>
    /// 入力の全域を走査し、「読み取りを始める」が起きるのは
    /// 「見えている・切断中・読み取り前」の組だけであることを固定する。
    /// </summary>
    /// <remarks>
    /// 個別ケースの列挙だけだと、次に条件式を触った人が別の組で読み取りを始める
    /// （＝ポーリングを二重に起動する、接続中なのに作り直す）退行を検出できない（#1812）。
    /// </remarks>
    [Fact]
    public void DecideHealthCheckAction_読み取りを始めるのは起動時未接続から接続を検出した組だけであること()
    {
        // Issue #2327: 状態は列挙型の全値から導出する（「再接続中」を撤去した。状態を足したら下の件数で気付く）
        var states = (CardReaderConnectionState[])System.Enum.GetValues(typeof(CardReaderConnectionState));

        var checkedCount = 0;
        foreach (var isAvailable in new[] { true, false })
        {
            foreach (var state in states)
            {
                foreach (var isReading in new[] { true, false })
                {
                    var expected = isAvailable && state == CardReaderConnectionState.Disconnected && !isReading;
                    var actual = FelicaCardReader.DecideHealthCheckAction(isAvailable, state, isReading)
                        == FelicaCardReader.HealthCheckAction.StartReading;

                    actual.Should().Be(expected,
                        $"isAvailable={isAvailable}, state={state}, isReading={isReading}");
                    checkedCount++;
                }
            }
        }

        checkedCount.Should().Be(8, "走査が空振りしていないこと（状態 2 × 可否 2 × 読み取り中 2）");
    }

    #endregion

    #region 起動時に未接続でも監視を始めること（ソーステキストの静的検査）

    /// <summary>
    /// PaSoRi 未接続の分岐で、ヘルスチェックタイマーを起動してから抜けることを検査する。
    /// </summary>
    /// <remarks>
    /// 分岐の中でポーリングを起動しないことも対で表明する。未接続のままポーリングを回すと、
    /// felicalib の例外を 500ms ごとに受け続けるうえ、読み取り前の状態（<c>_isReading = false</c>）と
    /// 食い違う。
    /// </remarks>
    [Fact]
    public void StartReadingAsync_PaSoRi未接続の分岐でヘルスチェックだけを起動すること()
    {
        var body = ExtractBody("public Task StartReadingAsync");

        // Assert: 抽出範囲の妥当性（接続済みの経路まで抽出できていること）
        body.Should().Contain("StartPollingTimer()",
            "StartReadingAsync の本体を抽出できていない。抽出が空振りすると以降の検査が無意味になる");

        var branchStart = body.IndexOf("!IsReaderConnected()", System.StringComparison.Ordinal);
        branchStart.Should().BeGreaterThan(0, "PaSoRi 未接続の分岐が見つからない");
        var branchEnd = body.IndexOf("return;", branchStart, System.StringComparison.Ordinal);
        branchEnd.Should().BeGreaterThan(branchStart, "未接続の分岐が return で抜けていない");
        var branch = body.Substring(branchStart, branchEnd - branchStart);

        branch.Should().Contain("StartHealthCheckTimer()",
            "起動時に未接続でも接続の監視を始めないと、後から接続しても再接続ボタンを押すまで認識されない（#2298）");
        branch.Should().NotContain("StartPollingTimer()",
            "未接続の間はカード検出のポーリングを回さない（接続を検出した時点で起動する）");
    }

    /// <summary>
    /// ヘルスチェックが「読み取りを始める」と判定したとき、ポーリングを起動していることと、
    /// 停止済みのタイマーから遅れて届いたコールバックを無視していることを検査する。
    /// </summary>
    /// <remarks>
    /// <c>System.Timers.Timer</c> は <c>Stop()</c> の後もキュー済みの <c>Elapsed</c> を配ることがある。
    /// 再接続（停止 → 開始）の途中で古いコールバックが読み取りを始めると、停止したはずのポーリングが
    /// 生き残る。
    /// </remarks>
    [Fact]
    public void OnHealthCheckTimerElapsed_読み取りを始める判定でポーリングを起動し_古いタイマーを無視すること()
    {
        var body = ExtractBody("private void OnHealthCheckTimerElapsed");

        body.Should().Contain("DecideHealthCheckAction(",
            "判定は純関数 DecideHealthCheckAction に寄せる（単体テストで固定した判定と実装を一致させる）");
        body.Should().Contain("StartPollingTimer()",
            "読み取りを始めるときはポーリングを起動する。起動せずに接続中と通知するとカードを読まない");
        body.Should().Contain("ReferenceEquals(sender, _healthCheckTimer)",
            "停止済みのタイマーから遅れて届いたコールバックで読み取りを始めない");

        // 古いタイマーの確認は判定より前に置く（判定の後ろでは、古いコールバックが読み取りを始めてしまう）
        var guardIndex = body.IndexOf("ReferenceEquals(sender, _healthCheckTimer)", System.StringComparison.Ordinal);
        var decideIndex = body.IndexOf("DecideHealthCheckAction(", System.StringComparison.Ordinal);
        guardIndex.Should().BeLessThan(decideIndex, "停止済みのタイマーを無視する確認が、判定より前にあること");

        // ポーリングの起動は「読み取りを始める」判定の内側に限る（毎回起動するとポーリングを 10 秒ごとに作り直す）
        var startReadingBranch = body.IndexOf("action == HealthCheckAction.StartReading", System.StringComparison.Ordinal);
        startReadingBranch.Should().BeGreaterThan(0, "読み取りを始める判定の分岐が見つからない");
        var pollingIndex = body.IndexOf("StartPollingTimer()", System.StringComparison.Ordinal);
        pollingIndex.Should().BeGreaterThan(startReadingBranch, "ポーリングの起動が読み取りを始める分岐より前にある");
        body.IndexOf("}", startReadingBranch, System.StringComparison.Ordinal).Should().BeGreaterThan(pollingIndex,
            "ポーリングの起動が読み取りを始める分岐の内側にあること");
        body.IndexOf("StartPollingTimer()", pollingIndex + 1, System.StringComparison.Ordinal).Should().Be(-1,
            "ポーリングを起動するのは読み取りを始める分岐の 1 か所だけ");

        // 判定から通知までの間に再接続が割り込んだ場合に備え、通知の直前にも現役のタイマーかを確かめる
        var recheckIndex = body.IndexOf("IsCurrentHealthCheckTimer(sender)", System.StringComparison.Ordinal);
        recheckIndex.Should().BeGreaterThan(pollingIndex, "通知の直前の再確認が無い");
        body.IndexOf("switch (action)", System.StringComparison.Ordinal).Should().BeGreaterThan(recheckIndex,
            "再確認は通知（switch）より前に置く");
    }

    private static string ExtractBody(string signatureMarker)
    {
        var sourcePath = Path.Combine(
            ICCardManager.Tests.TestPaths.GetProductionSourceRoot(),
            "Infrastructure", "CardReader", "FelicaCardReader.cs");
        File.Exists(sourcePath).Should().BeTrue($"検査対象のソースが見つからない: {sourcePath}");

        var codeOnly = TestSourceInspection.ToCodeOnly(File.ReadAllText(sourcePath));
        return TestSourceInspection.ExtractMethodBody(codeOnly, signatureMarker);
    }

    #endregion
}
