using System.Collections.Generic;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// DataExportImportViewModelの単体テスト
/// </summary>
/// <remarks>
/// インポート実行後にダイアログで結果が通知されることを検証する（Issue #598）
/// </remarks>
public class DataExportImportViewModelTests : IDisposable
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<DbContext> _dbContextMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<CsvImportService> _importServiceMock;
    private readonly Mock<CsvExportService> _exportServiceMock;
    private readonly SQLiteConnection _connection;
    private readonly DataExportImportViewModel _viewModel;

    public DataExportImportViewModelTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _validationServiceMock = new Mock<IValidationService>();
        _dbContextMock = new Mock<DbContext>();
        _cacheServiceMock = new Mock<ICacheService>();
        _dialogServiceMock = new Mock<IDialogService>();

        // SQLiteインメモリ接続（DbContextモックのトランザクション用）
        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        var transaction = _connection.BeginTransaction();
        _dbContextMock.Setup(x => x.BeginTransaction()).Returns(transaction);

        // CsvExportService（コンストラクタで必要だが、テスト対象ではない）
        _exportServiceMock = new Mock<CsvExportService>(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object);

        // CsvImportService（virtualメソッドをモックする）
        _importServiceMock = new Mock<CsvImportService>(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _validationServiceMock.Object,
            _dbContextMock.Object,
            _cacheServiceMock.Object);

        _viewModel = new DataExportImportViewModel(
            _exportServiceMock.Object,
            _importServiceMock.Object,
            _dialogServiceMock.Object,
            _cardRepositoryMock.Object);
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    /// <summary>
    /// プレビュー未実行時、ExecuteImportAsyncはステータスメッセージを表示してダイアログは表示しないこと
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_WithoutPreview_ShouldSetStatusMessageAndNotShowDialog()
    {
        // Arrange - プレビュー未実行（ImportPreview = null）

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("プレビューを実行してください");
        _dialogServiceMock.Verify(
            d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _dialogServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// インポート成功時、完了ダイアログが表示されること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnSuccess_ShouldShowInformationDialog()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 3,
                SkippedCount = 0
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _dialogServiceMock.Verify(
            d => d.ShowInformation(
                It.Is<string>(msg => msg.Contains("3件")),
                It.Is<string>(title => title.Contains("インポート完了"))),
            Times.Once);
        _viewModel.StatusMessage.Should().Contain("3件を登録しました");
    }

    /// <summary>
    /// インポート成功時（スキップあり）、スキップ件数がダイアログに表示されること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnSuccessWithSkip_ShouldShowSkipCountInDialog()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 2,
                SkippedCount = 1
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _dialogServiceMock.Verify(
            d => d.ShowInformation(
                It.Is<string>(msg => msg.Contains("2件") && msg.Contains("スキップ") && msg.Contains("1件")),
                It.IsAny<string>()),
            Times.Once);
        _viewModel.StatusMessage.Should().Contain("1件はスキップ");
    }

    /// <summary>
    /// インポートエラー時、エラーダイアログが表示されること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnError_ShouldShowErrorDialog()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = "ファイル形式が不正です"
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _dialogServiceMock.Verify(
            d => d.ShowError(
                It.Is<string>(msg => msg.Contains("ファイル形式が不正です")),
                It.Is<string>(title => title.Contains("エラー"))),
            Times.Once);
        _viewModel.StatusMessage.Should().Contain("エラー");
    }

    /// <summary>
    /// インポート一部エラー時、警告ダイアログが表示されること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnPartialSuccess_ShouldShowWarningDialog()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = null,
                ImportedCount = 2,
                ErrorCount = 1,
                SkippedCount = 0,
                Errors = new List<CsvImportError>
                {
                    new CsvImportError { LineNumber = 3, Message = "IDmが不正です" }
                }
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _dialogServiceMock.Verify(
            d => d.ShowWarning(
                It.Is<string>(msg => msg.Contains("2件") && msg.Contains("1件")),
                It.Is<string>(title => title.Contains("一部エラー"))),
            Times.Once);
        _viewModel.ImportErrors.Should().ContainSingle(e => e.Contains("IDmが不正です"));
    }

    /// <summary>
    /// インポート中に例外が発生した場合、エラーダイアログが表示されること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnException_ShouldShowErrorDialog()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("DB接続エラー"));

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _dialogServiceMock.Verify(
            d => d.ShowError(
                It.Is<string>(msg => msg.Contains("DB接続エラー")),
                It.Is<string>(title => title.Contains("エラー"))),
            Times.Once);
        _viewModel.StatusMessage.Should().Contain("DB接続エラー");
    }

    /// <summary>
    /// インポート成功後、プレビューがクリアされること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnSuccess_ShouldClearPreview()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 1
            });

        _viewModel.HasPreview.Should().BeTrue("プレビューがセットアップされていること");

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasPreview.Should().BeFalse("成功後にプレビューがクリアされること");
        _viewModel.PreviewItems.Should().BeEmpty();
    }

    /// <summary>
    /// 職員データインポート成功時もダイアログが表示されること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_StaffImport_OnSuccess_ShouldShowInformationDialog()
    {
        // Arrange
        SetupValidPreview(DataType.Staff);
        _importServiceMock
            .Setup(s => s.ImportStaffAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 5
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _dialogServiceMock.Verify(
            d => d.ShowInformation(
                It.Is<string>(msg => msg.Contains("5件")),
                It.Is<string>(title => title.Contains("インポート完了"))),
            Times.Once);
    }

    #region HasImported フラグ（Issue #744）

    /// <summary>
    /// 初期状態でHasImportedがfalseであること
    /// </summary>
    [Fact]
    public void HasImported_Initially_ShouldBeFalse()
    {
        _viewModel.HasImported.Should().BeFalse();
    }

    /// <summary>
    /// インポート成功時にHasImportedがtrueになること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnSuccess_ShouldSetHasImportedTrue()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 3
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasImported.Should().BeTrue("インポートが成功し登録件数が1件以上");
    }

    /// <summary>
    /// インポート件数0の場合はHasImportedがfalseのままであること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_ZeroImported_ShouldKeepHasImportedFalse()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 0,
                SkippedCount = 3
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasImported.Should().BeFalse("登録件数が0件のため");
    }

    /// <summary>
    /// 一部エラーでも登録件数が1件以上ならHasImportedがtrueになること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_PartialSuccess_ShouldSetHasImportedTrue()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = null,
                ImportedCount = 2,
                ErrorCount = 1,
                Errors = new List<CsvImportError>
                {
                    new CsvImportError { LineNumber = 3, Message = "エラー" }
                }
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasImported.Should().BeTrue("一部エラーでも登録件数が1件以上");
    }

    /// <summary>
    /// インポートエラー（全件失敗）でHasImportedがfalseのままであること
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_OnError_ShouldKeepHasImportedFalse()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = "ファイル形式が不正です",
                ImportedCount = 0
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasImported.Should().BeFalse("全件失敗のため");
    }

    #endregion

    /// <summary>
    /// テスト用にプレビュー状態をセットアップするヘルパー
    /// </summary>
    private void SetupValidPreview(DataType dataType = DataType.Cards)
    {
        _viewModel.SelectedImportType = dataType;
        _viewModel.ImportPreviewFile = "test.csv";
        _viewModel.ImportPreview = new CsvImportPreviewResult { IsValid = true };
        _viewModel.HasPreview = true;
    }
}
