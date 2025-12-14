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
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 0));
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.InitializeAsync(card);

        // Assert
        _viewModel.Card.Should().Be(card);
        _ledgerRepositoryMock.Verify(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    #endregion

    #region 履歴読み込みテスト

    /// <summary>
    /// 履歴が正しく読み込まれること
    /// </summary>
    [Fact]
    public async Task LoadHistoryAsync_ShouldLoadLedgers()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;

        var ledgers = new List<Ledger>
        {
            new() { Id = 3, CardIdm = card.CardIdm, Date = DateTime.Today, Summary = "鉄道", Balance = 1500 },
            new() { Id = 2, CardIdm = card.CardIdm, Date = DateTime.Today.AddDays(-1), Summary = "チャージ", Balance = 2000 },
            new() { Id = 1, CardIdm = card.CardIdm, Date = DateTime.Today.AddDays(-2), Summary = "鉄道", Balance = 1000 }
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ledgers, 3));
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers.First());

        // Act
        await _viewModel.LoadHistoryAsync();

        // Assert
        _viewModel.Ledgers.Should().HaveCount(3);
        _viewModel.CurrentBalance.Should().Be(1500);
        _viewModel.TotalCount.Should().Be(3);
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
        _ledgerRepositoryMock.Verify(r => r.GetPagedAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
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
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 0));
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.LoadHistoryAsync();

        // Assert
        _viewModel.Ledgers.Should().BeEmpty();
        _viewModel.CurrentBalance.Should().Be(0);
        _viewModel.TotalCount.Should().Be(0);
    }

    #endregion

    #region ページネーションテスト

    /// <summary>
    /// デフォルトのページサイズが50件であること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetDefaultPageSize()
    {
        // Assert
        _viewModel.PageSize.Should().Be(50);
        _viewModel.SelectedPageSizeItem.Should().NotBeNull();
        _viewModel.SelectedPageSizeItem!.Value.Should().Be(50);
    }

    /// <summary>
    /// ページサイズオプションが正しく設定されていること
    /// </summary>
    [Fact]
    public void Constructor_ShouldHaveCorrectPageSizeOptions()
    {
        // Assert
        _viewModel.PageSizeOptions.Should().HaveCount(4);
        _viewModel.PageSizeOptions.Select(o => o.Value).Should().ContainInOrder(25, 50, 100, 200);
    }

    /// <summary>
    /// ページ表示が正しくフォーマットされること
    /// </summary>
    [Fact]
    public void PageDisplay_ShouldFormatCorrectly()
    {
        // Arrange
        _viewModel.CurrentPage = 3;
        _viewModel.TotalPages = 10;

        // Assert
        _viewModel.PageDisplay.Should().Be("3 / 10");
    }

    /// <summary>
    /// 最初のページの場合、前に移動できないこと
    /// </summary>
    [Fact]
    public void CanGoToPrevPage_WhenOnFirstPage_ShouldBeFalse()
    {
        // Arrange
        _viewModel.CurrentPage = 1;
        _viewModel.TotalPages = 5;

        // Assert
        _viewModel.CanGoToFirstPage.Should().BeFalse();
        _viewModel.CanGoToPrevPage.Should().BeFalse();
    }

    /// <summary>
    /// 最後のページの場合、次に移動できないこと
    /// </summary>
    [Fact]
    public void CanGoToNextPage_WhenOnLastPage_ShouldBeFalse()
    {
        // Arrange
        _viewModel.CurrentPage = 5;
        _viewModel.TotalPages = 5;

        // Assert
        _viewModel.CanGoToNextPage.Should().BeFalse();
        _viewModel.CanGoToLastPage.Should().BeFalse();
    }

    /// <summary>
    /// 中間ページの場合、前後に移動可能であること
    /// </summary>
    [Fact]
    public void Navigation_WhenOnMiddlePage_ShouldAllowBothDirections()
    {
        // Arrange
        _viewModel.CurrentPage = 3;
        _viewModel.TotalPages = 5;

        // Assert
        _viewModel.CanGoToFirstPage.Should().BeTrue();
        _viewModel.CanGoToPrevPage.Should().BeTrue();
        _viewModel.CanGoToNextPage.Should().BeTrue();
        _viewModel.CanGoToLastPage.Should().BeTrue();
    }

    /// <summary>
    /// 次のページに移動できること
    /// </summary>
    [Fact]
    public async Task GoToNextPage_ShouldIncrementCurrentPage()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;
        _viewModel.CurrentPage = 1;
        _viewModel.TotalPages = 3;

        // PageSize=50なので、totalCount=150でTotalPages=3になる
        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 150));
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.GoToNextPage();

        // Assert
        _viewModel.CurrentPage.Should().Be(2);
        _viewModel.TotalPages.Should().Be(3);
    }

    /// <summary>
    /// 前のページに移動できること
    /// </summary>
    [Fact]
    public async Task GoToPrevPage_ShouldDecrementCurrentPage()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;
        _viewModel.CurrentPage = 3;
        _viewModel.TotalPages = 5;

        // PageSize=50なので、totalCount=250でTotalPages=5になる
        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 250));
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.GoToPrevPage();

        // Assert
        _viewModel.CurrentPage.Should().Be(2);
    }

    /// <summary>
    /// 最初のページに移動できること
    /// </summary>
    [Fact]
    public async Task GoToFirstPage_ShouldSetCurrentPageToOne()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;
        _viewModel.CurrentPage = 5;
        _viewModel.TotalPages = 5;

        // PageSize=50なので、totalCount=250でTotalPages=5になる
        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 250));
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.GoToFirstPage();

        // Assert
        _viewModel.CurrentPage.Should().Be(1);
    }

    /// <summary>
    /// 最後のページに移動できること
    /// </summary>
    [Fact]
    public async Task GoToLastPage_ShouldSetCurrentPageToTotalPages()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;
        _viewModel.CurrentPage = 1;
        _viewModel.TotalPages = 5;

        // PageSize=50なので、totalCount=250でTotalPages=5になる
        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 250));
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.GoToLastPage();

        // Assert
        _viewModel.CurrentPage.Should().Be(5);
    }

    /// <summary>
    /// 期間変更時にページが1にリセットされること
    /// </summary>
    [Fact]
    public void SetThisMonth_ShouldResetToPageOne()
    {
        // Arrange
        _viewModel.CurrentPage = 5;

        // Act
        _viewModel.SetThisMonth();

        // Assert
        _viewModel.CurrentPage.Should().Be(1);
    }

    /// <summary>
    /// 先月選択時にページが1にリセットされること
    /// </summary>
    [Fact]
    public void SetLastMonth_ShouldResetToPageOne()
    {
        // Arrange
        _viewModel.CurrentPage = 5;

        // Act
        _viewModel.SetLastMonth();

        // Assert
        _viewModel.CurrentPage.Should().Be(1);
    }

    /// <summary>
    /// 月選択適用時にページが1にリセットされること
    /// </summary>
    [Fact]
    public void ApplySelectedMonth_ShouldResetToPageOne()
    {
        // Arrange
        _viewModel.CurrentPage = 5;
        _viewModel.SelectedYear = 2024;
        _viewModel.SelectedMonth = 6;

        // Act
        _viewModel.ApplySelectedMonth();

        // Assert
        _viewModel.CurrentPage.Should().Be(1);
    }

    /// <summary>
    /// 総ページ数が正しく計算されること
    /// </summary>
    [Fact]
    public async Task LoadHistoryAsync_ShouldCalculateTotalPagesCorrectly()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;
        _viewModel.PageSize = 25;

        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), 25))
            .ReturnsAsync((new List<Ledger>(), 60)); // 60件を25件/ページで3ページ
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.LoadHistoryAsync();

        // Assert
        _viewModel.TotalCount.Should().Be(60);
        _viewModel.TotalPages.Should().Be(3); // 60 / 25 = 2.4 -> ceil = 3
    }

    /// <summary>
    /// 現在のページが総ページ数を超えている場合に調整されること
    /// </summary>
    [Fact]
    public async Task LoadHistoryAsync_ShouldAdjustCurrentPageIfExceedsTotalPages()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01020304050607FF", CardType = "test", CardNumber = "001" };
        _viewModel.Card = card;
        _viewModel.CurrentPage = 10;
        _viewModel.PageSize = 50;

        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), 50))
            .ReturnsAsync((new List<Ledger>(), 100)); // 100件 / 50 = 2ページ
        _ledgerRepositoryMock
            .Setup(r => r.GetLatestBeforeDateAsync(card.CardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger?)null);

        // Act
        await _viewModel.LoadHistoryAsync();

        // Assert
        _viewModel.TotalPages.Should().Be(2);
        _viewModel.CurrentPage.Should().Be(2); // 10から2に調整
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
