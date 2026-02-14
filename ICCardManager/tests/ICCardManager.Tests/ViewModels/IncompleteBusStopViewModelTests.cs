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
/// IncompleteBusStopViewModelの単体テスト（Issue #672, #703）
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

    /// <summary>
    /// 利用日フィルタの選択肢が正しく構築されること（Issue #703）
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldBuildDateFilterOptions()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD001", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 300 },
            new Ledger { Id = 3, CardIdm = "CARD002", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 150 },
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

        // Assert: "すべて" + 2つの重複しない日付（降順）
        _viewModel.DateFilterOptions.Should().HaveCount(3);
        _viewModel.DateFilterOptions[0].Should().Be("すべて");
        _viewModel.DateFilterOptions[1].Should().Be("2026/01/15");
        _viewModel.DateFilterOptions[2].Should().Be("2026/01/10");
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

    #region 利用日フィルタテスト（Issue #703）

    /// <summary>
    /// 利用日フィルタで絞り込みが機能すること
    /// </summary>
    [Fact]
    public async Task SelectedDateFilter_ShouldFilterItemsByDate()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD001", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 300 },
            new Ledger { Id = 3, CardIdm = "CARD002", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 150 },
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
        _viewModel.SelectedDateFilter = "2026/01/10";

        // Assert
        _viewModel.Items.Should().HaveCount(2);
        _viewModel.Items.Should().OnlyContain(i => i.DateDisplay == "2026/01/10");
    }

    /// <summary>
    /// 利用日フィルタで「すべて」を選択するとリセットされること
    /// </summary>
    [Fact]
    public async Task SelectedDateFilter_AllOption_ShouldShowAllItems()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD001", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 300 },
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

        await _viewModel.InitializeAsync();
        _viewModel.SelectedDateFilter = "2026/01/10";
        _viewModel.Items.Should().HaveCount(1);

        // Act
        _viewModel.SelectedDateFilter = "すべて";

        // Assert
        _viewModel.Items.Should().HaveCount(2);
    }

    /// <summary>
    /// 利用日フィルタとカードフィルタの併用が機能すること（Issue #703）
    /// </summary>
    [Fact]
    public async Task CombinedFilters_ShouldFilterByBothDateAndCard()
    {
        // Arrange: 3件のデータ（2日付×2カード、ただし1つは別日付）
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD002", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 300 },
            new Ledger { Id = 3, CardIdm = "CARD001", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 150 },
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
        _viewModel.Items.Should().HaveCount(3);

        // Act: 日付とカードの両方で絞り込み
        _viewModel.SelectedDateFilter = "2026/01/10";
        _viewModel.SelectedCardFilter = "はやかけん 001";

        // Assert: 1/10のはやかけん001の1件のみ
        _viewModel.Items.Should().HaveCount(1);
        _viewModel.Items[0].LedgerId.Should().Be(1);
    }

    #endregion

    #region フィルタ条件の保持テスト（Issue #703）

    /// <summary>
    /// 再読み込み時にフィルタ条件が維持されること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldPreserveFilters_WhenReloading()
    {
        // Arrange: 初回読み込み
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD002", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 300 },
            new Ledger { Id = 3, CardIdm = "CARD001", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 150 },
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
        _viewModel.SelectedDateFilter = "2026/01/15";
        _viewModel.SelectedCardFilter = "はやかけん 001";
        _viewModel.Items.Should().HaveCount(1);

        // Act: 再読み込み（バス停名入力後の更新を模擬）
        await _viewModel.InitializeAsync();

        // Assert: フィルタ条件が維持されていること
        _viewModel.SelectedDateFilter.Should().Be("2026/01/15");
        _viewModel.SelectedCardFilter.Should().Be("はやかけん 001");
        _viewModel.Items.Should().HaveCount(1);
    }

    /// <summary>
    /// 再読み込み時にフィルタ値が選択肢から消えた場合は「すべて」にリセットされること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldResetFilter_WhenFilterValueNoLongerExists()
    {
        // Arrange: 初回読み込み（2日分のデータ）
        var initialLedgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
            new Ledger { Id = 2, CardIdm = "CARD001", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 300 },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(initialLedgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        await _viewModel.InitializeAsync();
        _viewModel.SelectedDateFilter = "2026/01/15";

        // Act: 再読み込み時に1/15のデータが消えている（バス停名入力済みになった）
        var updatedLedgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200 },
        };
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(updatedLedgers);

        await _viewModel.InitializeAsync();

        // Assert: 選択していた日付が選択肢から消えたので「すべて」にリセット
        _viewModel.SelectedDateFilter.Should().Be("すべて");
        _viewModel.Items.Should().HaveCount(1);
    }

    #endregion

    #region SelectedLedgerId テスト（Issue #703）

    /// <summary>
    /// 選択された項目のLedgerIdが取得できること
    /// </summary>
    [Fact]
    public void SelectedLedgerId_WithSelectedItem_ShouldReturnLedgerId()
    {
        // Arrange
        _viewModel.SelectedItem = new IncompleteBusStopItem
        {
            LedgerId = 42,
            CardIdm = "CARD001",
            CardDisplayName = "はやかけん 001"
        };

        // Act & Assert
        _viewModel.SelectedLedgerId.Should().Be(42);
    }

    /// <summary>
    /// 選択されていない場合にnullが返ること
    /// </summary>
    [Fact]
    public void SelectedLedgerId_WithoutSelectedItem_ShouldReturnNull()
    {
        // Arrange
        _viewModel.SelectedItem = null;

        // Act & Assert
        _viewModel.SelectedLedgerId.Should().BeNull();
    }

    #endregion
}
