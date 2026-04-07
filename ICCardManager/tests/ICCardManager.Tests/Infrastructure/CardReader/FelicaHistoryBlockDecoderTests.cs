using System;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// FelicaHistoryBlockDecoder のテスト。
/// 16バイトFeliCa履歴ブロックの解析（日付・利用種別・バス判別・金額計算・
/// Issue #942 ポイント還元フォールバック）を網羅的に検証する。
/// </summary>
public class FelicaHistoryBlockDecoderTests
{
    /// <summary>
    /// 16バイトのFeliCa履歴ブロックを生成するヘルパー。
    /// </summary>
    /// <param name="usageType">バイト1: 利用種別</param>
    /// <param name="year">年（2000以降）</param>
    /// <param name="month">月（1-12）</param>
    /// <param name="day">日（1-31）</param>
    /// <param name="entryStationCode">入場駅コード（ビッグエンディアン16bit）</param>
    /// <param name="exitStationCode">出場駅コード（ビッグエンディアン16bit）</param>
    /// <param name="balance">残額（リトルエンディアン16bit）</param>
    private static byte[] BuildBlock(
        byte usageType = 0x16,
        int year = 2026,
        int month = 4,
        int day = 7,
        int entryStationCode = 0,
        int exitStationCode = 0,
        int balance = 0)
    {
        var block = new byte[16];
        block[0] = 0x00; // 機器種別
        block[1] = usageType;
        block[2] = 0x00; // 支払種別
        block[3] = 0x00; // 入出場種別

        // バイト4-5: 日付（[YYYYYYY][MMMM][DDDDD]、ビッグエンディアン）
        var yearOffset = year - 2000;
        var dateValue = (yearOffset << 9) | (month << 5) | day;
        block[4] = (byte)((dateValue >> 8) & 0xFF);
        block[5] = (byte)(dateValue & 0xFF);

        // バイト6-7: 入場駅コード（BE）
        block[6] = (byte)((entryStationCode >> 8) & 0xFF);
        block[7] = (byte)(entryStationCode & 0xFF);

        // バイト8-9: 出場駅コード（BE）
        block[8] = (byte)((exitStationCode >> 8) & 0xFF);
        block[9] = (byte)(exitStationCode & 0xFF);

        // バイト10-11: 残額（LE）
        block[10] = (byte)(balance & 0xFF);
        block[11] = (byte)((balance >> 8) & 0xFF);

        return block;
    }

    /// <summary>常に駅名を返さないリゾルバ（バス判定用）</summary>
    private static readonly Func<int, int, string> NullResolver = (_, _) => null;

    /// <summary>常に固定の駅名を返すリゾルバ</summary>
    private static readonly Func<int, int, string> FakeResolver = (line, num) => $"駅{line:X2}{num:X2}";

    #region 入力検証

    /// <summary>
    /// null入力の場合は null を返す。
    /// </summary>
    [Fact]
    public void Decode_NullInput_ReturnsNull()
    {
        var result = FelicaHistoryBlockDecoder.Decode(null, null, NullResolver, out var fallback);

        result.Should().BeNull();
        fallback.Should().BeFalse();
    }

    /// <summary>
    /// 16バイト未満の入力は null を返す。
    /// </summary>
    [Fact]
    public void Decode_TooShort_ReturnsNull()
    {
        var result = FelicaHistoryBlockDecoder.Decode(new byte[15], null, NullResolver, out var fallback);

        result.Should().BeNull();
        fallback.Should().BeFalse();
    }

    /// <summary>
    /// 17バイト入力の場合、先頭16バイトのみ参照され RawBytes も16バイトに切り詰められる。
    /// </summary>
    [Fact]
    public void Decode_OversizedInput_TruncatesRawBytesTo16()
    {
        var oversized = new byte[20];
        var block = BuildBlock(balance: 1000);
        Array.Copy(block, oversized, 16);
        oversized[16] = 0xFF;

        var result = FelicaHistoryBlockDecoder.Decode(oversized, null, NullResolver, out _);

        result.Should().NotBeNull();
        result.RawBytes.Should().HaveCount(16);
        result.Balance.Should().Be(1000);
    }

