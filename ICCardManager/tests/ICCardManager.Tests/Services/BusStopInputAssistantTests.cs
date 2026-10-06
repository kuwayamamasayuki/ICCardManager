using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2251: バス停名入力の補助（候補の並び・既定値・往復の復路）を決める純関数の単体テスト。
/// </summary>
/// <remarks>
/// 段ごとに異なるバス停名を置き、どの段から来た候補かが結果の並びから読めるようにする（testing.md「2 つの経路に同じ値を仕込まない」）。
/// </remarks>
public class BusStopInputAssistantTests
{
    private static BusStopUsageStatRow Row(
        string busStops, int? amount, bool sameStaff, int count, DateTime? lastUsed = null)
        => new()
        {
            BusStops = busStops,
            Amount = amount,
            IsSameStaff = sameStaff,
            UsageCount = count,
            LastUsedDate = lastUsed,
        };

    #region Rank

    [Fact]
    public void Rank_同じ職員と同じ金額から同じ金額と同じ職員を経て全体の順に並ぶこと()
    {
        var stats = new[]
        {
            Row("職員のみ～A", 150, sameStaff: true, count: 9),   // ③ 同じ職員（別の金額）
            Row("金額のみ～B", 200, sameStaff: false, count: 9),  // ② 同じ金額（別の職員）
            Row("両方～C", 200, sameStaff: true, count: 1),       // ① 同じ職員×同じ金額（回数は最小）
        };
        var overall = new[] { "全体～D", "職員のみ～A" };

        var result = BusStopInputAssistant.Rank(stats, overall, amount: 200);

        // 回数が少なくても ① が先頭。② には ① の行も含まれるが、重複は先に現れた段の位置を残す
        result.Should().Equal("両方～C", "金額のみ～B", "職員のみ～A", "全体～D");
    }

    [Fact]
    public void Rank_金額が違う明細では並びが変わること()
    {
        // 対の表明: 同じ実績でも明細の金額で並びが変わる（全欄で 1 つの一覧を共有していた従来との違い）
        var stats = new[]
        {
            Row("二百円～A", 200, sameStaff: true, count: 3),
            Row("百五十円～B", 150, sameStaff: true, count: 3),
        };

        BusStopInputAssistant.Rank(stats, Array.Empty<string>(), amount: 200)
            .Should().Equal("二百円～A", "百五十円～B");
        BusStopInputAssistant.Rank(stats, Array.Empty<string>(), amount: 150)
            .Should().Equal("百五十円～B", "二百円～A");
    }

    [Fact]
    public void Rank_段の中は回数の多い順と最終利用日の新しい順と名前の順に並ぶこと()
    {
        var older = new DateTime(2026, 4, 1);
        var newer = new DateTime(2026, 9, 1);
        var stats = new[]
        {
            Row("回数少～A", 200, sameStaff: true, count: 2, lastUsed: newer),
            Row("同数古～B", 200, sameStaff: true, count: 5, lastUsed: older),
            Row("同数新～C", 200, sameStaff: true, count: 5, lastUsed: newer),
            Row("同日い～E", 200, sameStaff: true, count: 4, lastUsed: older),
            Row("同日あ～D", 200, sameStaff: true, count: 4, lastUsed: older),
        };

        var result = BusStopInputAssistant.Rank(stats, Array.Empty<string>(), amount: 200);

        result.Should().Equal("同数新～C", "同数古～B", "同日あ～D", "同日い～E", "回数少～A");
    }

    [Fact]
    public void Rank_金額が不明な明細は同じ職員と全体だけで並ぶこと()
    {
        var stats = new[]
        {
            Row("金額のみ～B", 200, sameStaff: false, count: 9),
            Row("職員～A", 200, sameStaff: true, count: 1),
        };

        var result = BusStopInputAssistant.Rank(stats, new[] { "全体～D" }, amount: null);

        result.Should().Equal("職員～A", "全体～D");
    }

    [Fact]
    public void Rank_同じバス停名の回数は職員と金額の行をまたいで合算されること()
    {
        // ② の段（同じ金額）は職員を問わないので、同じ職員の行と別の職員の行の回数を足して比べる
        var stats = new[]
        {
            Row("合算～A", 200, sameStaff: true, count: 2),
            Row("合算～A", 200, sameStaff: false, count: 2),
            Row("単独～B", 200, sameStaff: false, count: 3),
        };

        var result = BusStopInputAssistant.Rank(stats, Array.Empty<string>(), amount: 999);

        // 金額が一致しないので ③（同じ職員）だけ: 合算～A のみ
        result.Should().Equal("合算～A");

        BusStopInputAssistant.Rank(stats, Array.Empty<string>(), amount: 200)
            .Should().Equal("合算～A", "単独～B");
    }

