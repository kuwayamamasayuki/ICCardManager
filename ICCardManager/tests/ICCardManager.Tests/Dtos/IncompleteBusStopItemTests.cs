using System;
using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// IncompleteBusStopItemの表示用プロパティの単体テスト
/// </summary>
public class IncompleteBusStopItemTests
{
    [Fact]
    public void DateDisplay_日付がフォーマットされること()
    {
        var item = new IncompleteBusStopItem
        {
            Date = new DateTime(2025, 7, 1)
        };

        item.DateDisplay.Should().Be("2025/07/01");
    }

    [Fact]
    public void ExpenseDisplay_金額が正の場合に円付きで表示すること()
    {
        var item = new IncompleteBusStopItem { Expense = 230 };

        item.ExpenseDisplay.Should().Be("230円");
    }

    [Fact]
    public void ExpenseDisplay_金額が大きい場合に3桁区切りで表示すること()
    {
        var item = new IncompleteBusStopItem { Expense = 1500 };

        item.ExpenseDisplay.Should().Be("1,500円");
    }

    [Fact]
    public void ExpenseDisplay_金額がゼロの場合に空文字を返すこと()
    {
        var item = new IncompleteBusStopItem { Expense = 0 };

        item.ExpenseDisplay.Should().BeEmpty();
    }
}
