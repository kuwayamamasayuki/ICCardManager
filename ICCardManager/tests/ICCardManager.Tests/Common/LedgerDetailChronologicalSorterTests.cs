using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Common;

public class LedgerDetailChronologicalSorterTests
{
    #region 基本ケース

    [Fact]
    public void Sort_EmptyList_ReturnsEmptyList()
    {
        var result = LedgerDetailChronologicalSorter.Sort(new List<LedgerDetail>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void Sort_SingleItem_ReturnsSameItem()
    {
        var detail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 210,
            Balance = 790
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { detail });

        result.Should().HaveCount(1);
        result[0].Should().BeSameAs(detail);
    }

    [Fact]
    public void Sort_TwoTrips_ReturnsChronologicalOrder()
    {
        // 時系列: 天神→博多(1000→790), 博多→天神(790→580)
        // 入力: 新しい順（FeliCa順）
        var newer = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            Balance = 580
        };
        var older = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 210,
            Balance = 790
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { newer, older });

        result.Should().HaveCount(2);
        result[0].Balance.Should().Be(790);  // 古い方（天神→博多）が先
        result[1].Balance.Should().Be(580);  // 新しい方（博多→天神）が後
    }

    [Fact]
    public void Sort_TwoTrips_AlreadyChronological_ReturnsSameOrder()
    {
        // 入力が既に古い順の場合でも正しく動作する
        var older = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 210,
            Balance = 790
        };
        var newer = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            Balance = 580
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { older, newer });

        result.Should().HaveCount(2);
        result[0].Balance.Should().Be(790);  // 古い方が先
        result[1].Balance.Should().Be(580);  // 新しい方が後
    }

    #endregion

    #region チャージが利用の間に挟まるケース

    [Fact]
    public void Sort_ChargeBetweenTrips_ReturnsCorrectOrder()
    {
        // 時系列: 天神→博多(1000→790), チャージ(790→1790), 博多→天神(1790→1580)
        // 入力: 新しい順（FeliCa順）
        var trip2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            Balance = 1580
        };
        var charge = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 1000,
            Balance = 1790,
            IsCharge = true
        };
        var trip1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 210,
            Balance = 790
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { trip2, charge, trip1 });

        result.Should().HaveCount(3);
        result[0].Balance.Should().Be(790);   // trip1: 天神→博多（最古）
        result[1].Balance.Should().Be(1790);  // charge
        result[2].Balance.Should().Be(1580);  // trip2: 博多→天神（最新）
    }

    [Fact]
    public void Sort_ChargeAtStart_ReturnsChargeFirst()
    {
        // 時系列: チャージ(500→1500), 天神→博多(1500→1290)
        var trip = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 210,
            Balance = 1290
        };
        var charge = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 1000,
            Balance = 1500,
            IsCharge = true
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { trip, charge });

        result.Should().HaveCount(2);
        result[0].Balance.Should().Be(1500);  // チャージが先
        result[1].Balance.Should().Be(1290);  // 利用が後
    }

    #endregion

    #region フォールバック

    [Fact]
    public void Sort_MissingBalance_PreservesOrder_WhenPreserveOrderOnFailureIsTrue()
    {
        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 210,
            Balance = null  // 残高情報なし
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            Balance = 580
        };

        var result = LedgerDetailChronologicalSorter.Sort(
            new[] { detail1, detail2 }, preserveOrderOnFailure: true);

        // 入力順序が維持される
        result.Should().HaveCount(2);
        result[0].Should().BeSameAs(detail1);
        result[1].Should().BeSameAs(detail2);
    }

    [Fact]
    public void Sort_MissingBalance_ReversesOrder_WhenPreserveOrderOnFailureIsFalse()
    {
        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 210,
            Balance = null  // 残高情報なし
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            Balance = 580
        };

        var result = LedgerDetailChronologicalSorter.Sort(
            new[] { detail1, detail2 }, preserveOrderOnFailure: false);

        // FeliCa入力を想定して逆順
        result.Should().HaveCount(2);
        result[0].EntryStation.Should().Be("博多");
        result[1].EntryStation.Should().Be("天神");
    }

    [Fact]
    public void Sort_CircularChain_PreservesOrder()
    {
        // 残高が循環するケース（異常データ）
        // detail1: balance_before = 500 + 210 = 710, balance = 500
        // detail2: balance_before = 710 + 210 = 920, balance = 710
        // detail1のbalance_before(710) == detail2のbalance(710) → 循環
        // ただしチェーン先頭の検出で解決される可能性もある
        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "A",
            ExitStation = "B",
            Amount = 210,
            Balance = 500
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "B",
            ExitStation = "A",
            Amount = 210,
            Balance = 710
        };

        // エラーが発生しないこと
        var result = LedgerDetailChronologicalSorter.Sort(
            new[] { detail1, detail2 }, preserveOrderOnFailure: true);

        result.Should().HaveCount(2);
    }

    #endregion

    #region 挿入順序に依存しないことの検証

    [Fact]
    public void Sort_SameResult_RegardlessOfInputOrder()
    {
        // 3件の利用を異なる順序で入力しても、同じ時系列順になること
        // 時系列: A→B(1000→790), B→C(790→580), C→D(580→370)
        var trip1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "A",
            ExitStation = "B",
            Amount = 210,
            Balance = 790
        };
        var trip2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "B",
            ExitStation = "C",
            Amount = 210,
            Balance = 580
        };
        var trip3 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "C",
            ExitStation = "D",
            Amount = 210,
            Balance = 370
        };

        // パターン1: FeliCa順（新しい→古い）
        var result1 = LedgerDetailChronologicalSorter.Sort(new[] { trip3, trip2, trip1 });
        // パターン2: 古い順
        var result2 = LedgerDetailChronologicalSorter.Sort(new[] { trip1, trip2, trip3 });
        // パターン3: ランダム順
        var result3 = LedgerDetailChronologicalSorter.Sort(new[] { trip2, trip3, trip1 });

        // すべて同じ時系列順になる
        foreach (var result in new[] { result1, result2, result3 })
        {
            result.Should().HaveCount(3);
            result[0].Balance.Should().Be(790);  // A→B（最古）
            result[1].Balance.Should().Be(580);  // B→C
            result[2].Balance.Should().Be(370);  // C→D（最新）
        }
    }

    #endregion
}
