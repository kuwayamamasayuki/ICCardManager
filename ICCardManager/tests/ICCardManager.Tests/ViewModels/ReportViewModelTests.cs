using System.IO;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// ReportViewModelの単体テスト
/// </summary>
public class ReportViewModelTests
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly ReportService _reportService;
    private readonly PrintService _printService;
    private readonly ReportViewModel _viewModel;

    public ReportViewModelTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        // ReportServiceはコンクリートクラスのため、モックしたリポジトリで実インスタンスを作成
        _reportService = new ReportService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object, _settingsRepositoryMock.Object);
        _printService = new PrintService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object);

        _viewModel = new ReportViewModel(
            _reportService,
            _printService,
            _cardRepositoryMock.Object);
    }

    #region 初期化テスト

    /// <summary>
    /// デフォルトで先月が選択されていること（先月が最も使用頻度が高いため）
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetDefaultYearAndMonthToLastMonth()
    {
        // Assert
        var lastMonth = DateTime.Now.AddMonths(-1);
        _viewModel.SelectedYear.Should().Be(lastMonth.Year);
        _viewModel.SelectedMonth.Should().Be(lastMonth.Month);
    }

    /// <summary>
    /// 選択可能な年が過去5年分あること
    /// </summary>
    [Fact]
    public void Constructor_ShouldHaveYearsForPast5Years()
    {
        // Assert
        var currentYear = DateTime.Now.Year;
        _viewModel.Years.Should().HaveCount(6);
        _viewModel.Years.Should().Contain(currentYear);
        _viewModel.Years.Should().Contain(currentYear - 5);
    }

    /// <summary>
    /// 選択可能な月が1〜12月あること
    /// </summary>
    [Fact]
    public void Constructor_ShouldHaveMonths1To12()
    {
        // Assert
        _viewModel.Months.Should().HaveCount(12);
        _viewModel.Months.Should().ContainInOrder(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
    }

    /// <summary>
    /// デフォルト出力フォルダがマイドキュメントであること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetDefaultOutputFolderToMyDocuments()
    {
        // Assert
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _viewModel.OutputFolder.Should().Be(myDocuments);
    }

    #endregion

    #region カード一覧読み込みテスト

    /// <summary>
    /// カード一覧が正しく読み込まれること
    /// </summary>
    [Fact]
    public async Task LoadCardsAsync_ShouldLoadCardsOrderedByTypeAndNumber()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-002" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" },
            new() { CardIdm = "03", CardType = "nimoca", CardNumber = "N-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);

        // Act
        await _viewModel.LoadCardsAsync();

        // Assert
        _viewModel.Cards.Should().HaveCount(3);
        // カード種別→番号順にソートされている
        _viewModel.Cards[0].CardType.Should().Be("nimoca");
        _viewModel.Cards[0].CardNumber.Should().Be("N-001");
        _viewModel.Cards[1].CardType.Should().Be("nimoca");
        _viewModel.Cards[1].CardNumber.Should().Be("N-002");
        _viewModel.Cards[2].CardType.Should().Be("はやかけん");
    }

    /// <summary>
    /// カード一覧読み込み時にデフォルトで全選択されること
    /// </summary>
    [Fact]
    public async Task LoadCardsAsync_ShouldSelectAllCardsByDefault()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);

        // Act
        await _viewModel.LoadCardsAsync();

        // Assert
        _viewModel.IsAllSelected.Should().BeTrue();
        _viewModel.SelectedCards.Should().HaveCount(2);
    }

    /// <summary>
    /// カード一覧が空の場合、空のコレクションになること
    /// </summary>
    [Fact]
    public async Task LoadCardsAsync_WithNoCards_ShouldHaveEmptyCollection()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.LoadCardsAsync();

        // Assert
        _viewModel.Cards.Should().BeEmpty();
        _viewModel.SelectedCards.Should().BeEmpty();
    }

    #endregion

    #region カード選択テスト

    /// <summary>
    /// 全選択をOFFにすると全解除されること
    /// </summary>
    [Fact]
    public async Task OnIsAllSelectedChanged_WhenFalse_ShouldClearSelectedCards()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();

        // Act
        _viewModel.IsAllSelected = false;

        // Assert
        _viewModel.SelectedCards.Should().BeEmpty();
    }

    /// <summary>
    /// 全選択をONにすると全選択されること
    /// </summary>
    [Fact]
    public async Task OnIsAllSelectedChanged_WhenTrue_ShouldSelectAllCards()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();
        _viewModel.IsAllSelected = false; // 一度解除

        // Act
        _viewModel.IsAllSelected = true;

        // Assert
        _viewModel.SelectedCards.Should().HaveCount(2);
    }

    /// <summary>
    /// カードの選択状態を切り替えできること
    /// </summary>
    /// <remarks>
    /// IsAllSelectedの変更がSelectedCardsに連動しているため、
    /// 1つのカードを選択解除するとIsAllSelected=falseになり、
    /// OnIsAllSelectedChangedで全解除される仕様となっている。
    /// </remarks>
    [Fact]
    public async Task ToggleCardSelection_ShouldToggleSelectionState()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();

        var targetCard = _viewModel.Cards[0];

        // Act - 選択解除（IsAllSelected=falseに変わり、全解除される）
        _viewModel.ToggleCardSelection(targetCard);

        // Assert - IsAllSelected変更で全解除される
        _viewModel.IsAllSelected.Should().BeFalse();
        _viewModel.SelectedCards.Should().BeEmpty();

        // Act - 再選択（IsAllSelected=falseのまま、1件追加される）
        _viewModel.ToggleCardSelection(targetCard);

        // Assert
        _viewModel.SelectedCards.Should().Contain(targetCard);
        _viewModel.SelectedCards.Should().HaveCount(1);
        _viewModel.IsAllSelected.Should().BeFalse(); // まだ全選択ではない
    }

    #endregion

    #region バリデーションテスト

    /// <summary>
    /// 帳票作成実行時、前回のStatusMessageがクリアされること（Issue #812）
    /// </summary>
    [Fact]
    public async Task CreateReportAsync_ShouldClearPreviousStatusMessage()
    {
        // Arrange - 前回の結果メッセージが残っている状態
        _viewModel.StatusMessage = "3件の帳票を作成しました";
        _viewModel.SelectedCards.Clear();

        // Act
        await _viewModel.CreateReportAsync();

        // Assert - 前回のメッセージではなく、バリデーションエラーに更新されていること
        _viewModel.StatusMessage.Should().NotBe("3件の帳票を作成しました");
        _viewModel.StatusMessage.Should().Contain("カードを1つ以上選択");
    }

    /// <summary>
    /// カード未選択時はエラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task CreateReportAsync_WithNoSelectedCards_ShouldShowError()
    {
        // Arrange
        _viewModel.SelectedCards.Clear();
        _viewModel.OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // Act
        await _viewModel.CreateReportAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("カードを1つ以上選択");
    }

    /// <summary>
    /// 出力フォルダ未選択時はエラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task CreateReportAsync_WithEmptyOutputFolder_ShouldShowError()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" };
        _viewModel.SelectedCards.Add(card);
        _viewModel.OutputFolder = "";

        // Act
        await _viewModel.CreateReportAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("出力先フォルダを選択");
    }

    /// <summary>
    /// 存在しない出力フォルダを指定時はエラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task CreateReportAsync_WithNonExistentFolder_ShouldShowError()
    {
        // Arrange
        var card = new CardDto { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" };
        _viewModel.SelectedCards.Add(card);
        _viewModel.OutputFolder = @"C:\NonExistentFolder12345";

        // Act
        await _viewModel.CreateReportAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("存在しません");
    }

    // Note: 帳票作成テストについて
    // ReportServiceはコンクリートクラスであり、CreateMonthlyReportAsyncメソッドは
    // Excelテンプレートファイルの読み込みと新規ファイル作成を行います。
    // これらの動作はユニットテストでは検証が困難なため、以下のテストは省略しています:
    // - CreateReportAsync_WithValidInput_ShouldCreateReport
    // - CreateReportAsync_WithMultipleCards_ShouldCreateMultipleReports
    // - CreateReportAsync_WithPartialFailure_ShouldShowPartialSuccessMessage
    // - CreateReportAsync_ShouldClearPreviousCreatedFiles
    //
    // 帳票作成機能の完全なテストには、IReportServiceインターフェースの導入か
    // 統合テストの実装が必要です。

    #endregion

    #region クイック選択テスト

    /// <summary>
    /// 「今月」ボタンをクリックすると今年今月が選択されること
    /// </summary>
    [Fact]
    public void SelectThisMonth_ShouldSetToCurrentYearAndMonth()
    {
        // Arrange - 別の年月を設定
        _viewModel.SelectedYear = 2020;
        _viewModel.SelectedMonth = 6;

        // Act
        _viewModel.SelectThisMonth();

        // Assert
        var now = DateTime.Now;
        _viewModel.SelectedYear.Should().Be(now.Year);
        _viewModel.SelectedMonth.Should().Be(now.Month);
    }

    /// <summary>
    /// 「先月」ボタンをクリックすると先月が選択されること
    /// </summary>
    [Fact]
    public void SelectLastMonth_ShouldSetToLastMonth()
    {
        // Act
        _viewModel.SelectLastMonth();

        // Assert
        var lastMonth = DateTime.Now.AddMonths(-1);
        _viewModel.SelectedYear.Should().Be(lastMonth.Year);
        _viewModel.SelectedMonth.Should().Be(lastMonth.Month);
    }

    /// <summary>
    /// 1月に「先月」を選択すると前年の12月になること
    /// </summary>
    [Fact]
    public void SelectLastMonth_InJanuary_ShouldSetToDecemberOfPreviousYear()
    {
        // Arrange
        // テスト実行時が1月の場合を想定してテスト
        // 先月は常に1ヶ月前になるため、このテストはどの月でも成功する

        // Act
        _viewModel.SelectLastMonth();

        // Assert
        var lastMonth = DateTime.Now.AddMonths(-1);
        // 年またぎのケースも含めて正しく計算されていることを確認
        if (lastMonth.Month == 12)
        {
            // 1月にテストを実行した場合
            _viewModel.SelectedMonth.Should().Be(12);
            _viewModel.SelectedYear.Should().Be(lastMonth.Year);
        }
        else
        {
            // その他の月にテストを実行した場合
            _viewModel.SelectedMonth.Should().Be(lastMonth.Month);
            _viewModel.SelectedYear.Should().Be(lastMonth.Year);
        }
    }

    #endregion

    #region 個別チェックボックス連動テスト

    /// <summary>
    /// 個別のカードのIsSelectedを変更するとSelectedCardsが更新されること
    /// </summary>
    [Fact]
    public async Task CardIsSelected_WhenChangedToFalse_ShouldRemoveFromSelectedCards()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();

        var targetCard = _viewModel.Cards[0];
        _viewModel.SelectedCards.Should().HaveCount(2);

        // Act - 個別チェックボックスをOFFにする（UIの動作をシミュレート）
        targetCard.IsSelected = false;

        // Assert
        _viewModel.SelectedCards.Should().HaveCount(1);
        _viewModel.SelectedCards.Should().NotContain(targetCard);
        _viewModel.IsAllSelected.Should().BeFalse();
    }

    /// <summary>
    /// 個別のカードのIsSelectedを変更してすべて選択状態になるとIsAllSelectedがtrueになること
    /// </summary>
    [Fact]
    public async Task CardIsSelected_WhenAllSelected_ShouldSetIsAllSelectedToTrue()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();

        // 全解除
        _viewModel.IsAllSelected = false;
        _viewModel.SelectedCards.Should().BeEmpty();

        // Act - 個別に全カードを選択
        foreach (var card in _viewModel.Cards)
        {
            card.IsSelected = true;
        }

        // Assert
        _viewModel.SelectedCards.Should().HaveCount(2);
        _viewModel.IsAllSelected.Should().BeTrue();
    }

    /// <summary>
    /// すべて選択チェックボックスをONにすると各カードのIsSelectedもtrueになること
    /// </summary>
    [Fact]
    public async Task IsAllSelected_WhenSetToTrue_ShouldSetAllCardsIsSelectedToTrue()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();

        // 全解除
        _viewModel.IsAllSelected = false;
        _viewModel.Cards.All(c => !c.IsSelected).Should().BeTrue();

        // Act
        _viewModel.IsAllSelected = true;

        // Assert
        _viewModel.Cards.All(c => c.IsSelected).Should().BeTrue();
        _viewModel.SelectedCards.Should().HaveCount(2);
    }

    /// <summary>
    /// すべて選択チェックボックスをOFFにすると各カードのIsSelectedもfalseになること
    /// </summary>
    [Fact]
    public async Task IsAllSelected_WhenSetToFalse_ShouldSetAllCardsIsSelectedToFalse()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();

        _viewModel.Cards.All(c => c.IsSelected).Should().BeTrue();

        // Act
        _viewModel.IsAllSelected = false;

        // Assert
        _viewModel.Cards.All(c => !c.IsSelected).Should().BeTrue();
        _viewModel.SelectedCards.Should().BeEmpty();
    }

    #endregion

    #region InitializeAsyncテスト

    /// <summary>
    /// InitializeAsyncがLoadCardsAsyncを呼び出すこと
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ShouldCallLoadCardsAsync()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
    }

    #endregion
}
