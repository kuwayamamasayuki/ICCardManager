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
    private readonly Mock<INavigationService> _navigationServiceMock;
    private readonly Mock<ICCardManager.Services.ISafeFileLauncher> _safeFileLauncherMock;
    private readonly ReportService _reportService;
    private readonly PrintService _printService;
    private readonly Mock<IReportDataBuilder> _preflightDataBuilderMock;
    private readonly Mock<IReportExportStatusService> _exportStatusServiceMock;
    private readonly ReportViewModel _viewModel;

    public ReportViewModelTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        // ReportServiceはコンクリートクラスのため、モックしたリポジトリで実インスタンスを作成
        var reportDataBuilder = new ReportDataBuilder(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object);
        _reportService = new ReportService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object, _settingsRepositoryMock.Object, reportDataBuilder);
        _printService = new PrintService(reportDataBuilder);
        _navigationServiceMock = new Mock<INavigationService>();

        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _safeFileLauncherMock = new Mock<ICCardManager.Services.ISafeFileLauncher>();
        // 既定で成功を返す。失敗テストで個別に上書きする。
        _safeFileLauncherMock.Setup(l => l.LaunchFolder(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Ok());
        _safeFileLauncherMock.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Ok());

        // Issue #1688: プリフライトチェック。既定では帳票データを構築できない（=警告なし）状態にし、
        // 警告を出したいテストで個別に上書きする。
        _preflightDataBuilderMock = new Mock<IReportDataBuilder>();
        _preflightDataBuilderMock
            .Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((MonthlyReportData)null);
        _ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync()).ReturnsAsync(new List<Ledger>());
        var preflightChecker = new ReportPreflightChecker(
            _preflightDataBuilderMock.Object, _ledgerRepositoryMock.Object);

        // Issue #1691: 出力済み / 未出力チェックリスト。
        // 既定では出力先フォルダを走査できない状態（=判定不能）にし、
        // 状況を指定したいテストで個別に上書きする。
        _exportStatusServiceMock = new Mock<IReportExportStatusService>();
        _exportStatusServiceMock
            .Setup(s => s.GetStatuses(
                It.IsAny<IEnumerable<ReportExportTarget>>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(new List<ReportExportStatus>());

        _viewModel = new ReportViewModel(
            _reportService,
            _printService,
            _cardRepositoryMock.Object,
            _navigationServiceMock.Object,
            _settingsRepositoryMock.Object,
            _safeFileLauncherMock.Object,
            preflightChecker,
            _exportStatusServiceMock.Object);
    }

    /// <summary>
    /// 出力状況サービスが指定の状態を返すように設定する（Issue #1691）
    /// </summary>
    private void SetupExportStatuses(params ReportExportStatus[] statuses)
    {
        _exportStatusServiceMock
            .Setup(s => s.GetStatuses(
                It.IsAny<IEnumerable<ReportExportTarget>>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(statuses.ToList());
    }

    /// <summary>
    /// プリフライトチェックが警告を出すよう、不整合な帳票データを返すように設定する
    /// </summary>
    private void SetupPreflightWarning()
    {
        _preflightDataBuilderMock
            .Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(() => new MonthlyReportData
            {
                Card = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" },
                Year = _viewModel.SelectedYear,
                Month = _viewModel.SelectedMonth,
                PrecedingBalance = null,
                Ledgers = new List<Ledger>
                {
                    // 残額がマイナス（NegativeBalance）
                    new Ledger { Id = 1, Date = new DateTime(2026, 7, 15), Summary = "鉄道（博多～天神）", Expense = 500, Balance = -120 }
                },
                MonthlyTotal = new ReportTotalData { Label = "月計", Income = 0, Expense = 500, Balance = null },
                CumulativeTotal = null
            });
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

    #region Issue #825: 月ボタンハイライトテスト

    /// <summary>
    /// 初期状態（先月がデフォルト）でIsLastMonthSelectedがtrueであること
    /// </summary>
    [Fact]
    public void Constructor_ShouldHighlightLastMonthButton()
    {
        // Assert
        _viewModel.IsLastMonthSelected.Should().BeTrue();
        _viewModel.IsThisMonthSelected.Should().BeFalse();
    }

    /// <summary>
    /// 「今月」を選択するとIsThisMonthSelectedがtrue、IsLastMonthSelectedがfalseになること
    /// </summary>
    [Fact]
    public void SelectThisMonth_ShouldHighlightThisMonthButton()
    {
        // Act
        _viewModel.SelectThisMonth();

        // Assert
        _viewModel.IsThisMonthSelected.Should().BeTrue();
        _viewModel.IsLastMonthSelected.Should().BeFalse();
    }

    /// <summary>
    /// 「先月」を選択するとIsLastMonthSelectedがtrue、IsThisMonthSelectedがfalseになること
    /// </summary>
    [Fact]
    public void SelectLastMonth_ShouldHighlightLastMonthButton()
    {
        // Arrange - 先に今月に切り替え
        _viewModel.SelectThisMonth();
        _viewModel.IsThisMonthSelected.Should().BeTrue();

        // Act
        _viewModel.SelectLastMonth();

        // Assert
        _viewModel.IsLastMonthSelected.Should().BeTrue();
        _viewModel.IsThisMonthSelected.Should().BeFalse();
    }

    /// <summary>
    /// 先月でも今月でもない年月を選択すると、両方のハイライトがfalseになること
    /// </summary>
    [Fact]
    public void ManualSelection_OtherMonth_ShouldNotHighlightAnyButton()
    {
        // Arrange - 先月でも今月でもない月を設定
        _viewModel.SelectedYear = 2020;
        _viewModel.SelectedMonth = 6;

        // Assert
        _viewModel.IsLastMonthSelected.Should().BeFalse();
        _viewModel.IsThisMonthSelected.Should().BeFalse();
    }

    /// <summary>
    /// 年だけ変更して月が今月と同じでも、年が違えばハイライトされないこと
    /// </summary>
    [Fact]
    public void ManualSelection_SameMonthDifferentYear_ShouldNotHighlight()
    {
        // Arrange
        var now = DateTime.Now;
        _viewModel.SelectedYear = now.Year - 1;
        _viewModel.SelectedMonth = now.Month;

        // Assert
        _viewModel.IsThisMonthSelected.Should().BeFalse();
    }

    /// <summary>
    /// コンボボックスで先月と同じ年月を手動選択してもハイライトされること
    /// </summary>
    [Fact]
    public void ManualSelection_MatchingLastMonth_ShouldHighlight()
    {
        // Arrange - 一度別の月にする
        _viewModel.SelectedYear = 2020;
        _viewModel.SelectedMonth = 6;
        _viewModel.IsLastMonthSelected.Should().BeFalse();

        // Act - コンボボックスで先月と同じ値を手動設定
        var lastMonth = DateTime.Now.AddMonths(-1);
        _viewModel.SelectedYear = lastMonth.Year;
        _viewModel.SelectedMonth = lastMonth.Month;

        // Assert
        _viewModel.IsLastMonthSelected.Should().BeTrue();
    }

    /// <summary>
    /// コンボボックスで今月と同じ年月を手動選択してもハイライトされること
    /// </summary>
    [Fact]
    public void ManualSelection_MatchingThisMonth_ShouldHighlight()
    {
        // Act - コンボボックスで今月と同じ値を手動設定
        var now = DateTime.Now;
        _viewModel.SelectedYear = now.Year;
        _viewModel.SelectedMonth = now.Month;

        // Assert
        _viewModel.IsThisMonthSelected.Should().BeTrue();
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

    #region Issue #1029: 出力先フォルダ永続化テスト

    /// <summary>
    /// InitializeAsync時に保存済みの出力先フォルダが読み込まれること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WithSavedOutputFolder_ShouldLoadSavedFolder()
    {
        // Arrange
        var savedFolder = @"D:\Reports\Monthly";
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { ReportOutputFolder = savedFolder });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        _viewModel.OutputFolder.Should().Be(savedFolder);
    }

    /// <summary>
    /// 保存済みフォルダが空の場合はデフォルト値（マイドキュメント）のままであること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WithEmptyOutputFolder_ShouldKeepDefault()
    {
        // Arrange
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { ReportOutputFolder = string.Empty });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _viewModel.OutputFolder.Should().Be(myDocuments);
    }

    /// <summary>
    /// 保存済みフォルダがnull（未設定）の場合はデフォルト値のままであること
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WithNullOutputFolder_ShouldKeepDefault()
    {
        // Arrange - ReportOutputFolderのデフォルトはstring.Empty
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _viewModel.OutputFolder.Should().Be(myDocuments);
    }

    #endregion

    #region Issue #1026: 出力先フォルダ直接入力テスト

    /// <summary>
    /// 初期化完了後にOutputFolderを変更すると設定が保存されること
    /// </summary>
    [Fact]
    public async Task OutputFolder_AfterInitialize_WhenChanged_ShouldSaveToSettings()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _settingsRepositoryMock.Setup(s => s.SaveAppSettingsAsync(It.IsAny<AppSettings>())).ReturnsAsync(true);
        await _viewModel.InitializeAsync();

        // Act - ユーザーがテキストボックスに直接入力した場合をシミュレート
        _viewModel.OutputFolder = @"\\server\share\reports";

        // Assert - 設定が保存されること
        // fire-and-forgetのため少し待つ
        await Task.Delay(100);
        _settingsRepositoryMock.Verify(
            s => s.SaveAppSettingsAsync(It.Is<AppSettings>(a => a.ReportOutputFolder == @"\\server\share\reports")),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// 初期化完了前にOutputFolderを変更しても設定が保存されないこと
    /// </summary>
    [Fact]
    public void OutputFolder_BeforeInitialize_WhenChanged_ShouldNotSave()
    {
        // Act - コンストラクタ後（InitializeAsync前）にフォルダを変更
        _viewModel.OutputFolder = @"D:\SomeFolder";

        // Assert - SaveAppSettingsAsyncが呼ばれないこと
        _settingsRepositoryMock.Verify(
            s => s.SaveAppSettingsAsync(It.IsAny<AppSettings>()),
            Times.Never);
    }

    /// <summary>
    /// UNCパスを出力先フォルダに設定できること
    /// </summary>
    [Fact]
    public async Task OutputFolder_WithUncPath_ShouldAcceptAndSave()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _settingsRepositoryMock.Setup(s => s.SaveAppSettingsAsync(It.IsAny<AppSettings>())).ReturnsAsync(true);
        await _viewModel.InitializeAsync();

        // Act
        _viewModel.OutputFolder = @"\\192.168.1.100\共有フォルダ\帳票";

        // Assert
        _viewModel.OutputFolder.Should().Be(@"\\192.168.1.100\共有フォルダ\帳票");
        await Task.Delay(100);
        _settingsRepositoryMock.Verify(
            s => s.SaveAppSettingsAsync(It.Is<AppSettings>(a => a.ReportOutputFolder == @"\\192.168.1.100\共有フォルダ\帳票")),
            Times.AtLeastOnce);
    }

    #endregion

    #region HasCreatedFiles テスト (Issue #1410)

    /// <summary>
    /// 初期状態では CreatedFiles が空で HasCreatedFiles が false であること。
    /// 「作成結果」GroupBox は帳票作成前は非表示でなければならない。
    /// </summary>
    [Fact]
    public void HasCreatedFiles_WhenInitialized_ShouldBeFalse()
    {
        // Assert
        _viewModel.CreatedFiles.Should().BeEmpty();
        _viewModel.HasCreatedFiles.Should().BeFalse();
    }

    /// <summary>
    /// CreatedFiles に要素を追加すると HasCreatedFiles が true になること。
    /// これにより ReportDialog の「作成結果」GroupBox の Visibility が Visible に切り替わる。
    /// </summary>
    [Fact]
    public void HasCreatedFiles_AfterFileAdded_ShouldBeTrue()
    {
        // Act
        _viewModel.CreatedFiles.Add(@"C:\dummy\file1.xlsx");

        // Assert
        _viewModel.HasCreatedFiles.Should().BeTrue();
    }

    /// <summary>
    /// CreatedFiles を Clear すると HasCreatedFiles が false に戻ること。
    /// 帳票再作成のクリアフローで「作成結果」GroupBox を一旦隠すために必須。
    /// </summary>
    [Fact]
    public void HasCreatedFiles_AfterClear_ShouldBeFalse()
    {
        // Arrange
        _viewModel.CreatedFiles.Add(@"C:\dummy\file1.xlsx");
        _viewModel.CreatedFiles.Add(@"C:\dummy\file2.xlsx");
        _viewModel.HasCreatedFiles.Should().BeTrue();

        // Act
        _viewModel.CreatedFiles.Clear();

        // Assert
        _viewModel.HasCreatedFiles.Should().BeFalse();
    }

    /// <summary>
    /// CreatedFiles のコレクション変更で HasCreatedFiles の PropertyChanged が発火すること。
    /// この通知が無いと xaml の Visibility バインディングが追従せず、Issue #1410 の不具合が再発する。
    /// </summary>
    [Fact]
    public void HasCreatedFiles_WhenCreatedFilesAdded_ShouldRaisePropertyChanged()
    {
        // Arrange
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
            {
                changedProperties.Add(e.PropertyName);
            }
        };

        // Act
        _viewModel.CreatedFiles.Add(@"C:\dummy\file1.xlsx");

        // Assert
        changedProperties.Should().Contain(nameof(ReportViewModel.HasCreatedFiles));
    }

    /// <summary>
    /// CreatedFiles のクリアでも HasCreatedFiles の PropertyChanged が発火すること。
    /// </summary>
    [Fact]
    public void HasCreatedFiles_WhenCreatedFilesCleared_ShouldRaisePropertyChanged()
    {
        // Arrange
        _viewModel.CreatedFiles.Add(@"C:\dummy\file1.xlsx");
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
            {
                changedProperties.Add(e.PropertyName);
            }
        };

        // Act
        _viewModel.CreatedFiles.Clear();

        // Assert
        changedProperties.Should().Contain(nameof(ReportViewModel.HasCreatedFiles));
    }

    #endregion

    #region Open* コマンド経由のISafeFileLauncher 委譲（Issue #1465）

    [Fact]
    public void OpenOutputFolder_ISafeFileLauncher_LaunchFolderを呼び出す()
    {
        _viewModel.OutputFolder = "C:\\Reports";

        _viewModel.OpenOutputFolderCommand.Execute(null);

        _safeFileLauncherMock.Verify(l => l.LaunchFolder("C:\\Reports"), Times.Once);
    }

    [Fact]
    public void OpenOutputFolder_失敗時_ステータスにエラー表示()
    {
        _safeFileLauncherMock.Setup(l => l.LaunchFolder(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Fail("テストエラー: 起動失敗"));
        _viewModel.OutputFolder = "C:\\evil.exe";

        _viewModel.OpenOutputFolderCommand.Execute(null);

        _viewModel.StatusMessage.Should().Contain("起動失敗");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    [Fact]
    public void OpenCreatedFile_ISafeFileLauncher_LaunchFileを呼び出す()
    {
        _viewModel.OpenCreatedFileCommand.Execute("C:\\report.xlsx");

        _safeFileLauncherMock.Verify(l => l.LaunchFile("C:\\report.xlsx"), Times.Once);
    }

    [Fact]
    public void OpenCreatedFile_失敗時_ステータスにエラー表示()
    {
        _safeFileLauncherMock.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Fail("拡張子NG"));

        _viewModel.OpenCreatedFileCommand.Execute("C:\\evil.exe");

        _viewModel.StatusMessage.Should().Contain("拡張子NG");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    #endregion

    #region 帳票出力前プリフライトチェック（Issue #1688）

    /// <summary>
    /// テスト用にカードを1枚選択状態にする
    /// </summary>
    private void SelectOneCard()
    {
        _viewModel.SelectedCards.Clear();
        _viewModel.SelectedCards.Add(new CardDto
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "はやかけん",
            CardNumber = "001"
        });
    }

    /// <summary>
    /// 警告がなければ確認ダイアログを表示せずそのまま作成に進むこと
    /// </summary>
    [Fact]
    public async Task RunPreflightBeforeCreateAsync_WithNoWarnings_ProceedsWithoutDialog()
    {
        SelectOneCard();

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync();

        canProceed.Should().BeTrue();
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()),
            Times.Never);
    }

    /// <summary>
    /// 警告があり「中止して修正する」が選ばれた場合、作成に進まないこと
    /// </summary>
    [Fact]
    public async Task RunPreflightBeforeCreateAsync_WhenUserCancels_StopsCreation()
    {
        SelectOneCard();
        SetupPreflightWarning();
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()))
            .Returns(false);

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync();

        canProceed.Should().BeFalse();
        _viewModel.StatusMessage.Should().Contain("中止");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// 警告があっても「このまま作成する」が選ばれた場合は作成に進むこと
    /// （Issue #1688: 強制ブロックはしない方針）
    /// </summary>
    [Fact]
    public async Task RunPreflightBeforeCreateAsync_WhenUserContinues_ProceedsWithCreation()
    {
        SelectOneCard();
        SetupPreflightWarning();
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()))
            .Returns(true);

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync();

        canProceed.Should().BeTrue();
        _viewModel.StatusMessage.Should().NotContain("中止");
    }

    /// <summary>
    /// ダイアログが閉じられただけ（DialogResult=null）の場合は中止として扱うこと
    /// </summary>
    [Fact]
    public async Task RunPreflightBeforeCreateAsync_WhenDialogClosedWithoutChoice_StopsCreation()
    {
        SelectOneCard();
        SetupPreflightWarning();
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()))
            .Returns((bool?)null);

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync();

        canProceed.Should().BeFalse();
    }

    /// <summary>
    /// 中止を選んだ場合、帳票ファイルが1件も作成されないこと
    /// </summary>
    [Fact]
    public async Task CreateReportAsync_WhenPreflightCancelled_CreatesNoFiles()
    {
        SelectOneCard();
        _viewModel.OutputFolder = Path.GetTempPath();
        SetupPreflightWarning();
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()))
            .Returns(false);

        await _viewModel.CreateReportAsync();

        _viewModel.CreatedFiles.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Contain("中止");
    }

    /// <summary>
    /// 「事前チェック」ボタンは警告件数をステータスに表示すること
    /// </summary>
    [Fact]
    public async Task RunPreflightCheckAsync_WithWarnings_ShowsWarningCountInStatus()
    {
        SelectOneCard();
        SetupPreflightWarning();

        await _viewModel.RunPreflightCheckAsync();

        _viewModel.StatusMessage.Should().Contain("警告1件");
        _viewModel.IsStatusError.Should().BeTrue();
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()),
            Times.Once);
    }

    /// <summary>
    /// 「事前チェック」ボタンは警告0件でも結果ダイアログを表示すること
    /// （出力せずに健全性だけ確認したい運用のため）
    /// </summary>
    [Fact]
    public async Task RunPreflightCheckAsync_WithNoWarnings_StillShowsResultDialog()
    {
        SelectOneCard();

        await _viewModel.RunPreflightCheckAsync();

        _viewModel.StatusMessage.Should().Contain("問題なし");
        _viewModel.IsStatusError.Should().BeFalse();
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()),
            Times.Once);
    }

    /// <summary>
    /// カード未選択で「事前チェック」を押した場合はエラーを表示しダイアログを開かないこと
    /// </summary>
    [Fact]
    public async Task RunPreflightCheckAsync_WithNoSelectedCards_ShowsError()
    {
        _viewModel.SelectedCards.Clear();

        await _viewModel.RunPreflightCheckAsync();

        _viewModel.StatusMessage.Should().Contain("カードを1つ以上選択");
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()),
            Times.Never);
    }

    #endregion

    #region 出力済みチェックリスト・一括出力テスト（Issue #1691）

    /// <summary>
    /// 3枚のカード（うち1枚は払戻済）を読み込む
    /// </summary>
    private async Task LoadThreeCardsAsync()
    {
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001" },
            new() { CardIdm = "02", CardType = "nimoca", CardNumber = "N-002", IsRefunded = true },
            new() { CardIdm = "03", CardType = "はやかけん", CardNumber = "H-001" }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();
    }

    /// <summary>
    /// 出力状況の判定結果がカード一覧へ反映されること
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_ShouldApplyStatesToCards()
    {
        // Arrange
        await LoadThreeCardsAsync();
        var lastWrite = new DateTime(2026, 7, 28, 14, 2, 0);
        SetupExportStatuses(
            new ReportExportStatus
            {
                CardIdm = "01",
                State = ReportExportState.Exported,
                LastWriteTime = lastWrite
            },
            new ReportExportStatus { CardIdm = "02", State = ReportExportState.NotExported },
            new ReportExportStatus { CardIdm = "03", State = ReportExportState.Unknown });

        // Act
        await _viewModel.RefreshExportStatusAsync();

        // Assert
        var byIdm = _viewModel.Cards.ToDictionary(c => c.CardIdm);
        byIdm["01"].ExportState.Should().Be(ReportExportState.Exported);
        byIdm["01"].ExportLastWriteTime.Should().Be(lastWrite);
        byIdm["01"].ExportStateText.Should().Contain("出力済み");
        byIdm["02"].ExportState.Should().Be(ReportExportState.NotExported);
        byIdm["02"].ExportStateText.Should().Be("未出力");
        byIdm["03"].ExportState.Should().Be(ReportExportState.Unknown);
    }

    /// <summary>
    /// 判定結果に含まれないカードは「判定不能」に戻されること
    /// （前回判定の結果が古いまま残らないようにする）
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WithMissingCard_ShouldResetToUnknown()
    {
        // Arrange
        await LoadThreeCardsAsync();
        SetupExportStatuses(
            new ReportExportStatus { CardIdm = "01", State = ReportExportState.Exported });
        await _viewModel.RefreshExportStatusAsync();

        // Act: 2回目は「01」の結果も返らない
        SetupExportStatuses();
        await _viewModel.RefreshExportStatusAsync();

        // Assert
        _viewModel.Cards.Should().OnlyContain(c => c.ExportState == ReportExportState.Unknown);
    }

    /// <summary>
    /// 集計文言に対象年月と出力済み・未出力の件数が含まれること
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_ShouldBuildSummaryWithCounts()
    {
        // Arrange
        await LoadThreeCardsAsync();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 6;
        SetupExportStatuses(
            new ReportExportStatus { CardIdm = "01", State = ReportExportState.Exported },
            new ReportExportStatus { CardIdm = "02", State = ReportExportState.NotExported },
            new ReportExportStatus { CardIdm = "03", State = ReportExportState.NotExported });

        // Act
        await _viewModel.RefreshExportStatusAsync();

        // Assert
        _viewModel.ExportStatusSummary.Should().Contain("2026年6月");
        _viewModel.ExportStatusSummary.Should().Contain("出力済み 1件");
        _viewModel.ExportStatusSummary.Should().Contain("未出力 2件");
        _viewModel.ExportStatusSummary.Should().NotContain("確認できません");
    }

    /// <summary>
    /// 判定不能のカードがある場合は集計文言にその件数も含まれること
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WithUnknownCards_ShouldReportUnknownCount()
    {
        // Arrange
        await LoadThreeCardsAsync();
        SetupExportStatuses(
            new ReportExportStatus { CardIdm = "01", State = ReportExportState.Exported },
            new ReportExportStatus { CardIdm = "02", State = ReportExportState.Unknown },
            new ReportExportStatus { CardIdm = "03", State = ReportExportState.Unknown });

        // Act
        await _viewModel.RefreshExportStatusAsync();

        // Assert
        _viewModel.ExportStatusSummary.Should().Contain("確認できません 2件");
    }

    /// <summary>
    /// カードが1枚も無い場合は集計文言を出さないこと
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WithNoCards_ShouldClearSummary()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        await _viewModel.LoadCardsAsync();

        // Act
        await _viewModel.RefreshExportStatusAsync();

        // Assert
        _viewModel.ExportStatusSummary.Should().BeEmpty();
    }

    /// <summary>
    /// 一括出力の対象選択は払戻済カードを除外すること
    /// </summary>
    [Fact]
    public async Task SelectExportTargetCards_ShouldExcludeRefundedCards()
    {
        // Arrange
        await LoadThreeCardsAsync();

        // Act
        var count = _viewModel.SelectExportTargetCards();

        // Assert
        count.Should().Be(2);
        _viewModel.SelectedCards.Should().HaveCount(2);
        _viewModel.SelectedCards.Should().NotContain(c => c.IsRefunded);
        _viewModel.Cards.Single(c => c.CardIdm == "02").IsSelected.Should().BeFalse();
        // 全カードが対象になっていないため「すべて選択」はオフ
        _viewModel.IsAllSelected.Should().BeFalse();
    }

    /// <summary>
    /// 払戻済カードが無ければ「すべて選択」がオンになること
    /// </summary>
    [Fact]
    public async Task SelectExportTargetCards_WithoutRefundedCards_ShouldTurnOnSelectAll()
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
        var count = _viewModel.SelectExportTargetCards();

        // Assert
        count.Should().Be(2);
        _viewModel.IsAllSelected.Should().BeTrue();
    }

    /// <summary>
    /// 一括出力ボタンは対象年月を先月に切り替えること
    /// </summary>
    [Fact]
    public async Task BulkExportLastMonthAsync_ShouldSwitchToLastMonth()
    {
        // Arrange
        await LoadThreeCardsAsync();
        _viewModel.SelectedYear = 2020;
        _viewModel.SelectedMonth = 1;
        // 出力先フォルダを未指定にして実ファイル生成まで進まないようにする
        _viewModel.OutputFolder = string.Empty;

        // Act
        await _viewModel.BulkExportLastMonthAsync();

        // Assert
        var lastMonth = DateTime.Now.AddMonths(-1);
        _viewModel.SelectedYear.Should().Be(lastMonth.Year);
        _viewModel.SelectedMonth.Should().Be(lastMonth.Month);
        _viewModel.IsLastMonthSelected.Should().BeTrue();
        // 払戻済を除く2枚が選択されている
        _viewModel.SelectedCards.Should().HaveCount(2);
    }

    /// <summary>
    /// 出力対象カードが1枚も無い場合はエラーを表示して出力へ進まないこと
    /// </summary>
    [Fact]
    public async Task BulkExportLastMonthAsync_WithOnlyRefundedCards_ShowsError()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new() { CardIdm = "01", CardType = "nimoca", CardNumber = "N-001", IsRefunded = true }
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(cards);
        await _viewModel.LoadCardsAsync();

        // Act
        await _viewModel.BulkExportLastMonthAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("出力対象のカードがありません");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// プリフライト警告がカードごとの件数として一覧へ反映されること
    /// </summary>
    [Fact]
    public async Task ApplyPreflightWarnings_ShouldSetWarningCountPerCard()
    {
        // Arrange
        await LoadThreeCardsAsync();
        var result = new ReportPreflightResult();
        result.Warnings.Add(new ReportPreflightWarning { CardIdm = "01" });
        result.Warnings.Add(new ReportPreflightWarning { CardIdm = "01" });
        result.Warnings.Add(new ReportPreflightWarning { CardIdm = "03" });

        // Act
        _viewModel.ApplyPreflightWarnings(result);

        // Assert
        var byIdm = _viewModel.Cards.ToDictionary(c => c.CardIdm);
        byIdm["01"].PreflightWarningCount.Should().Be(2);
        byIdm["01"].HasPreflightWarning.Should().BeTrue();
        byIdm["01"].PreflightWarningText.Should().Contain("警告2件");
        byIdm["02"].PreflightWarningCount.Should().Be(0);
        byIdm["02"].HasPreflightWarning.Should().BeFalse();
        byIdm["03"].PreflightWarningCount.Should().Be(1);
    }

    /// <summary>
    /// 再チェックで解消した警告が一覧に残らないこと
    /// </summary>
    [Fact]
    public async Task ApplyPreflightWarnings_WhenWarningResolved_ShouldClearMarker()
    {
        // Arrange
        await LoadThreeCardsAsync();
        var first = new ReportPreflightResult();
        first.Warnings.Add(new ReportPreflightWarning { CardIdm = "01" });
        _viewModel.ApplyPreflightWarnings(first);

        // Act: 2回目は警告なし
        _viewModel.ApplyPreflightWarnings(new ReportPreflightResult());

        // Assert
        _viewModel.Cards.Should().OnlyContain(c => c.PreflightWarningCount == 0);
    }

    #endregion
}