    [Fact]
    public void Rank_実績が無ければ従来の全体の並びのままであること()
    {
        var overall = new[] { "全体1～A", "全体2～B" };

        BusStopInputAssistant.Rank(Array.Empty<BusStopUsageStatRow>(), overall, amount: 200)
            .Should().Equal("全体1～A", "全体2～B");
    }

    #endregion

    #region SelectDefault

    [Fact]
    public void SelectDefault_同じ職員と同じ金額で2回以上の単独1位なら既定値になること()
    {
        var stats = new[]
        {
            Row("既定～A", 200, sameStaff: true, count: 2),
            Row("次点～B", 200, sameStaff: true, count: 1),
        };

        BusStopInputAssistant.SelectDefault(stats, amount: 200).Should().Be("既定～A");
    }

    [Fact]
    public void SelectDefault_1回だけの実績では入れないこと()
    {
        var stats = new[] { Row("一度～A", 200, sameStaff: true, count: 1) };

        BusStopInputAssistant.SelectDefault(stats, amount: 200).Should().BeNull();
    }

    [Fact]
    public void SelectDefault_1位が同数なら入れないこと()
    {
        // 往復（A～B と B～A）は同じ回数になりやすく、片方を入れると向きが逆の値が入り得る
        var stats = new[]
        {
            Row("天神～博多", 200, sameStaff: true, count: 3),
            Row("博多～天神", 200, sameStaff: true, count: 3),
        };

        BusStopInputAssistant.SelectDefault(stats, amount: 200).Should().BeNull();
    }

    [Fact]
    public void SelectDefault_別の職員や別の金額の実績では入れないこと()
    {
        // 対の表明: ②③④ の段の回数がいくら多くても既定値にはしない（① だけで決める）
        var stats = new[]
        {
            Row("他職員～A", 200, sameStaff: false, count: 50),
            Row("別金額～B", 150, sameStaff: true, count: 50),
        };

        BusStopInputAssistant.SelectDefault(stats, amount: 200).Should().BeNull();
        BusStopInputAssistant.SelectDefault(stats, amount: null).Should().BeNull();
    }

    [Fact]
    public void SelectDefault_2位が別の職員の行でも1位の判定に混ぜないこと()
    {
        // 別の職員が同じ回数で使っていても、同じ職員×同じ金額の中で単独 1 位なら入れる
        var stats = new[]
        {
            Row("既定～A", 200, sameStaff: true, count: 2),
            Row("他職員～B", 200, sameStaff: false, count: 2),
        };

        BusStopInputAssistant.SelectDefault(stats, amount: 200).Should().Be("既定～A");
    }

    #endregion

    #region IsRoundTripContinuation / ReverseRoute

    [Fact]
    public void IsRoundTripContinuation_同じ金額で同じ利用日ならtrue()
    {
        var day = new DateTime(2026, 9, 1, 0, 0, 0);
        BusStopInputAssistant.IsRoundTripContinuation(200, day, 200, day.AddHours(9))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(200, 150, 0)]   // 金額が違う
    [InlineData(200, 200, 1)]   // 利用日が違う（日をまたぐ）
    public void IsRoundTripContinuation_金額か利用日が違えばfalse(int previousAmount, int amount, int dayOffset)
    {
        var day = new DateTime(2026, 9, 1);
        BusStopInputAssistant.IsRoundTripContinuation(previousAmount, day, amount, day.AddDays(dayOffset))
            .Should().BeFalse();
    }

    [Fact]
    public void IsRoundTripContinuation_金額か利用日が不明ならfalse()
    {
        var day = new DateTime(2026, 9, 1);
        BusStopInputAssistant.IsRoundTripContinuation(null, day, null, day).Should().BeFalse();
        BusStopInputAssistant.IsRoundTripContinuation(200, null, 200, null).Should().BeFalse();
        BusStopInputAssistant.IsRoundTripContinuation(200, day, 200, null).Should().BeFalse();
    }

    [Theory]
    [InlineData("天神～博多", "博多～天神")]
    [InlineData(" 天神 ～ 博多 ", "博多～天神")]
    [InlineData("天神（日銀前）～下原", "下原～天神（日銀前）")]
    public void ReverseRoute_乗車と降車を入れ替えること(string route, string expected)
    {
        BusStopInputAssistant.ReverseRoute(route).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("天神")]
    [InlineData("天神～博多～薬院")]
    [InlineData("天神～")]
    [InlineData("★")]
    public void ReverseRoute_乗車と降車の2つに分かれなければnull(string? route)
    {
        BusStopInputAssistant.ReverseRoute(route).Should().BeNull();
    }

    #endregion
}
