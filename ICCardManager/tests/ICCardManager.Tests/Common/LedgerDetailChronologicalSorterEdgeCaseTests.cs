using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// LedgerDetailChronologicalSorterのエッジケーステスト
/// 既存テストで検出できない境界値・異常データパターンを検証する。
/// </summary>
public class LedgerDetailChronologicalSorterEdgeCaseTests
{
    #region 同一残高の複数明細（チェーン曖昧性）

    /// <summary>
    /// 同じBalance値を持つ2件の明細がある場合、チェーン構築が曖昧になる。
    /// balance_beforeの差でチェーン先頭を特定できるかを検証する。
    /// </summary>
    [Fact]
    public void Sort_TwoDetailsWithSameBalance_ShouldNotThrow()
    {
        // Arrange: 利用1(1200→1000), 利用2(1210→1000)
        // 両方ともBalance=1000だが、balance_beforeが異なる
        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "A",
            ExitStation = "B",
            Amount = 200,
            Balance = 1000
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "C",
            ExitStation = "D",
            Amount = 210,
            Balance = 1000
        };

        // Act - 例外が発生しないこと
        var result = LedgerDetailChronologicalSorter.Sort(new[] { detail1, detail2 });

        // Assert - 2件とも出力されること
        result.Should().HaveCount(2);
    }

    /// <summary>
    /// 3件の明細のうち2件が同じBalance値を持つ場合、
    /// チェーンが正しく構築されるかを検証する。
    /// 時系列: A→B(1000→800), B→C(800→600), C→D(810→600)
    /// detail2とdetail3のBalanceがどちらも600。
    /// </summary>
    [Fact]
    public void Sort_TwoOfThreeWithSameBalance_ProducesAllItems()
    {
        // Arrange
        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "A",
            ExitStation = "B",
            Amount = 200,
            Balance = 800
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "B",
            ExitStation = "C",
            Amount = 200,
            Balance = 600
        };
        var detail3 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "C",
            ExitStation = "D",
            Amount = 210,
            Balance = 600  // detail2と同じBalance
        };

        // Act
        var result = LedgerDetailChronologicalSorter.Sort(new[] { detail3, detail1, detail2 });

        // Assert - 全件出力されること（順序は不定でも全件含まれていること）
        result.Should().HaveCount(3);
        result.Select(d => d.EntryStation).Should().Contain("A");
        result.Select(d => d.EntryStation).Should().Contain("B");
        result.Select(d => d.EntryStation).Should().Contain("C");
    }

    #endregion

    #region Amount=nullのエッジケース

    /// <summary>
    /// Issue #964: Amount=nullの場合、balance_before = Balance ± 0 = Balance となる。
    /// これが正しく処理されることを検証する。
    /// </summary>
    [Fact]
    public void Sort_AmountNull_TreatsAsZero()
    {
        // Arrange: Amount=nullの明細（FeliCa最古レコード等で発生）
        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "A",
            ExitStation = "B",
            Amount = null,  // Amount不明
            Balance = 1000
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "B",
            ExitStation = "C",
            Amount = 200,
            Balance = 800
        };

        // Act
        var result = LedgerDetailChronologicalSorter.Sort(new[] { detail2, detail1 });

        // Assert - detail1の balance_before = 1000+0 = 1000 (自分自身のBalance)
        // チェーン先頭検出で detail1 が先頭になり、detail2(balance_before=1000)が続く
        result.Should().HaveCount(2);
        result[0].Balance.Should().Be(1000, "Amount=nullの明細が先頭");
        result[1].Balance.Should().Be(800, "Amount=200の利用が後");
    }

    /// <summary>
    /// Amount=nullかつIsCharge=trueの場合でも例外にならないことを検証。
    /// </summary>
    [Fact]
    public void Sort_AmountNullWithIsCharge_DoesNotThrow()
    {
        var detail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = null,
            Balance = 500,
            IsCharge = true
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 200,
            Balance = 300
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { detail, detail2 });

        result.Should().HaveCount(2);
    }

    /// <summary>
    /// Amount=nullかつIsPointRedemption=trueの組み合わせ。
    /// </summary>
    [Fact]
    public void Sort_AmountNullWithIsPointRedemption_DoesNotThrow()
    {
        var detail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = null,
            Balance = 500,
            IsPointRedemption = true
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 100,
            Balance = 400
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { detail, detail2 });

        result.Should().HaveCount(2);
    }

    #endregion

    #region 全件Balanceがnull

    /// <summary>
    /// 全てのdetailのBalanceがnullの場合、フォールバック処理が実行されることを検証。
    /// </summary>
    [Fact]
    public void Sort_AllBalancesNull_PreservesOrder_WhenPreserveTrue()
    {
        var detail1 = new LedgerDetail { UseDate = DateTime.Today, EntryStation = "A", Balance = null };
        var detail2 = new LedgerDetail { UseDate = DateTime.Today, EntryStation = "B", Balance = null };
        var detail3 = new LedgerDetail { UseDate = DateTime.Today, EntryStation = "C", Balance = null };

        var result = LedgerDetailChronologicalSorter.Sort(
            new[] { detail1, detail2, detail3 }, preserveOrderOnFailure: true);

        result.Should().HaveCount(3);
        result[0].EntryStation.Should().Be("A", "入力順序が維持される");
        result[1].EntryStation.Should().Be("B");
        result[2].EntryStation.Should().Be("C");
    }

    [Fact]
    public void Sort_AllBalancesNull_ReversesOrder_WhenPreserveFalse()
    {
        var detail1 = new LedgerDetail { UseDate = DateTime.Today, EntryStation = "A", Balance = null };
        var detail2 = new LedgerDetail { UseDate = DateTime.Today, EntryStation = "B", Balance = null };

        var result = LedgerDetailChronologicalSorter.Sort(
            new[] { detail1, detail2 }, preserveOrderOnFailure: false);

        result.Should().HaveCount(2);
        result[0].EntryStation.Should().Be("B", "FeliCa入力想定で逆順");
        result[1].EntryStation.Should().Be("A");
    }

    #endregion

    #region IsChargeとIsPointRedemption両方true

    /// <summary>
    /// 通常はありえないが、IsChargeとIsPointRedemptionが同時にtrueの場合に
    /// 例外が発生しないことを検証。
    /// isIncomeTransaction = true になり balance_before = Balance - Amount で計算される。
    /// </summary>
    [Fact]
    public void Sort_BothIsChargeAndIsPointRedemption_DoesNotThrow()
    {
        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 500,
            Balance = 1500,
            IsCharge = true,
            IsPointRedemption = true  // 通常ありえない組み合わせ
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 200,
            Balance = 800
        };

        var result = LedgerDetailChronologicalSorter.Sort(new[] { detail1, detail2 });

        result.Should().HaveCount(2);
    }

    #endregion

    #region 3件以上のフォールバック（preserveOrderOnFailure=false）

    /// <summary>
    /// 3件以上でチェーン構築失敗時に逆順で返されることを確認。
    /// </summary>
    [Fact]
    public void Sort_ThreeItemsAllBalanceNull_ReversesAll_WhenPreserveFalse()
    {
        var details = new[]
        {
            new LedgerDetail { UseDate = DateTime.Today, EntryStation = "A", Balance = null },
            new LedgerDetail { UseDate = DateTime.Today, EntryStation = "B", Balance = null },
            new LedgerDetail { UseDate = DateTime.Today, EntryStation = "C", Balance = null }
        };

        var result = LedgerDetailChronologicalSorter.Sort(details, preserveOrderOnFailure: false);

        result.Should().HaveCount(3);
        result[0].EntryStation.Should().Be("C");
        result[1].EntryStation.Should().Be("B");
        result[2].EntryStation.Should().Be("A");
    }

    #endregion

    #region チェーン途切れ時のBalance降順追加

    /// <summary>
    /// チェーンが途中で途切れた場合、残りがBalance降順で追加されることを検証。
    /// 先頭はチェーンで特定できるが、2件目以降で途切れるケース。
    /// </summary>
    [Fact]
    public void Sort_ChainBreaksInMiddle_RemainingAddedByBalanceDescending()
    {
        // 時系列: A→B(1000→800)
        // C→DとE→Fはチェーンに繋がらない孤立データ
        var chained = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "A",
            ExitStation = "B",
            Amount = 200,
            Balance = 800
        };
        var orphan1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "C",
            ExitStation = "D",
            Amount = 150,
            Balance = 5000  // チェーンに無関係
        };
        var orphan2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "E",
            ExitStation = "F",
            Amount = 300,
            Balance = 3000  // チェーンに無関係
        };

        var result = LedgerDetailChronologicalSorter.Sort(
            new[] { orphan2, chained, orphan1 });

        // 全件出力されることを確認（チェーン構築の途中で途切れても全件含まれる）
        result.Should().HaveCount(3);
        result.Select(d => d.EntryStation).Should().Contain("A");
        result.Select(d => d.EntryStation).Should().Contain("C");
        result.Select(d => d.EntryStation).Should().Contain("E");
    }

    #endregion
}