    #endregion

    #region 日付デコード

    /// <summary>
    /// 通常日付（2026/04/07）が正しくデコードされる。
    /// </summary>
    [Fact]
    public void Decode_ValidDate_ParsedCorrectly()
    {
        var block = BuildBlock(year: 2026, month: 4, day: 7);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.UseDate.Should().Be(new DateTime(2026, 4, 7));
    }

    /// <summary>
    /// 月=0 は無効日付として UseDate=null になる。
    /// </summary>
    [Fact]
    public void Decode_MonthZero_UseDateNull()
    {
        var block = BuildBlock(year: 2026, month: 0, day: 1);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.Should().NotBeNull();
        result.UseDate.Should().BeNull();
    }

    /// <summary>
    /// 月=13 は無効日付として UseDate=null になる。
    /// </summary>
    [Fact]
    public void Decode_MonthOutOfRange_UseDateNull()
    {
        var block = BuildBlock(year: 2026, month: 13, day: 1);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.UseDate.Should().BeNull();
    }

    /// <summary>
    /// 2月30日のように月内に存在しない日付は無効として UseDate=null になる。
    /// （DateTime コンストラクタの例外が内部catchで吸収される）
    /// </summary>
    [Fact]
    public void Decode_NonExistentDate_UseDateNull()
    {
        var block = BuildBlock(year: 2026, month: 2, day: 30);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.UseDate.Should().BeNull();
    }

    #endregion

    #region 利用種別

