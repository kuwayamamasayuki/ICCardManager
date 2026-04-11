using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// DashboardServiceの単体テスト
/// データ結合・ソート・残高警告判定を検証する。
/// </summary>
public class DashboardServiceTests
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly DashboardService _service;

    public DashboardServiceTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();

        _service = new DashboardService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _settingsRepositoryMock.Object);
    }

    private void SetupRepositories(
        IEnumerable<IcCard> cards,
        Dictionary<string, (int Balance, DateTime? LastUsageDate)>? balances = null,
        IEnumerable<Staff>? staff = null,
        int warningBalance = 1000)
    {
        _settingsRepositoryMock
            .Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = warningBalance });
        _cardRepositoryMock.Setup(c => c.GetAllAsync()).ReturnsAsync(cards.ToList());
        _ledgerRepositoryMock
            .Setup(l => l.GetAllLatestBalancesAsync())
            .ReturnsAsync(balances ?? new Dictionary<string, (int, DateTime?)>());
        _staffRepositoryMock
            .Setup(s => s.GetAllAsync())
            .ReturnsAsync((staff ?? Enumerable.Empty<Staff>()).ToList());
    }

    #region BuildDashboardAsync — データ結合

    [Fact]
    public async Task BuildDashboardAsync_カードと残高と職員名を結合すること()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "0102030405060708", CardType = "はやかけん", CardNumber = "H-001", IsLent = true, LastLentStaff = "STAFF00000000001" }
        };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["0102030405060708"] = (5000, new DateTime(2026, 3, 15))
        };
        var staff = new[] { new Staff { StaffIdm = "STAFF00000000001", Name = "山田太郎" } };

        SetupRepositories(cards, balances, staff, warningBalance: 1000);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items.Should().HaveCount(1);
        var item = result.Items[0];
        item.CardIdm.Should().Be("0102030405060708");
        item.CardType.Should().Be("はやかけん");
        item.CardNumber.Should().Be("H-001");
        item.CurrentBalance.Should().Be(5000);
        item.LastUsageDate.Should().Be(new DateTime(2026, 3, 15));
        item.IsLent.Should().BeTrue();
        item.LentStaffName.Should().Be("山田太郎", "貸出中の場合は職員名が解決される");
        result.WarningBalance.Should().Be(1000);
    }

    [Fact]
    public async Task BuildDashboardAsync_balancesに該当キーがない場合は残高0最終利用日nullになること()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "MISSING_BALANCE_KEY", CardType = "nimoca", CardNumber = "N-001" }
        };
        SetupRepositories(cards); // balances は空

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].CurrentBalance.Should().Be(0, "balances辞書にキーがない場合は0にフォールバック");
        result.Items[0].LastUsageDate.Should().BeNull("balances辞書にキーがない場合はnullにフォールバック");
    }

    [Fact]
    public async Task BuildDashboardAsync_貸出中だがLastLentStaffがnullの場合はLentStaffNameもnull()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "0102030405060708", CardType = "SUGOCA", CardNumber = "S-001", IsLent = true, LastLentStaff = null }
        };
        SetupRepositories(cards);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].LentStaffName.Should().BeNull();
    }

    [Fact]
    public async Task BuildDashboardAsync_LastLentStaffに該当する職員が存在しない場合はLentStaffNameもnull()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "0102030405060708", IsLent = true, LastLentStaff = "MISSING_STAFF" }
        };
        var staff = new[] { new Staff { StaffIdm = "STAFF00000000001", Name = "別人" } };
        SetupRepositories(cards, staff: staff);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].LentStaffName.Should().BeNull("職員辞書に存在しない場合はnull");
    }

    [Fact]
    public async Task BuildDashboardAsync_未貸出の場合はLentStaffNameは常にnull()
    {
        // Arrange
        var cards = new[]
        {
            // IsLent=false でも LastLentStaff に値がある場合（履歴的にあり得る）
            new IcCard { CardIdm = "0102030405060708", IsLent = false, LastLentStaff = "STAFF00000000001" }
        };
        var staff = new[] { new Staff { StaffIdm = "STAFF00000000001", Name = "山田太郎" } };
        SetupRepositories(cards, staff: staff);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].LentStaffName.Should().BeNull("未貸出時は名前を表示しない");
    }

    #endregion

    #region BuildDashboardAsync — 残高警告境界値

    [Theory]
    [InlineData(999, 1000, true, "残高 < 閾値 → 警告（境界値-1）")]
    [InlineData(1000, 1000, true, "残高 == 閾値 → 警告（境界値）")]
    [InlineData(1001, 1000, false, "残高 > 閾値 → 警告なし（境界値+1）")]
    [InlineData(0, 1000, true, "残高ゼロは警告対象")]
    public async Task BuildDashboardAsync_残高警告判定_境界値(int balance, int warningThreshold, bool expectedWarning, string reason)
    {
        // Arrange
        var cards = new[] { new IcCard { CardIdm = "0102030405060708", CardType = "はやかけん", CardNumber = "H-001" } };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["0102030405060708"] = (balance, null)
        };
        SetupRepositories(cards, balances, warningBalance: warningThreshold);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].IsBalanceWarning.Should().Be(expectedWarning, reason);
    }

    #endregion

    #region SortItems — ソート順

    private static List<CardBalanceDashboardItem> CreateUnsortedItems()
    {
        return new List<CardBalanceDashboardItem>
        {
            new() { CardType = "nimoca",   CardNumber = "N-002", CurrentBalance = 500,  LastUsageDate = new DateTime(2026, 1, 5) },
            new() { CardType = "はやかけん", CardNumber = "H-001", CurrentBalance = 3000, LastUsageDate = new DateTime(2026, 3, 1) },
            new() { CardType = "SUGOCA",   CardNumber = "S-001", CurrentBalance = 1500, LastUsageDate = null },
            new() { CardType = "はやかけん", CardNumber = "H-002", CurrentBalance = 3000, LastUsageDate = new DateTime(2026, 2, 1) },
        };
    }

    [Fact]
    public void SortItems_BalanceAscending_残高昇順でソートされ同額はカード種別順()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.BalanceAscending);

        sorted.Select(s => s.CurrentBalance).Should().BeInAscendingOrder();
        // 同額(3000)のうち、はやかけんH-001 → H-002の順
        var threes = sorted.Where(s => s.CurrentBalance == 3000).ToList();
        threes.Should().HaveCount(2);
        threes[0].CardNumber.Should().Be("H-001");
        threes[1].CardNumber.Should().Be("H-002");
    }

    [Fact]
    public void SortItems_BalanceDescending_残高降順でソートされること()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.BalanceDescending);

        sorted.Select(s => s.CurrentBalance).Should().BeInDescendingOrder();
    }

    [Fact]
    public void SortItems_LastUsageDate_最新利用日順でソートされnullは末尾()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.LastUsageDate);

        sorted[0].LastUsageDate.Should().Be(new DateTime(2026, 3, 1));
        sorted[1].LastUsageDate.Should().Be(new DateTime(2026, 2, 1));
        sorted[2].LastUsageDate.Should().Be(new DateTime(2026, 1, 5));
        sorted[3].LastUsageDate.Should().BeNull("nullは最古として末尾に配置される");
    }

    [Fact]
    public void SortItems_CardName_カード種別と番号順でソートされること()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.CardName);

        // 各カード種別内で番号順になっていればOK（種別ごとの相対順は CardSortExtensions に依存）
        var hayakaken = sorted.Where(s => s.CardType == "はやかけん").ToList();
        hayakaken[0].CardNumber.Should().Be("H-001");
        hayakaken[1].CardNumber.Should().Be("H-002");
    }

    [Fact]
    public void SortItems_空リストは空リストを返すこと()
    {
        var sorted = _service.SortItems(new List<CardBalanceDashboardItem>(), DashboardSortOrder.CardName);

        sorted.Should().BeEmpty();
    }

    #endregion
}
