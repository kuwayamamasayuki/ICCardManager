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
            {
                propertyChanged = true;
            }
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

    #region HasBalanceInconsistency (PropertyChanged) — Issue #1052

    [Fact]
    public void HasBalanceInconsistency_変更時にPropertyChangedイベントが発火すること()
    {
        var dto = new LedgerDto();
        var propertyChanged = false;
        dto.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LedgerDto.HasBalanceInconsistency))
            {
                propertyChanged = true;
            }
        };

        dto.HasBalanceInconsistency = true;

        propertyChanged.Should().BeTrue();
    }

    [Fact]
    public void HasBalanceInconsistency_同じ値を設定した場合にPropertyChangedイベントが発火しないこと()
    {
        var dto = new LedgerDto { HasBalanceInconsistency = false };
        var propertyChanged = false;
        dto.PropertyChanged += (_, _) => propertyChanged = true;

        dto.HasBalanceInconsistency = false;

        propertyChanged.Should().BeFalse();
    }

    [Fact]
    public void BalanceInconsistencyMessage_初期値は空文字であること()
    {
        var dto = new LedgerDto();

        dto.BalanceInconsistencyMessage.Should().BeEmpty();
    }

    [Fact]
    public void BalanceInconsistencyMessage_設定した値が取得できること()
    {
        var dto = new LedgerDto
        {
            BalanceInconsistencyMessage = "残高不整合: 期待値 1,000円 / 実際 800円"
        };

        dto.BalanceInconsistencyMessage.Should().Be("残高不整合: 期待値 1,000円 / 実際 800円");
    }

    #endregion

    #region IsRecentlyRecorded（Issue #1907）

    [Fact]
    public void RecentlyRecordedMark_今回記録した行は文字でも示すこと()
    {
        // 色（行背景）だけに頼らず「今回」列の文字で示す（4 要素原則）
        new LedgerDto { IsRecentlyRecorded = true }.RecentlyRecordedMark.Should().Be("✔");
        new LedgerDto { IsRecentlyRecorded = false }.RecentlyRecordedMark.Should().BeEmpty("既定は印なし");
    }

    #endregion

    #region SummaryToolTip（Issue #2424）

    // 摘要は残りの幅を取る最も広い列で、セルの ToolTip が行の ToolTip（#1052 / #1907）を行の大部分で覆う。
    // 行の文言と摘要の全文を合わせて返すことを、行の状態ごとに対で固定する

    private const string LongSummary = "鉄道（博多～天神南、薬院～福岡空港）";
    private const string InconsistencyMessage = "残高不整合: 期待値 1,000円 / 実際 900円";

    [Fact]
    public void SummaryToolTip_通常の行は摘要の全文だけを返すこと()
    {
        new LedgerDto { Summary = LongSummary }.SummaryToolTip.Should().Be(LongSummary);
    }

    [Fact]
    public void SummaryToolTip_残高不整合の行は不整合の説明と摘要の全文を合わせて返すこと()
    {
        var dto = new LedgerDto { Summary = LongSummary, HasBalanceInconsistency = true, BalanceInconsistencyMessage = InconsistencyMessage };

        dto.SummaryToolTip.Should().Be($"{InconsistencyMessage}\n摘要: {LongSummary}",
            "摘要の上にマウスを置いても、行の ToolTip（残高不整合の説明）を読めること");
    }

    [Fact]
    public void SummaryToolTip_今回の返却で記録された行は行の文言と摘要の全文を合わせて返すこと()
    {
        var dto = new LedgerDto { Summary = LongSummary, IsRecentlyRecorded = true };

        dto.SummaryToolTip.Should().Be($"今回の返却で記録された行\n摘要: {LongSummary}");
        LedgerDto.RecentlyRecordedToolTip.Should().Be("今回の返却で記録された行", "行の ToolTip と同じ文言（XAML は同じ定数を参照する）");
    }

    [Fact]
    public void SummaryToolTip_残高不整合と今回の返却が重なれば行のスタイルと同じく残高不整合を優先すること()
    {
        var dto = new LedgerDto
        {
            Summary = LongSummary,
            IsRecentlyRecorded = true,
            HasBalanceInconsistency = true,
            BalanceInconsistencyMessage = InconsistencyMessage,
        };

        dto.SummaryToolTip.Should().Be($"{InconsistencyMessage}\n摘要: {LongSummary}");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SummaryToolTip_摘要が空なら通常の行はnullで_行の文言がある行は行の文言だけを返すこと(string? summary)
    {
        // null: 中身の無いツールチップを出さず、行の ToolTip を覆わない
        new LedgerDto { Summary = summary! }.SummaryToolTip.Should().BeNull();
        new LedgerDto { Summary = summary!, IsRecentlyRecorded = true }.SummaryToolTip.Should().Be(LedgerDto.RecentlyRecordedToolTip);
    }

    [Fact]
    public void SummaryToolTip_行の状態が変わったら変更を通知すること()
    {
        // 残高不整合のマーカーは表示中の行へ後から付け外しする（ApplyBalanceInconsistencyMarkers）。通知が無いと ToolTip が古いまま残る
        var dto = new LedgerDto { Summary = LongSummary };
        var changed = new List<string?>();
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.HasBalanceInconsistency = true;
        dto.BalanceInconsistencyMessage = InconsistencyMessage;
        dto.IsRecentlyRecorded = true;

        changed.Should().Contain(new[] { nameof(LedgerDto.HasBalanceInconsistency), nameof(LedgerDto.BalanceInconsistencyMessage), nameof(LedgerDto.IsRecentlyRecorded) });
        changed.FindAll(n => n == nameof(LedgerDto.SummaryToolTip)).Should().HaveCount(3, "3 つの変化それぞれで SummaryToolTip の変更を通知すること");
        dto.SummaryToolTip.Should().Be($"{InconsistencyMessage}\n摘要: {LongSummary}");
    }

    #endregion
}
