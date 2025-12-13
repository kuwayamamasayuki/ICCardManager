using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// HistoryViewModelの単体テスト
/// </summary>
public class HistoryViewModelTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly HistoryViewModel _viewModel;

    public HistoryViewModelTests()
    {
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _cardRepositoryMock = new Mock<ICardRepository>();
        _viewModel = new HistoryViewModel(
            _ledgerRepositoryMock.Object,
            _cardRepositoryMock.Object);
    }

    #region 初期化テスト

    /// <summary>
    /// デフォルトで今月が選択されていること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetDefaultPeriodToThisMonth()
    {
        // Assert
        var today = DateTime.Today;
        _viewModel.FromDate.Should().Be(new DateTime(today.Year, today.Month, 1));
        _viewModel.ToDate.Should().Be(today);
        _viewModel.SelectedYear.Should().Be(today.Year);
        _viewModel.SelectedMonth.Should().Be(today.Month);
    }

    /// <summary>
    /// 選択可能な年が過去6年分あること
    /// </summary>
    [Fact]
    public void Constructor_ShouldHaveAvailableYearsForPast6Years()
    {
        // Assert
        var currentYear = DateTime.Today.Year;
        _viewModel.AvailableYears.Should().HaveCount(7);
        _viewModel.AvailableYears.Should().Contain(currentYear);
        _viewModel.AvailableYears.Should().Contain(currentYear - 6);
    }

    /// <summary>
    /// 選択可能な月が1〜12月あること
    /// </summary>
    [Fact]
    public void Constructor_ShouldHaveAvailableMonths1To12()
    {
        // Assert
        _viewModel.AvailableMonths.Should().HaveCount(12);
        _viewModel.AvailableMonths.Should().ContainInOrder(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
    }

    /// <summary>
    /// 選択中の期間表示が正しいこと
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetCorrectSelectedPeriodDisplay()
    {
        // Assert
        var today = DateTime.Today;
        _viewModel.SelectedPeriodDisplay.Should().Be($"{today.Year}年{today.Month}月");
    }

    #endregion

    #region カード初期化テスト

    /// <summary>
    /// カードを設定して初期化できること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldSetCardAndLoadHistory()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "はやかけん", CardNumber = "H-001" };
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.InitializeAsync(card);

        // Assert
        _viewModel.Card.Should().Be(card);
        _ledgerRepositoryMock.Verify(r => r.GetByDateRangeAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once);
    }

    #endregion

    #region 履歴読み込みテスト

    /// <summary>
    /// 履歴が正しく読み込まれること
    /// </summary>
    [Fact]
    public async Task LoadHistoryAsync_ShouldLoadLedgersOrderedByDateDesc()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;

        var ledgers = new List<Ledger>
        {
            new() { Id = 1, CardIdm = card.CardIdm, Date = DateTime.Today.AddDays(-2), Summary = "鉄道", Balance = 1000 },
            new() { Id = 2, CardIdm = card.CardIdm, Date = DateTime.Today.AddDays(-1), Summary = "チャージ", Balance = 2000 },
            new() { Id = 3, CardIdm = card.CardIdm, Date = DateTime.Today, Summary = "鉄道", Balance = 1500 }
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers.Last());

        // Act
        await _viewModel.LoadHistoryAsync();

        // Assert
        _viewModel.Ledgers.Should().HaveCount(3);
        _viewModel.Ledgers[0].Date.Should().Be(DateTime.Today); // 最新が先頭
        _viewModel.CurrentBalance.Should().Be(1500);
        _viewModel.StatusMessage.Should().Contain("3件");
    }

    /// <summary>
    /// カードが未設定の場合、履歴読み込みをスキップすること
    /// </summary>
    [Fact]
    public async Task LoadHistoryAsync_WithNoCard_ShouldDoNothing()
    {
        // Arrange
        _viewModel.Card = null;

        // Act
        await _viewModel.LoadHistoryAsync();

        // Assert
        _viewModel.Ledgers.Should().BeEmpty();
        _ledgerRepositoryMock.Verify(r => r.GetByDateRangeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Never);
    }

    /// <summary>
    /// 履歴がない場合、残高が0になること
    /// </summary>
    [Fact]
    public async Task LoadHistoryAsync_WithNoHistory_ShouldSetBalanceToZero()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.LoadHistoryAsync();

        // Assert
        _viewModel.Ledgers.Should().BeEmpty();
        _viewModel.CurrentBalance.Should().Be(0);
        _viewModel.StatusMessage.Should().Contain("0件");
    }

    #endregion

    #region 期間選択テスト

    /// <summary>
    /// 今月を選択できること
    /// </summary>
    [Fact]
    public void SetThisMonth_ShouldSetPeriodToThisMonth()
    {
        // Arrange - 一旦先月に設定
        var lastMonth = DateTime.Today.AddMonths(-1);
        _viewModel.FromDate = new DateTime(lastMonth.Year, lastMonth.Month, 1);
        _viewModel.ToDate = new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month));

        // Act
        _viewModel.SetThisMonth();

        // Assert
        var today = DateTime.Today;
        _viewModel.FromDate.Should().Be(new DateTime(today.Year, today.Month, 1));
        _viewModel.ToDate.Month.Should().Be(today.Month);
        _viewModel.SelectedYear.Should().Be(today.Year);
        _viewModel.SelectedMonth.Should().Be(today.Month);
    }

    /// <summary>
    /// 先月を選択できること
    /// </summary>
    [Fact]
    public void SetLastMonth_ShouldSetPeriodToLastMonth()
    {
        // Act
        _viewModel.SetLastMonth();

        // Assert
        var lastMonth = DateTime.Today.AddMonths(-1);
        _viewModel.FromDate.Should().Be(new DateTime(lastMonth.Year, lastMonth.Month, 1));
        _viewModel.ToDate.Should().Be(new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month)));
        _viewModel.SelectedYear.Should().Be(lastMonth.Year);
        _viewModel.SelectedMonth.Should().Be(lastMonth.Month);
    }

    /// <summary>
    /// 月選択ポップアップを開閉できること
    /// </summary>
    [Fact]
    public void OpenAndCloseMonthSelector_ShouldToggleIsMonthSelectorOpen()
    {
        // Assert - 初期状態
        _viewModel.IsMonthSelectorOpen.Should().BeFalse();

        // Act - 開く
        _viewModel.OpenMonthSelector();

        // Assert
        _viewModel.IsMonthSelectorOpen.Should().BeTrue();

        // Act - 閉じる
        _viewModel.CloseMonthSelector();

        // Assert
        _viewModel.IsMonthSelectorOpen.Should().BeFalse();
    }

    /// <summary>
    /// 選択した月を適用できること
    /// </summary>
    [Fact]
    public void ApplySelectedMonth_ShouldSetPeriodAndCloseSelector()
    {
        // Arrange
        _viewModel.SelectedYear = 2024;
        _viewModel.SelectedMonth = 6;
        _viewModel.IsMonthSelectorOpen = true;

        // Act
        _viewModel.ApplySelectedMonth();

        // Assert
        _viewModel.FromDate.Should().Be(new DateTime(2024, 6, 1));
        _viewModel.ToDate.Should().Be(new DateTime(2024, 6, 30));
        _viewModel.IsMonthSelectorOpen.Should().BeFalse();
        _viewModel.SelectedPeriodDisplay.Should().Be("2024年6月");
    }

    /// <summary>
    /// 2月の末日が正しく設定されること（閏年）
    /// </summary>
    [Fact]
    public void ApplySelectedMonth_February2024_ShouldSetCorrectEndDate()
    {
        // Arrange
        _viewModel.SelectedYear = 2024;
        _viewModel.SelectedMonth = 2;

        // Act
        _viewModel.ApplySelectedMonth();

        // Assert
        _viewModel.ToDate.Should().Be(new DateTime(2024, 2, 29)); // 2024年は閏年
    }

    /// <summary>
    /// 2月の末日が正しく設定されること（平年）
    /// </summary>
    [Fact]
    public void ApplySelectedMonth_February2023_ShouldSetCorrectEndDate()
    {
        // Arrange
        _viewModel.SelectedYear = 2023;
        _viewModel.SelectedMonth = 2;

        // Act
        _viewModel.ApplySelectedMonth();

        // Assert
        _viewModel.ToDate.Should().Be(new DateTime(2023, 2, 28)); // 2023年は平年
    }

    #endregion

    #region LedgerDtoテスト

    /// <summary>
    /// LedgerDtoが正しく表示用データを生成すること
    /// </summary>
    [Fact]
    public void LedgerDto_ShouldFormatDataCorrectly()
    {
        // Arrange
        var displayItem = new LedgerDto
        {
            Id = 1,
            CardIdm = "01020304050607FF",
            Date = new DateTime(2024, 6, 15),
            Summary = "鉄道（福岡空港駅～博多駅）",
            Income = 0,
            Expense = 260,
            Balance = 1240,
            StaffName = "田中太郎",
            Note = "テスト"
        };

        // Assert
        displayItem.Id.Should().Be(1);
        displayItem.Date.Should().Be(new DateTime(2024, 6, 15));
        displayItem.Summary.Should().Be("鉄道（福岡空港駅～博多駅）");
        displayItem.HasIncome.Should().BeFalse();
        displayItem.Expense.Should().Be(260);
        displayItem.Balance.Should().Be(1240);
        displayItem.StaffName.Should().Be("田中太郎");
        displayItem.Note.Should().Be("テスト");
        displayItem.IncomeDisplay.Should().BeEmpty();
        displayItem.ExpenseDisplay.Should().Be("-260");
        displayItem.BalanceDisplay.Should().Be("1,240");
    }

    /// <summary>
    /// チャージ時の表示が正しいこと
    /// </summary>
    [Fact]
    public void LedgerDto_WithIncome_ShouldShowIncomeDisplay()
    {
        // Arrange
        var displayItem = new LedgerDto
        {
            Id = 2,
            CardIdm = "01020304050607FF",
            Date = DateTime.Today,
            Summary = "チャージ",
            Income = 3000,
            Expense = 0,
            Balance = 4000
        };

        // Assert
        displayItem.Income.Should().Be(3000);
        displayItem.HasExpense.Should().BeFalse();
        displayItem.IncomeDisplay.Should().Be("+3,000");
        displayItem.ExpenseDisplay.Should().BeEmpty();
    }

    #endregion
}