    /// <summary>0x02 → IsCharge=true, IsPointRedemption=false</summary>
    [Fact]
    public void Decode_UsageType02_IsCharge()
    {
        var block = BuildBlock(usageType: 0x02, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.IsCharge.Should().BeTrue();
        result.IsPointRedemption.Should().BeFalse();
    }

    /// <summary>0x14 オートチャージもチャージとして扱う</summary>
    [Fact]
    public void Decode_UsageType14_IsCharge()
    {
        var block = BuildBlock(usageType: 0x14, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.IsCharge.Should().BeTrue();
    }

    /// <summary>0x0D → IsPointRedemption=true</summary>
    [Fact]
    public void Decode_UsageType0D_IsPointRedemption()
    {
        var block = BuildBlock(usageType: 0x0D, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.IsCharge.Should().BeFalse();
        result.IsPointRedemption.Should().BeTrue();
    }

    /// <summary>0x16 通常利用 → 両方false</summary>
    [Fact]
    public void Decode_UsageType16_NormalUsage()
    {
        var block = BuildBlock(usageType: 0x16, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.IsCharge.Should().BeFalse();
        result.IsPointRedemption.Should().BeFalse();
    }

    #endregion

    #region バス判別

    /// <summary>
    /// 駅コードが両方0 かつ非チャージ非還元 → IsBus=true、駅名は両方null
    /// </summary>
    [Fact]
    public void Decode_BothStationCodesZero_IsBus()
    {
        var block = BuildBlock(usageType: 0x16, entryStationCode: 0, exitStationCode: 0, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, FakeResolver, out _);

        result.IsBus.Should().BeTrue();
        result.EntryStation.Should().BeNull();
        result.ExitStation.Should().BeNull();
    }

    /// <summary>
    /// 駅コードはあるがリゾルバが両方nullを返す → IsBus=true（西鉄バス等のケース）
    /// </summary>
    [Fact]
    public void Decode_StationCodesPresentButUnresolved_IsBus()
    {
        var block = BuildBlock(usageType: 0x16, entryStationCode: 0x1234, exitStationCode: 0x5678, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.IsBus.Should().BeTrue();
        result.EntryStation.Should().BeNull();
        result.ExitStation.Should().BeNull();
    }

    /// <summary>
    /// 駅コードが解決できる場合 → IsBus=false、駅名が設定される
    /// </summary>
    [Fact]
    public void Decode_StationsResolved_NotBus()
    {
        var block = BuildBlock(usageType: 0x16, entryStationCode: 0x1234, exitStationCode: 0x5678, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, FakeResolver, out _);

        result.IsBus.Should().BeFalse();
        result.EntryStation.Should().Be("駅1234");
        result.ExitStation.Should().Be("駅5678");
    }

    /// <summary>
    /// チャージレコードは駅コード0でも IsBus=false
    /// </summary>
    [Fact]
    public void Decode_ChargeWithZeroStations_NotBus()
    {
        var block = BuildBlock(usageType: 0x02, entryStationCode: 0, exitStationCode: 0, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.IsBus.Should().BeFalse();
        result.IsCharge.Should().BeTrue();
    }

    /// <summary>
    /// ポイント還元レコードは駅コード0でも IsBus=false
    /// </summary>
    [Fact]
    public void Decode_PointRedemptionWithZeroStations_NotBus()
    {
        var block = BuildBlock(usageType: 0x0D, entryStationCode: 0, exitStationCode: 0, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.IsBus.Should().BeFalse();
        result.IsPointRedemption.Should().BeTrue();
    }

    /// <summary>
    /// 片方の駅だけ解決できた場合は IsBus=false（駅名両方未解決ではないため）
    /// </summary>
    [Fact]
    public void Decode_OnlyOneStationResolved_NotBus()
    {
        var block = BuildBlock(usageType: 0x16, entryStationCode: 0x1234, exitStationCode: 0x5678, balance: 1000);
        Func<int, int, string> partial = (line, num) => line == 0x12 ? "A駅" : null;

        var result = FelicaHistoryBlockDecoder.Decode(block, null, partial, out _);

        result.IsBus.Should().BeFalse();
        result.EntryStation.Should().Be("A駅");
        result.ExitStation.Should().BeNull();
    }

    /// <summary>
    /// resolveStationName が null の場合でも例外を投げず、駅コードがあってもバス扱いになる
    /// </summary>
    [Fact]
    public void Decode_NullResolver_FallsBackToBus()
    {
        var block = BuildBlock(usageType: 0x16, entryStationCode: 0x1234, exitStationCode: 0x5678, balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, null, out _);

        result.IsBus.Should().BeTrue();
    }

    #endregion

    #region 金額計算

    /// <summary>
    /// 利用レコード: 前回残高 - 今回残高 で正の運賃が計算される
    /// </summary>
    [Fact]
    public void Decode_Usage_AmountIsPreviousMinusCurrent()
    {
        var previous = BuildBlock(balance: 1000);
        var current = BuildBlock(usageType: 0x16, balance: 790);

        var result = FelicaHistoryBlockDecoder.Decode(current, previous, NullResolver, out _);

        result.Amount.Should().Be(210);
        result.Balance.Should().Be(790);
    }

    /// <summary>
    /// チャージレコード: 今回残高 - 前回残高 でチャージ額が計算される
    /// </summary>
    [Fact]
    public void Decode_Charge_AmountIsCurrentMinusPrevious()
    {
        var previous = BuildBlock(balance: 500);
        var current = BuildBlock(usageType: 0x02, balance: 1500);

        var result = FelicaHistoryBlockDecoder.Decode(current, previous, NullResolver, out _);

        result.Amount.Should().Be(1000);
        result.IsCharge.Should().BeTrue();
    }

    /// <summary>
    /// previousData が null の場合、Amount は null
    /// </summary>
    [Fact]
    public void Decode_NoPrevious_AmountIsNull()
    {
        var current = BuildBlock(usageType: 0x16, balance: 790);

        var result = FelicaHistoryBlockDecoder.Decode(current, null, NullResolver, out _);

        result.Amount.Should().BeNull();
        result.Balance.Should().Be(790);
    }

    /// <summary>
    /// previousData の長さが12未満の場合、Amount は null
    /// </summary>
    [Fact]
    public void Decode_PreviousTooShort_AmountIsNull()
    {
        var current = BuildBlock(usageType: 0x16, balance: 790);
        var previous = new byte[10];

        var result = FelicaHistoryBlockDecoder.Decode(current, previous, NullResolver, out _);

        result.Amount.Should().BeNull();
    }

    /// <summary>
    /// 残額がリトルエンディアンで読まれる: バイト10=0x10, バイト11=0x27 → 0x2710 = 10000円
    /// </summary>
    [Fact]
    public void Decode_BalanceIsLittleEndian()
    {
        var block = BuildBlock(balance: 0x2710); // 10000

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.Balance.Should().Be(10000);
        result.RawBytes[10].Should().Be(0x10);
        result.RawBytes[11].Should().Be(0x27);
    }

    #endregion

    #region Issue #942 ポイント還元フォールバック

    /// <summary>
    /// Issue #942: 利用種別が0x16（通常利用）でも残高が増加していれば
    /// ポイント還元として処理し、Amount は正の入金額に符号反転される。
    /// out フラグも true になる。
    /// </summary>
    [Fact]
    public void Decode_NonRedemptionWithBalanceIncrease_FallbackTriggered()
    {
        var previous = BuildBlock(balance: 500);
        var current = BuildBlock(usageType: 0x16, balance: 700); // +200

        var result = FelicaHistoryBlockDecoder.Decode(current, previous, NullResolver, out var fallback);

        fallback.Should().BeTrue();
        result.IsPointRedemption.Should().BeTrue();
        result.IsCharge.Should().BeFalse();
        result.Amount.Should().Be(200); // 符号反転後
    }

    /// <summary>
    /// 通常の利用（残高減少）ではフォールバックは発生しない
    /// </summary>
    [Fact]
    public void Decode_NormalUsage_FallbackNotTriggered()
    {
        var previous = BuildBlock(balance: 1000);
        var current = BuildBlock(usageType: 0x16, balance: 790);

        FelicaHistoryBlockDecoder.Decode(current, previous, NullResolver, out var fallback);

        fallback.Should().BeFalse();
    }

    /// <summary>
    /// 既に IsCharge と判定されているレコードではフォールバックは発生しない
    /// </summary>
    [Fact]
    public void Decode_ChargeRecord_FallbackNotTriggered()
    {
        var previous = BuildBlock(balance: 500);
        var current = BuildBlock(usageType: 0x02, balance: 1500);

        FelicaHistoryBlockDecoder.Decode(current, previous, NullResolver, out var fallback);

        fallback.Should().BeFalse();
    }

    /// <summary>
    /// 既に IsPointRedemption（0x0D）と判定されているレコードではフォールバックは発生しない
    /// </summary>
    [Fact]
    public void Decode_ExplicitPointRedemption_FallbackNotTriggered()
    {
        var previous = BuildBlock(balance: 500);
        var current = BuildBlock(usageType: 0x0D, balance: 700);

        FelicaHistoryBlockDecoder.Decode(current, previous, NullResolver, out var fallback);

        fallback.Should().BeFalse();
    }

    #endregion

    #region RawBytes

    /// <summary>
    /// RawBytes には入力ブロックの先頭16バイトがコピーされる（参照ではなく独立配列）
    /// </summary>
    [Fact]
    public void Decode_RawBytes_IsIndependentCopy()
    {
        var block = BuildBlock(balance: 1000);

        var result = FelicaHistoryBlockDecoder.Decode(block, null, NullResolver, out _);

        result.RawBytes.Should().NotBeSameAs(block);
        result.RawBytes.Should().Equal(block);
    }

    #endregion
}
