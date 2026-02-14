using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// IncompleteBusStopViewModelの単体テスト（Issue #672）
/// </summary>
public class IncompleteBusStopViewModelTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly IncompleteBusStopViewModel _viewModel;

    public IncompleteBusStopViewModelTests()
    {
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _cardRepositoryMock = new Mock<ICardRepository>();

        _viewModel = new IncompleteBusStopViewModel(
            _ledgerRepositoryMock.Object,
            _cardRepositoryMock.Object);
    }

    #region InitializeAsync テスト

    /// <summary>
    /// "★"を含む履歴のみがItemsに表示されること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldShowOnlyIncompleteBusStopLedgers()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200, StaffName = "田中" },
            new Ledger { Id = 2, CardIdm = "CARD001", Date = new DateTime(2026, 1, 11), Summary = "鉄道（A駅～B駅）", Expense = 300, StaffName = "田中" },
            new Ledger { Id = 3, CardIdm = "CARD002", Date = new DateTime(2026, 1, 12), Summary = "バス（★）、鉄道（C駅～D駅）", Expense = 500, StaffName = "鈴木" },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "CARD002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.Items.Should().HaveCount(2);
        _viewModel.Items.Should().OnlyContain(i => i.Summary.Contains("★"));
    }

    /// <summary>
    /// カード名が正しく解決されること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldResolveCardDisplayNames()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200, StaffName = "田中" },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.Items.Should().HaveCount(1);
        _viewModel.Items[0].CardDisplayName.Should().Be("はやかけん 001");
        _viewModel.Items[0].CardIdm.Should().Be("CARD001");
        _viewModel.Items[0].LedgerId.Should().Be(1);
    }

    /// <summary>
    /// 未登録カードの場合CardIdmがそのまま表示されること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldFallbackToCardIdm_WhenCardNotFound()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "UNKNOWN_CARD", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 100, StaffName = "佐藤" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.Items.Should().HaveCount(1);
        _viewModel.Items[0].CardDisplayName.Should().Be("UNKNOWN_CARD");
    }

    /// <summary>
    /// 日付降順でソートされること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldSortByDateDescending()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 5), Summary = "バス（★）", Expense = 100 },
            new Ledger { Id = 2, CardIdm = "CARD001", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 3, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 150 },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.Items.Should().HaveCount(3);
        _viewModel.Items[0].Date.Should().Be(new DateTime(2026, 1, 15));
        _viewModel.Items[1].Date.Should().Be(new DateTime(2026, 1, 10));
        _viewModel.Items[2].Date.Should().Be(new DateTime(2026, 1, 5));
    }

    /// <summary>
    /// カードフィルタの選択肢が正しく構築されること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldBuildCardFilterOptions()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD002", Date = new DateTime(2026, 1, 11), Summary = "バス（★）", Expense = 300 },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "CARD002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.CardFilterOptions.Should().HaveCount(3); // "すべて" + 2カード
        _viewModel.CardFilterOptions[0].Should().Be("すべて");
        _viewModel.CardFilterOptions.Should().Contain("はやかけん 001");
        _viewModel.CardFilterOptions.Should().Contain("nimoca 002");
    }

    #endregion

    #region カードフィルタテスト

    /// <summary>
    /// カード名フィルタで絞り込みが機能すること
    /// </summary>
    [Fact]
    public async Task SelectedCardFilter_ShouldFilterItemsByCardName()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD002", Date = new DateTime(2026, 1, 11), Summary = "バス（★）", Expense = 300 },
            new Ledger { Id = 3, CardIdm = "CARD001", Date = new DateTime(2026, 1, 12), Summary = "バス（★）", Expense = 150 },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "CARD002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        await _viewModel.InitializeAsync();

        // Act
        _viewModel.SelectedCardFilter = "はやかけん 001";

        // Assert
        _viewModel.Items.Should().HaveCount(2);
        _viewModel.Items.Should().OnlyContain(i => i.CardDisplayName == "はやかけん 001");
    }

    /// <summary>
    /// 「すべて」を選択するとフィルタがリセットされること
    /// </summary>
    [Fact]
    public async Task SelectedCardFilter_AllOption_ShouldShowAllItems()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD002", Date = new DateTime(2026, 1, 11), Summary = "バス（★）", Expense = 300 },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "CARD002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        await _viewModel.InitializeAsync();
        _viewModel.SelectedCardFilter = "はやかけん 001";
        _viewModel.Items.Should().HaveCount(1);

        // Act
        _viewModel.SelectedCardFilter = "すべて";

        // Assert
        _viewModel.Items.Should().HaveCount(2);
    }

    #endregion

    #region Confirmコマンドテスト

    /// <summary>
    /// 選択がある場合にIsConfirmedがtrueになること
    /// </summary>
    [Fact]
    public void Confirm_WithSelectedItem_ShouldSetIsConfirmedTrue()
    {
        // Arrange
        _viewModel.SelectedItem = new IncompleteBusStopItem
        {
            LedgerId = 1,
            CardIdm = "CARD001",
            CardDisplayName = "はやかけん 001"
        };

        // Act
        _viewModel.ConfirmCommand.Execute(null);

        // Assert
        _viewModel.IsConfirmed.Should().BeTrue();
        _viewModel.SelectedCardIdm.Should().Be("CARD001");
    }

    /// <summary>
    /// 選択がない場合にIsConfirmedがfalseのままであること
    /// </summary>
    [Fact]
    public void Confirm_WithoutSelectedItem_ShouldNotSetIsConfirmed()
    {
        // Arrange
        _viewModel.SelectedItem = null;

        // Act
        _viewModel.ConfirmCommand.Execute(null);

        // Assert
        _viewModel.IsConfirmed.Should().BeFalse();
    }

    #endregion
}
