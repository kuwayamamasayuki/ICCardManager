using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// LedgerDtoの表示用プロパティの単体テスト
/// </summary>
public class LedgerDtoTests
{
    #region IncomeDisplay / ExpenseDisplay

    [Fact]
    public void IncomeDisplay_受入金額がある場合に3桁区切りで表示すること()
    {
        var dto = new LedgerDto { Income = 3000 };

        dto.IncomeDisplay.Should().Be("3,000");
    }

    [Fact]
    public void IncomeDisplay_受入金額がゼロの場合に空文字を返すこと()
    {
        var dto = new LedgerDto { Income = 0 };

        dto.IncomeDisplay.Should().BeEmpty();
    }

    [Fact]
    public void ExpenseDisplay_払出金額がある場合に3桁区切りで表示すること()
    {
        var dto = new LedgerDto { Expense = 210 };

        dto.ExpenseDisplay.Should().Be("210");
    }

    [Fact]
    public void ExpenseDisplay_払出金額がゼロの場合に空文字を返すこと()
    {
        var dto = new LedgerDto { Expense = 0 };

        dto.ExpenseDisplay.Should().BeEmpty();
    }

    #endregion

    #region BalanceDisplay

    [Fact]
    public void BalanceDisplay_残額を3桁区切りで表示すること()
    {
        var dto = new LedgerDto { Balance = 12345 };

        dto.BalanceDisplay.Should().Be("12,345");
    }

    [Fact]
    public void BalanceDisplay_ゼロの場合にゼロを表示すること()
    {
        var dto = new LedgerDto { Balance = 0 };

        dto.BalanceDisplay.Should().Be("0");
    }

    #endregion

    #region HasIncome / HasExpense

    [Fact]
    public void HasIncome_受入金額が正の場合trueを返すこと()
    {
        var dto = new LedgerDto { Income = 1000 };

        dto.HasIncome.Should().BeTrue();
    }

    [Fact]
    public void HasIncome_受入金額がゼロの場合falseを返すこと()
    {
        var dto = new LedgerDto { Income = 0 };

        dto.HasIncome.Should().BeFalse();
    }

    [Fact]
    public void HasExpense_払出金額が正の場合trueを返すこと()
    {
        var dto = new LedgerDto { Expense = 500 };

        dto.HasExpense.Should().BeTrue();
    }

    [Fact]
    public void HasExpense_払出金額がゼロの場合falseを返すこと()
    {
        var dto = new LedgerDto { Expense = 0 };

        dto.HasExpense.Should().BeFalse();
    }

    #endregion

    #region DetailCount / HasDetails

    [Fact]
    public void DetailCount_Detailsリストがある場合にその件数を返すこと()
    {
        var dto = new LedgerDto
        {
            Details = new List<LedgerDetailDto>
            {
                new LedgerDetailDto(),
                new LedgerDetailDto(),
                new LedgerDetailDto()
            }
        };

        dto.DetailCount.Should().Be(3);
    }

    [Fact]
    public void DetailCount_Detailsが空の場合にDetailCountValueを返すこと()
    {
        var dto = new LedgerDto
        {
            Details = new List<LedgerDetailDto>(),
            DetailCountValue = 5
        };

        dto.DetailCount.Should().Be(5);
    }

    [Fact]
    public void DetailCount_Detailsがnullの場合にDetailCountValueを返すこと()
    {
        var dto = new LedgerDto
        {
            Details = null,
            DetailCountValue = 3
        };

        dto.DetailCount.Should().Be(3);
    }

    [Fact]
    public void HasDetails_詳細が2件以上の場合trueを返すこと()
    {
        var dto = new LedgerDto
        {
            Details = new List<LedgerDetailDto>
            {
                new LedgerDetailDto(),
                new LedgerDetailDto()
            }
        };

        dto.HasDetails.Should().BeTrue();
    }

    [Fact]
    public void HasDetails_詳細が1件の場合falseを返すこと()
    {
        var dto = new LedgerDto
        {
            Details = new List<LedgerDetailDto>
            {
                new LedgerDetailDto()
            }
        };

        dto.HasDetails.Should().BeFalse();
    }

    [Fact]
    public void HasDetails_詳細がない場合falseを返すこと()
    {
        var dto = new LedgerDto { Details = new List<LedgerDetailDto>() };

        dto.HasDetails.Should().BeFalse();
    }

    #endregion

    #region IsChecked (PropertyChanged)

    [Fact]
    public void IsChecked_変更時にPropertyChangedイベントが発火すること()
    {
        var dto = new LedgerDto();
        var propertyChanged = false;
        dto.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LedgerDto.IsChecked))
                propertyChanged = true;
        };

        dto.IsChecked = true;

        propertyChanged.Should().BeTrue();
    }

    [Fact]
    public void IsChecked_同じ値を設定した場合にPropertyChangedイベントが発火しないこと()
    {
        var dto = new LedgerDto { IsChecked = false };
        var propertyChanged = false;
        dto.PropertyChanged += (_, _) => propertyChanged = true;

        dto.IsChecked = false;

        propertyChanged.Should().BeFalse();
    }

    #endregion
}
