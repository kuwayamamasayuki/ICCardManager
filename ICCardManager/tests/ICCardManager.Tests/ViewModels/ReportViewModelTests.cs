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
using System.Threading;
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
        _reportService = new ReportService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object, _settingsRepositoryMock.Object, reportDataBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportService>.Instance);
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
    //
    // Issue #1949: 上記のうち一括作成ループ（対象カードのスナップショット・件数表示）は
    // ReportService.CreateMonthlyReportAsync を virtual にして差し替える形で検査できるように
    // なりました。ReportViewModelBulkCreationTests を参照してください。

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
        const string expectedFolder = @"\\server\share\reports";
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _settingsRepositoryMock.Setup(s => s.SaveAppSettingsAsync(It.IsAny<AppSettings>())).ReturnsAsync(true);
        await _viewModel.InitializeAsync();

        // Issue #1821: fire-and-forget の完了は固定時間ではなく Callback のシグナルで待つ
        var saved = CreateSaveSignal(expectedFolder);

        // Act - ユーザーがテキストボックスに直接入力した場合をシミュレート
        _viewModel.OutputFolder = expectedFolder;

        // Assert - 設定が保存されること
        (await WaitForSignalAsync(saved)).Should().BeTrue(
            "OutputFolder の変更は SaveOutputFolderAsync により設定へ保存されるべき");
        _settingsRepositoryMock.Verify(
            s => s.SaveAppSettingsAsync(It.Is<AppSettings>(a => a.ReportOutputFolder == expectedFolder)),
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
        const string expectedFolder = @"\\192.168.1.100\共有フォルダ\帳票";
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _settingsRepositoryMock.Setup(s => s.SaveAppSettingsAsync(It.IsAny<AppSettings>())).ReturnsAsync(true);
        await _viewModel.InitializeAsync();

        // Issue #1821: fire-and-forget の完了は固定時間ではなく Callback のシグナルで待つ
        var saved = CreateSaveSignal(expectedFolder);

        // Act
        _viewModel.OutputFolder = expectedFolder;

        // Assert
        _viewModel.OutputFolder.Should().Be(expectedFolder);
        (await WaitForSignalAsync(saved)).Should().BeTrue(
            "UNC パスも SaveOutputFolderAsync により設定へ保存されるべき");
        _settingsRepositoryMock.Verify(
            s => s.SaveAppSettingsAsync(It.Is<AppSettings>(a => a.ReportOutputFolder == expectedFolder)),
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

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync(
            _viewModel.SelectedCards.ToList(), _viewModel.SelectedYear, _viewModel.SelectedMonth);

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

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync(
            _viewModel.SelectedCards.ToList(), _viewModel.SelectedYear, _viewModel.SelectedMonth);

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

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync(
            _viewModel.SelectedCards.ToList(), _viewModel.SelectedYear, _viewModel.SelectedMonth);

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

        var canProceed = await _viewModel.RunPreflightBeforeCreateAsync(
            _viewModel.SelectedCards.ToList(), _viewModel.SelectedYear, _viewModel.SelectedMonth);

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

    /// <summary>
    /// 事前チェックの帳票データ構築を待機させ、呼ばれた年月を記録する（Issue #2045）。
    /// 返す <see cref="TaskCompletionSource{T}"/> を完了させるまで検査は先へ進まない。
    /// </summary>
    private (TaskCompletionSource<bool> Entered, TaskCompletionSource<bool> Release, List<(int Year, int Month)> Calls)
        SetupBlockingPreflightBuild()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<(int Year, int Month)>();

        _preflightDataBuilderMock
            .Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(async (string _, int year, int month) =>
            {
                lock (calls)
                {
                    calls.Add((year, month));
                }
                entered.TrySetResult(true);
                await release.Task;
                // 警告を出す（確認ダイアログへ進ませる）データを、呼ばれた年月で返す
                return new MonthlyReportData
                {
                    Card = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" },
                    Year = year,
                    Month = month,
                    Ledgers = new List<Ledger>
                    {
                        new Ledger { Id = 1, Date = new DateTime(year, month, 15), Summary = "鉄道（博多～天神）", Expense = 500, Balance = -120 }
                    },
                    MonthlyTotal = new ReportTotalData { Label = "月計", Income = 0, Expense = 500, Balance = null }
                };
            });

        return (entered, release, calls);
    }

    /// <summary>
    /// 帳票作成: 事前チェックの待機中に選択年月を変えても、検査対象は作成開始時点の年月であること（Issue #2045）
    /// </summary>
    /// <remarks>
    /// 割り込み点はスレッドの競争ではなく、帳票データ構築を待機させて確定的に再現する。
    /// 中止を選ばせてファイル生成へは進ませない（検査対象の年月だけを観測する）。
    /// </remarks>
    [Fact]
    public async Task CreateReportAsync_WhenSelectedMonthChangesDuringPreflight_ChecksSnapshotMonth()
    {
        SelectOneCard();
        _viewModel.OutputFolder = Path.GetTempPath();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;
        var (entered, release, calls) = SetupBlockingPreflightBuild();
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()))
            .Returns(false);

        var creating = _viewModel.CreateReportAsync();
        await entered.Task;

        // 検査の待機中に、キーボードで月を変えた状況
        _viewModel.SelectedMonth = 6;
        _viewModel.SelectedYear = 2027;
        release.SetResult(true);
        await creating;

        calls.Should().NotBeEmpty("事前チェックが帳票データを構築したこと（検査の空振り防止）");
        calls.Should().OnlyContain(c => c.Year == 2026 && c.Month == 5,
            "検査対象は作成開始時点のスナップショット（2026年5月）であること");
        // 対の表明: 警告を検出して確認ダイアログまで到達している（検査の結果が作成判断に使われている）
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.ReportPreflightDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.ReportPreflightDialog>>()),
            Times.Once);
        _viewModel.StatusMessage.Should().Contain("中止");
    }

    /// <summary>
    /// 「事前チェック」ボタン: 検査の待機中に選択年月を変えても、検査対象は押した時点の年月であること（Issue #2045）
    /// </summary>
    [Fact]
    public async Task RunPreflightCheckAsync_WhenSelectedMonthChangesDuringCheck_ChecksMonthAtStart()
    {
        SelectOneCard();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;
        var (entered, release, calls) = SetupBlockingPreflightBuild();

        var checking = _viewModel.RunPreflightCheckAsync();
        await entered.Task;

        _viewModel.SelectedMonth = 6;
        release.SetResult(true);
        await checking;

        calls.Should().NotBeEmpty("事前チェックが帳票データを構築したこと（検査の空振り防止）");
        calls.Should().OnlyContain(c => c.Year == 2026 && c.Month == 5,
            "検査対象はボタンを押した時点の選択年月（2026年5月）であること");
        _viewModel.StatusMessage.Should().Contain("警告1件");
    }

    /// <summary>
    /// 対の表明: 検査の前に選択年月を変えた場合は、変更後の年月を検査すること（Issue #2045）
    /// </summary>
    /// <remarks>
    /// スナップショットを「常に初期値（先月）を使う」形へ退化させた実装を検出する。
    /// </remarks>
    [Fact]
    public async Task RunPreflightCheckAsync_WhenSelectedMonthChangedBeforeCheck_ChecksChangedMonth()
    {
        SelectOneCard();
        _viewModel.SelectedYear = 2027;
        _viewModel.SelectedMonth = 11;
        var (_, release, calls) = SetupBlockingPreflightBuild();
        release.SetResult(true);

        await _viewModel.RunPreflightCheckAsync();

        calls.Should().NotBeEmpty();
        calls.Should().OnlyContain(c => c.Year == 2027 && c.Month == 11);
    }

    #endregion

    #region 事前チェック警告マークの陳腐化（Issue #2059）

    /// <summary>
    /// 事前チェックが警告を出すカード 1 枚を一覧へ読み込み、選択状態にする
    /// </summary>
    /// <remarks>
    /// 警告マークは <c>Cards</c> の DTO に付くため、<c>SelectedCards</c> だけへ足す
    /// <see cref="SelectOneCard"/> では観測できない。
    /// </remarks>
    private async Task<CardDto> LoadOneWarningCardAsync(int year, int month)
    {
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>
        {
            new() { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        });
        await _viewModel.LoadCardsAsync();
        _viewModel.SelectedYear = year;
        _viewModel.SelectedMonth = month;
        return _viewModel.Cards.Single();
    }

    /// <summary>
    /// 欠陥を突く側: 事前チェックの後に月を変えると、警告マークが消えること
    /// </summary>
    [Fact]
    public async Task RunPreflightCheckAsync_ThenSelectedMonthChanges_ShouldClearWarningMarker()
    {
        var card = await LoadOneWarningCardAsync(2026, 5);
        SetupPreflightWarning();
        await _viewModel.RunPreflightCheckAsync();
        card.PreflightWarningCount.Should().Be(1, "前提: 5月の事前チェックでマークが付いていること");

        _viewModel.SelectedMonth = 6;

        card.PreflightWarningCount.Should().Be(0);
        card.HasPreflightWarning.Should().BeFalse();
    }

    /// <summary>
    /// 欠陥を突く側: 年だけを変えても、警告マークが消えること
    /// </summary>
    [Fact]
    public async Task RunPreflightCheckAsync_ThenSelectedYearChanges_ShouldClearWarningMarker()
    {
        var card = await LoadOneWarningCardAsync(2026, 5);
        SetupPreflightWarning();
        await _viewModel.RunPreflightCheckAsync();
        card.PreflightWarningCount.Should().Be(1, "前提: 2026年5月の事前チェックでマークが付いていること");

        _viewModel.SelectedYear = 2025;

        card.PreflightWarningCount.Should().Be(0);
    }

    /// <summary>
    /// 対の表明: 年月を変えなければ、警告マークは残ること
    /// </summary>
    /// <remarks>
    /// 「事前チェックの直後に消す」実装を検出する。
    /// </remarks>
    [Fact]
    public async Task RunPreflightCheckAsync_WithoutPeriodChange_ShouldKeepWarningMarker()
    {
        var card = await LoadOneWarningCardAsync(2026, 5);
        SetupPreflightWarning();

        await _viewModel.RunPreflightCheckAsync();

        card.PreflightWarningCount.Should().Be(1);
        card.HasPreflightWarning.Should().BeTrue();
    }

    /// <summary>
    /// 対の表明: 同じ年月を再選択しても、警告マークは消えないこと
    /// </summary>
    /// <remarks>
    /// 「先月」ボタンを先月の表示中にもう一度押す（年月が変わらない）操作で消す実装を検出する。
    /// 生成された setter は値が変わったときしか変更通知を呼ばないため、消去を「先月」「今月」の
    /// コマンド本体へ置いた形がこのテストの検出対象になる。
    /// 年月の基準は <see cref="ReportViewModel.SelectLastMonth"/> 自身に決めさせ、テスト側で
    /// <c>DateTime.Now</c> を読まない（月末の境界で基準が食い違わないようにする）。
    /// </remarks>
    [Fact]
    public async Task RunPreflightCheckAsync_ThenSamePeriodReselected_ShouldKeepWarningMarker()
    {
        var card = await LoadOneWarningCardAsync(2026, 5);
        _viewModel.SelectLastMonth();
        SetupPreflightWarning();
        await _viewModel.RunPreflightCheckAsync();
        card.PreflightWarningCount.Should().Be(1, "前提: 事前チェックでマークが付いていること");

        _viewModel.SelectLastMonth();

        card.PreflightWarningCount.Should().Be(1);
    }

    /// <summary>
    /// 欠陥を突く側: 検査の待機中に月を変えたら、検査していない月の画面へ古い結果を書き戻さないこと
    /// </summary>
    /// <remarks>
    /// 年月の変更でマークを消しても、待機中の検査が後から書き戻すと同じ食い違いが残る。
    /// </remarks>
    [Fact]
    public async Task RunPreflightCheckAsync_WhenSelectedMonthChangesDuringCheck_ShouldNotApplyWarningMarker()
    {
        var card = await LoadOneWarningCardAsync(2026, 5);
        var (entered, release, calls) = SetupBlockingPreflightBuild();

        var checking = _viewModel.RunPreflightCheckAsync();
        await entered.Task;
        _viewModel.SelectedMonth = 6;
        release.SetResult(true);
        await checking;

        calls.Should().NotBeEmpty("事前チェックが帳票データを構築したこと（検査の空振り防止）");
        _viewModel.StatusMessage.Should().Contain("警告1件", "検査自体は 5 月の警告を検出していること");
        card.PreflightWarningCount.Should().Be(0, "6 月を表示中の一覧へ 5 月の結果を付けないこと");
    }

    /// <summary>
    /// 対の表明: 検査の待機中に年月を変えなければ、待機の後でも結果が反映されること
    /// </summary>
    /// <remarks>
    /// 「待機を挟んだら書き戻さない」形へ退化した実装を検出する。
    /// </remarks>
    [Fact]
    public async Task RunPreflightCheckAsync_WhenCheckWaitsWithoutPeriodChange_ShouldApplyWarningMarker()
    {
        var card = await LoadOneWarningCardAsync(2026, 5);
        var (entered, release, _) = SetupBlockingPreflightBuild();

        var checking = _viewModel.RunPreflightCheckAsync();
        await entered.Task;
        release.SetResult(true);
        await checking;

        card.PreflightWarningCount.Should().Be(1);
    }

    /// <summary>
    /// 検査した年月が画面の年月と異なる結果は、一覧へ反映しないこと
    /// </summary>
    [Fact]
    public async Task ApplyPreflightWarnings_WithDifferentPeriod_ShouldNotApply()
    {
        var card = await LoadOneWarningCardAsync(2026, 6);
        var result = new ReportPreflightResult();
        result.Warnings.Add(new ReportPreflightWarning { CardIdm = card.CardIdm });

        _viewModel.ApplyPreflightWarnings(result, 2026, 5);
        card.PreflightWarningCount.Should().Be(0, "月が異なる");

        _viewModel.ApplyPreflightWarnings(result, 2025, 6);
        card.PreflightWarningCount.Should().Be(0, "年が異なる");

        _viewModel.ApplyPreflightWarnings(result, 2026, 6);
        card.PreflightWarningCount.Should().Be(1, "対の表明: 年月が一致すれば反映する");
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

    #region 出力状況の更新の陳腐化（Issue #2058）

    /// <summary>
    /// 月ごとに <c>GetStatuses</c> を止めるゲート。判定の待機中という割り込み点を、
    /// スレッドを競争させずに確定的に再現する。
    /// </summary>
    private sealed class ExportStatusGate
    {
        private readonly Dictionary<int, (ManualResetEventSlim Entered, ManualResetEventSlim Release)> _gates = new();

        public void Block(int month) =>
            _gates[month] = (new ManualResetEventSlim(false), new ManualResetEventSlim(false));

        public void WaitUntilEntered(int month) =>
            _gates[month].Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue($"{month}月の判定が開始されること");

        public void Release(int month) => _gates[month].Release.Set();

        public void PassThrough(int month)
        {
            if (_gates.TryGetValue(month, out var gate))
            {
                gate.Entered.Set();
                gate.Release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue($"{month}月の判定の待機が解除されること");
            }
        }
    }

    /// <summary>
    /// 月ごとに異なる判定結果を返し、ゲートで止められるようにする。
    /// 5月は全カード「出力済み」、6月は全カード「未出力」。
    /// </summary>
    private ExportStatusGate SetupMonthDependentExportStatuses(Func<int, Exception> failure = null)
    {
        var gate = new ExportStatusGate();
        _exportStatusServiceMock
            .Setup(s => s.GetStatuses(
                It.IsAny<IEnumerable<ReportExportTarget>>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns((IEnumerable<ReportExportTarget> targets, string folder, int year, int month) =>
            {
                gate.PassThrough(month);
                var ex = failure?.Invoke(month);
                if (ex != null)
                {
                    throw ex;
                }

                var state = month == 5 ? ReportExportState.Exported : ReportExportState.NotExported;
                return targets
                    .Select(t => new ReportExportStatus { CardIdm = t.CardIdm, State = state })
                    .ToList();
            });
        return gate;
    }

    /// <summary>
    /// 判定の待機中に年月を変えても、集計文言は判定に使った年月の名前になること（欠陥 1）
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WhenMonthChangesWhileChecking_ShouldNameSummaryWithCheckedMonth()
    {
        // Arrange
        await LoadThreeCardsAsync();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;
        var gate = SetupMonthDependentExportStatuses();
        gate.Block(5);

        // Act: 5月の判定を待機させている間に 6月へ変える
        var refreshMay = _viewModel.RefreshExportStatusAsync();
        gate.WaitUntilEntered(5);
        _viewModel.SelectedMonth = 6;
        gate.Release(5);
        await refreshMay;

        // Assert: 5月の結果は 5月の名前で表示される
        _viewModel.ExportStatusSummary.Should().Be("2026年5月: 出力済み 3件 / 未出力 0件");
        _viewModel.ExportStatusSummary.Should().NotContain("6月");
    }

    /// <summary>
    /// 先に始まった更新が後から終わっても、後から始まった更新の結果が残ること（欠陥 2）
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WhenOlderRefreshFinishesLast_ShouldKeepNewerResult()
    {
        // Arrange
        await LoadThreeCardsAsync();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;
        var gate = SetupMonthDependentExportStatuses();
        gate.Block(5);

        // Act: 5月の更新を止めたまま 6月の更新を完了させ、その後に 5月の更新を終わらせる
        var refreshMay = _viewModel.RefreshExportStatusAsync();
        gate.WaitUntilEntered(5);
        _viewModel.SelectedMonth = 6;
        await _viewModel.RefreshExportStatusAsync();
        gate.Release(5);
        await refreshMay;

        // Assert: 6月の結果（未出力）が残り、5月の「出力済み」バッジで上書きされない
        _viewModel.Cards.Should().OnlyContain(c => c.ExportState == ReportExportState.NotExported);
        _viewModel.ExportStatusSummary.Should().Be("2026年6月: 出力済み 0件 / 未出力 3件");
    }

    /// <summary>
    /// 古い更新の失敗で、新しい更新の成功結果を消さないこと
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WhenOlderRefreshFailsLast_ShouldKeepNewerResult()
    {
        // Arrange
        await LoadThreeCardsAsync();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;
        var gate = SetupMonthDependentExportStatuses(
            month => month == 5 ? new IOException("network path was not found") : null);
        gate.Block(5);

        // Act
        var refreshMay = _viewModel.RefreshExportStatusAsync();
        gate.WaitUntilEntered(5);
        _viewModel.SelectedMonth = 6;
        await _viewModel.RefreshExportStatusAsync();
        gate.Release(5);
        await refreshMay;

        // Assert
        _viewModel.Cards.Should().OnlyContain(c => c.ExportState == ReportExportState.NotExported);
        _viewModel.ExportStatusSummary.Should().Be("2026年6月: 出力済み 0件 / 未出力 3件");
    }

    /// <summary>
    /// 対の表明: 待機を挟んでも、更新が 1 本だけなら結果が反映されること
    /// （世代判定が結果を常に捨てる実装を検出する）
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WhenSingleRefreshWaits_ShouldApplyResult()
    {
        // Arrange
        await LoadThreeCardsAsync();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;
        var gate = SetupMonthDependentExportStatuses();
        gate.Block(5);

        // Act
        var refreshMay = _viewModel.RefreshExportStatusAsync();
        gate.WaitUntilEntered(5);
        gate.Release(5);
        await refreshMay;

        // Assert
        _viewModel.Cards.Should().OnlyContain(c => c.ExportState == ReportExportState.Exported);
        _viewModel.ExportStatusSummary.Should().Be("2026年5月: 出力済み 3件 / 未出力 0件");
    }

    /// <summary>
    /// 対の表明: 1 本だけの更新が失敗したら、バッジを消して失敗を案内すること（#1996 を維持）
    /// </summary>
    [Fact]
    public async Task RefreshExportStatusAsync_WhenSingleRefreshFails_ShouldClearBadgesAndReportFailure()
    {
        // Arrange
        await LoadThreeCardsAsync();
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;
        SetupMonthDependentExportStatuses();
        await _viewModel.RefreshExportStatusAsync();
        SetupMonthDependentExportStatuses(_ => new IOException("network path was not found"));

        // Act
        await _viewModel.RefreshExportStatusAsync();

        // Assert
        _viewModel.Cards.Should().OnlyContain(c => c.ExportState == ReportExportState.Unknown);
        _viewModel.ExportStatusSummary.Should().Contain("出力状況を確認できませんでした");
    }

    #endregion

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
        _viewModel.ApplyPreflightWarnings(result, _viewModel.SelectedYear, _viewModel.SelectedMonth);

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
        _viewModel.ApplyPreflightWarnings(first, _viewModel.SelectedYear, _viewModel.SelectedMonth);

        // Act: 2回目は警告なし
        _viewModel.ApplyPreflightWarnings(new ReportPreflightResult(), _viewModel.SelectedYear, _viewModel.SelectedMonth);

        // Assert
        _viewModel.Cards.Should().OnlyContain(c => c.PreflightWarningCount == 0);
    }

    #endregion

    #region ヘルパーメソッド（Issue #1821）

    /// <summary>
    /// fire-and-forget の完了待ちに使うタイムアウト。
    /// 遅い CI ランナーでも余裕を持って完了できる長さを取る（正常時はシグナルで即座に抜ける）。
    /// </summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 指定した出力先フォルダーで <see cref="ISettingsRepository.SaveAppSettingsAsync"/> が
    /// 呼ばれたことを知らせるシグナルを仕込む。
    /// </summary>
    /// <remarks>
    /// Issue #1821: <c>OnOutputFolderChanged</c> は <c>SaveOutputFolderAsync</c> を
    /// fire-and-forget で起動するため、呼び出し側は完了を await できない。
    /// 固定時間の <c>Task.Delay</c> で待つと遅いマシンで散発的に赤くなり、
    /// かつ常にその時間だけ待つので実行時間も無駄になる（.claude/rules/testing.md）。
    /// </remarks>
    private TaskCompletionSource<bool> CreateSaveSignal(string expectedFolder)
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsRepositoryMock
            .Setup(s => s.SaveAppSettingsAsync(It.Is<AppSettings>(a => a.ReportOutputFolder == expectedFolder)))
            .Callback(() => signal.TrySetResult(true))
            .ReturnsAsync(true);
        return signal;
    }

    /// <summary>
    /// シグナルが立つまで待つ。タイムアウトした場合は false を返す。
    /// </summary>
    private static async Task<bool> WaitForSignalAsync(TaskCompletionSource<bool> signal)
    {
        using var timeoutCts = new CancellationTokenSource();
        var timeout = Task.Delay(SignalTimeout, timeoutCts.Token);
        var completed = await Task.WhenAny(signal.Task, timeout);
        // シグナルが先に立ったらタイマーを畳む（5 秒ぶんのタイマーを残さない）
        timeoutCts.Cancel();
        return completed == signal.Task && await signal.Task;
    }

    #endregion
}
