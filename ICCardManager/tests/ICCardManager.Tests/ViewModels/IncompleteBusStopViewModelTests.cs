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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.Items.Should().HaveCount(2);
        _viewModel.Items.Should().OnlyContain(i => i.Summary.Contains(SummaryGenerator.BusPlaceholder));
    }

    /// <summary>
    /// Issue #2329: 期間で区切らず、全期間の取得元（<c>GetBySummaryContainingAsync(プレースホルダ)</c>）から一覧を作ること。
    /// </summary>
    /// <remarks>
    /// 以前は直近 1 年（Issue #2104 のテストがその期間を固定していた）で、1 年より前の未入力は一覧から消えていた。
    /// 警告の件数（<c>WarningService.CheckIncompleteBusStopsAsync</c>）と同じ取得元を使うことも、この表明で固定する。
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_期間で区切らず全期間の未入力を一覧にすること()
    {
        // Arrange: 1 年より前（5 年前）の未入力を含む
        var today = DateTime.Today;
        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 1, CardIdm = "CARD001", Date = today, Summary = "バス（★）" },
                new Ledger { Id = 2, CardIdm = "CARD001", Date = today.AddYears(-5), Summary = "バス（★）" }
            });
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" } });

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.Items.Select(i => i.LedgerId).Should().Equal(new[] { 1, 2 }, "1 年より前の未入力も一覧に載せる");
        _ledgerRepositoryMock.Verify(r => r.GetBySummaryContainingAsync(SummaryGenerator.BusPlaceholder), Times.Once);
        _ledgerRepositoryMock.Verify(
            r => r.GetByDateRangeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Never, "期間で区切った問い合わせをしない");
    }

    /// <summary>
    /// Issue #2329: 論理削除したカードの未入力は一覧に載せず、それ以外（貸出中・払戻済みを含む）は載せること。
    /// </summary>
    /// <remarks>
    /// 全期間に広げると、論理削除したカードの未入力が最長 6 年一覧に残る（<c>GetAllAsync</c> が返さないため、
    /// カード名を解決できず IDm がそのまま出ていた）。払い戻したカードは手元に無く、ここで案内しないと直す手段が無いため残す。
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_論理削除したカードの未入力だけを一覧から外すこと()
    {
        var today = DateTime.Today;
        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 1, CardIdm = "OPERATING", Date = today, Summary = "バス（★）" },
                new Ledger { Id = 2, CardIdm = "LENT", Date = today, Summary = "バス（★）" },
                new Ledger { Id = 3, CardIdm = "REFUNDED", Date = today, Summary = "バス（★）" },
                new Ledger { Id = 4, CardIdm = "DELETED", Date = today, Summary = "バス（★）" },
                new Ledger { Id = 5, CardIdm = "MISSING", Date = today, Summary = "バス（★）" }   // 論理削除済み（GetAllAsync が返さない）
            });
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>
            {
                new IcCard { CardIdm = "OPERATING", CardType = "はやかけん", CardNumber = "001" },
                new IcCard { CardIdm = "LENT", CardType = "はやかけん", CardNumber = "002", IsLent = true },
                new IcCard { CardIdm = "REFUNDED", CardType = "はやかけん", CardNumber = "003", IsRefunded = true },
                new IcCard { CardIdm = "DELETED", CardType = "はやかけん", CardNumber = "004", IsDeleted = true }
            });

        await _viewModel.InitializeAsync();

        _viewModel.Items.Select(i => i.LedgerId).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        _viewModel.Items.Single(i => i.LedgerId == 3).CardDisplayName.Should().Be("はやかけん 003", "払戻済みカードもカード名で表示する");
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
    /// カードの一覧に居ない（論理削除済みの）カードの未入力は一覧に載せないこと
    /// </summary>
    /// <remarks>
    /// Issue #2329 で期待値を変えた。以前はカード名を解決できないまま IDm を表示していたが、
    /// 未入力の対象から論理削除したカードを除いたため、カードの一覧に居ない（＝論理削除済みの）カードの行は載せない。
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_カードの一覧に居ないカードの未入力は載せないこと()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "UNKNOWN_CARD", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 100, StaffName = "佐藤" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.Items.Should().BeEmpty("論理削除したカードの行は載せない");
    }

    /// <summary>
    /// Issue #2332: 利用者欄は、同行者がいれば履歴一覧と同じく「外N名」を付けて表示すること。
    /// </summary>
    [Theory]
    [InlineData(2, "田中 外2名")]
    [InlineData(0, "田中")] // 対: 単独利用は氏名のまま
    public async Task InitializeAsync_利用者欄は同行者数から外N名を導出すること(int companionCount, string expected)
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200, StaffName = "田中", CompanionCount = companionCount },
        };
        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" } });

        await _viewModel.InitializeAsync();

        var item = _viewModel.Items.Should().ContainSingle().Subject;
        item.DisplayStaffName.Should().Be(expected);
        item.StaffName.Should().Be("田中", "「外N名」は表示時の導出であり、氏名そのものは書き換えない（#1906）");
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
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
        _viewModel.SelectedItem = null!;

        // Act & Assert
        _viewModel.SelectedLedgerId.Should().BeNull();
    }

    #endregion

    #region UpdateItemSummaryAsync テスト（Issue #709）

    /// <summary>
    /// UpdateItemSummaryAsync: 摘要が更新されること
    /// </summary>
    [Fact]
    public async Task UpdateItemSummaryAsync_ShouldUpdateSummary()
    {
        // Arrange: 初期化してItems内に★付き摘要のアイテムを準備
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 10, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200, StaffName = "田中" },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        await _viewModel.InitializeAsync();
        _viewModel.Items.Should().HaveCount(1);
        _viewModel.Items[0].Summary.Should().Be("バス（★）");

        // GetByIdAsync が更新済みの摘要を返す
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(10))
            .ReturnsAsync(new Ledger { Id = 10, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（天神→博多）", Expense = 200, StaffName = "田中" });

        // Act
        var result = await _viewModel.UpdateItemSummaryAsync(10);

        // Assert
        result.Should().NotBeNull();
        result.Summary.Should().Be("バス（天神→博多）");
        _viewModel.Items[0].Summary.Should().Be("バス（天神→博多）");
    }

    /// <summary>
    /// Issue #2332: バス停名を入力して行を差し替えても、利用者欄の「外N名」が消えないこと。
    /// </summary>
    [Fact]
    public async Task UpdateItemSummaryAsync_同行者数を引き継ぐこと()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 10, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200, StaffName = "田中", CompanionCount = 2 },
        };
        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" } });
        await _viewModel.InitializeAsync();
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(10))
            .ReturnsAsync(new Ledger { Id = 10, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（天神→博多）", Expense = 200, StaffName = "田中", CompanionCount = 2 });

        var result = await _viewModel.UpdateItemSummaryAsync(10);

        result!.DisplayStaffName.Should().Be("田中 外2名");
        _viewModel.Items[0].DisplayStaffName.Should().Be("田中 外2名");
    }

    /// <summary>
    /// UpdateItemSummaryAsync: 存在しないledgerIdはnullを返すこと
    /// </summary>
    [Fact]
    public async Task UpdateItemSummaryAsync_ShouldReturnNull_WhenLedgerNotFound()
    {
        // Arrange
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(999))
            .ReturnsAsync((Ledger?)null);

        // Act
        var result = await _viewModel.UpdateItemSummaryAsync(999);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// UpdateItemSummaryAsync: Itemsコレクションが差し替えられ他プロパティが保持されること
    /// </summary>
    [Fact]
    public async Task UpdateItemSummaryAsync_ShouldReplaceItemAndPreserveOtherProperties()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 20, CardIdm = "CARD002", Date = new DateTime(2026, 2, 5), Summary = "バス（★）、鉄道（A駅～B駅）", Expense = 500, StaffName = "鈴木" },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        await _viewModel.InitializeAsync();
        var originalItem = _viewModel.Items[0];

        // GetByIdAsync が更新済みの摘要を返す
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(20))
            .ReturnsAsync(new Ledger { Id = 20, CardIdm = "CARD002", Date = new DateTime(2026, 2, 5), Summary = "バス（天神→博多）、鉄道（A駅～B駅）", Expense = 500, StaffName = "鈴木" });

        // Act
        var result = await _viewModel.UpdateItemSummaryAsync(20);

        // Assert: 新しいオブジェクトに差し替わる
        result.Should().NotBeSameAs(originalItem);
        _viewModel.Items[0].Should().BeSameAs(result);

        // 他のプロパティは保持される
        result.CardDisplayName.Should().Be("nimoca 002");
        result.CardIdm.Should().Be("CARD002");
        result.Date.Should().Be(new DateTime(2026, 2, 5));
        result.Expense.Should().Be(500);
        result.StaffName.Should().Be("鈴木");
        result.Summary.Should().Be("バス（天神→博多）、鉄道（A駅～B駅）");
    }

    /// <summary>
    /// UpdateItemSummaryAsync: _allItemsも更新されフィルタ適用後に正しい摘要が表示されること
    /// </summary>
    [Fact]
    public async Task UpdateItemSummaryAsync_ShouldUpdateAllItems_SoFilterReflectsNewSummary()
    {
        // Arrange: 2件のデータを準備
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 30, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（★）", Expense = 200, StaffName = "田中" },
            new Ledger { Id = 31, CardIdm = "CARD002", Date = new DateTime(2026, 1, 15), Summary = "バス（★）", Expense = 300, StaffName = "鈴木" },
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD001", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "CARD002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards);

        await _viewModel.InitializeAsync();
        _viewModel.Items.Should().HaveCount(2);

        // Id=30 のアイテムの摘要を更新
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(30))
            .ReturnsAsync(new Ledger { Id = 30, CardIdm = "CARD001", Date = new DateTime(2026, 1, 10), Summary = "バス（天神→博多）", Expense = 200, StaffName = "田中" });

        await _viewModel.UpdateItemSummaryAsync(30);

        // Act: フィルタを適用（_allItemsのデータが使われる）
        _viewModel.SelectedCardFilter = "はやかけん 001";

        // Assert: フィルタ適用後も更新された摘要が表示される
        _viewModel.Items.Should().HaveCount(1);
        _viewModel.Items[0].Summary.Should().Be("バス（天神→博多）");
    }

    /// <summary>
    /// UpdateItemSummaryAsync: Items内に該当ledgerIdが存在しない場合nullを返すこと
    /// </summary>
    [Fact]
    public async Task UpdateItemSummaryAsync_ShouldReturnNull_WhenItemNotInCollection()
    {
        // Arrange: Itemsは空だがDBにはレコードがある
        _ledgerRepositoryMock
            .Setup(r => r.GetBySummaryContainingAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<Ledger>());
        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());

        await _viewModel.InitializeAsync();
        _viewModel.Items.Should().BeEmpty();

        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(50))
            .ReturnsAsync(new Ledger { Id = 50, Summary = "バス（天神→博多）" });

        // Act
        var result = await _viewModel.UpdateItemSummaryAsync(50);

        // Assert
        result.Should().BeNull();
    }

    #endregion
}
