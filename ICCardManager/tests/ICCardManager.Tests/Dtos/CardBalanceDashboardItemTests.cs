using System;
using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// CardBalanceDashboardItemの表示用プロパティの単体テスト
/// </summary>
public class CardBalanceDashboardItemTests
{
    [Fact]
    public void DisplayName_カード種別と番号が結合されること()
    {
        var item = new CardBalanceDashboardItem
        {
            CardType = "はやかけん",
            CardNumber = "H-001"
        };

        item.DisplayName.Should().Be("はやかけん H-001");
    }

    [Fact]
    public void BalanceDisplay_3桁区切りで円マーク付きになること()
    {
        var item = new CardBalanceDashboardItem { CurrentBalance = 12345 };

        item.BalanceDisplay.Should().Be("¥12,345");
    }

    [Fact]
    public void BalanceDisplay_ゼロの場合も正しく表示されること()
    {
        var item = new CardBalanceDashboardItem { CurrentBalance = 0 };

        item.BalanceDisplay.Should().Be("¥0");
    }

    [Fact]
    public void WarningIcon_残高警告時に警告アイコンを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsBalanceWarning = true };

        item.WarningIcon.Should().Be("⚠");
    }

    [Fact]
    public void WarningIcon_残高警告なしの場合空文字を返すこと()
    {
        var item = new CardBalanceDashboardItem { IsBalanceWarning = false };

        item.WarningIcon.Should().BeEmpty();
    }

    [Fact]
    public void LentStatusIcon_貸出中の場合送信アイコンを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsLent = true };

        item.LentStatusIcon.Should().Be("📤");
    }

    [Fact]
    public void LentStatusIcon_在庫の場合受信アイコンを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsLent = false };

        item.LentStatusIcon.Should().Be("📥");
    }

    [Fact]
    public void LentStatusDisplay_貸出中の場合に正しいテキストを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsLent = true };

        item.LentStatusDisplay.Should().Be("貸出中");
    }

    [Fact]
    public void LentStatusDisplay_在庫の場合に正しいテキストを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsLent = false };

        item.LentStatusDisplay.Should().Be("在庫");
    }

    [Fact]
    public void LentInfoDisplay_貸出中で貸出者名がある場合に名前を含むこと()
    {
        var item = new CardBalanceDashboardItem
        {
            IsLent = true,
            LentStaffName = "山田太郎"
        };

        item.LentInfoDisplay.Should().Be("貸出中（山田太郎）");
    }

    [Fact]
    public void LentInfoDisplay_貸出中で貸出者名がない場合に貸出中のみ表示すること()
    {
        var item = new CardBalanceDashboardItem
        {
            IsLent = true,
            LentStaffName = null
        };

        item.LentInfoDisplay.Should().Be("貸出中");
    }

    [Fact]
    public void LentInfoDisplay_在庫の場合に在庫と表示すること()
    {
        var item = new CardBalanceDashboardItem { IsLent = false };

        item.LentInfoDisplay.Should().Be("在庫");
    }

    [Fact]
    public void LastUsageDateDisplay_日付がある場合にフォーマットされること()
    {
        var item = new CardBalanceDashboardItem
        {
            LastUsageDate = new DateTime(2025, 3, 15)
        };

        item.LastUsageDateDisplay.Should().Be("2025/03/15");
    }

    [Fact]
    public void LastUsageDateDisplay_日付がない場合にハイフンを返すこと()
    {
        var item = new CardBalanceDashboardItem { LastUsageDate = null };

        item.LastUsageDateDisplay.Should().Be("-");
    }

    [Fact]
    public void RowBackgroundColor_残高警告時に薄い赤を返すこと()
    {
        var item = new CardBalanceDashboardItem { IsBalanceWarning = true };

        item.RowBackgroundColor.Should().Be("#FFEBEE");
    }

    [Fact]
    public void RowBackgroundColor_通常時にTransparentを返すこと()
    {
        var item = new CardBalanceDashboardItem { IsBalanceWarning = false };

        item.RowBackgroundColor.Should().Be("Transparent");
    }
}
