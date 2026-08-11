using System.Collections.Generic;
using System.Data.SQLite;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Timing;
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
    private readonly Mock<ICCardManager.Services.ISafeFileLauncher> _safeFileLauncherMock;
    private readonly SQLiteConnection _connection;
    private readonly DbContext _realDbContext;
    private readonly OperationLogRepository _operationLogRepository;
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
        // セマフォを保持しないConnectionLease/TransactionScopeを使用
        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        _realDbContext = new DbContext(":memory:");
        _realDbContext.InitializeDatabase();
        var noOpLease = new ConnectionLease(_connection, () => { });
        var noOpTransaction = _connection.BeginTransaction();
        var transactionScope = new ICCardManager.Data.TransactionScope(noOpLease, noOpTransaction);
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(transactionScope);

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

        // OperationLogger (Issue #1302): 実DB + 実Contextを使う。
        // Issue #1741: 書き込まれた operation_log 行そのものを検証対象にするため、
        // リポジトリをフィールドで保持して読み返せるようにする。
        _operationLogRepository = new OperationLogRepository(_realDbContext);
        var operatorContext = new CurrentOperatorContext(new SystemClock());
        var operationLogger = new OperationLogger(_operationLogRepository, operatorContext);

        _safeFileLauncherMock = new Mock<ICCardManager.Services.ISafeFileLauncher>();
        _safeFileLauncherMock.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Ok());
        _safeFileLauncherMock.Setup(l => l.LaunchFolder(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Ok());

        _viewModel = new DataExportImportViewModel(
            _exportServiceMock.Object,
            _importServiceMock.Object,
            _dialogServiceMock.Object,
            _cardRepositoryMock.Object,
            operationLogger,
            new WeakReferenceMessenger(),
            _safeFileLauncherMock.Object);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _realDbContext?.Dispose();
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
        // Issue #1614: 生の ex.Message（"DB接続エラー"）を UI に漏らさず、
        // 「何が／なぜ／どうすれば」を満たす文言を表示する。技術的詳細はログのみ。
        _dialogServiceMock.Verify(
            d => d.ShowError(
                It.Is<string>(msg => !msg.Contains("DB接続エラー") && msg.Contains("インポートに失敗")),
                It.Is<string>(title => title.Contains("エラー"))),
            Times.Once);
        _viewModel.StatusMessage.Should().NotContain("DB接続エラー", "技術的詳細はログのみに記録する");
        _viewModel.StatusMessage.Should().Contain("インポートに失敗");
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

    #region Issue #905: プレビュー列ヘッダーの動的切り替え

    [Theory]
    [InlineData(DataType.Cards, "IDm", "カード種別", "管理番号")]
    [InlineData(DataType.Staff, "IDm", "氏名", "職員番号")]
    [InlineData(DataType.Ledgers, "カード", "摘要", "日付")]
    [InlineData(DataType.LedgerDetails, "利用履歴ID", "カード", "詳細件数")]
    public void PreviewColumnHeaders_データ種別に応じて正しいヘッダーが返される(
        DataType dataType, string expectedCol1, string expectedCol2, string expectedCol3)
    {
        // Act
        _viewModel.SelectedImportType = dataType;

        // Assert
        _viewModel.PreviewColumn1Header.Should().Be(expectedCol1);
        _viewModel.PreviewColumn2Header.Should().Be(expectedCol2);
        _viewModel.PreviewColumn3Header.Should().Be(expectedCol3);
    }

    [Fact]
    public void PreviewColumnHeaders_種別変更時にPropertyChangedが発火する()
    {
        // Arrange
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Act
        _viewModel.SelectedImportType = DataType.LedgerDetails;

        // Assert
        changedProperties.Should().Contain(nameof(DataExportImportViewModel.PreviewColumn1Header));
        changedProperties.Should().Contain(nameof(DataExportImportViewModel.PreviewColumn2Header));
        changedProperties.Should().Contain(nameof(DataExportImportViewModel.PreviewColumn3Header));
    }

    #endregion

    /// <summary>
    /// テスト用にプレビュー状態をセットアップするヘルパー
    /// </summary>
    private void SetupValidPreview(DataType dataType = DataType.Cards)
        => SetupValidPreview(_viewModel, dataType);

    /// <summary>
    /// 指定した ViewModel にプレビュー状態をセットアップするヘルパー（Issue #1741 で追加）
    /// </summary>
    private static void SetupValidPreview(DataExportImportViewModel viewModel, DataType dataType = DataType.Cards)
    {
        viewModel.SelectedImportType = dataType;
        viewModel.ImportPreviewFile = "test.csv";
        viewModel.ImportPreview = new CsvImportPreviewResult { IsValid = true };
        viewModel.HasPreview = true;
    }

    #region Issue #1383: エクスポート完了時にプログレスバー(IsBusy)がダイアログ表示前に閉じること

    /// <summary>
    /// 成功時、ShowInformationが呼ばれる時点でIsBusy=falseになっていること。
    /// BeginBusyスコープ内でMessageBoxを表示するとモーダル中プログレスバーが残るため、
    /// スコープを抜けてから表示する修正が効いていることを確認する。
    /// </summary>
    [Fact]
    public async Task ExportToFileAsync_成功時_ShowInformation呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"export_{System.Guid.NewGuid()}.csv");
        _viewModel.SelectedExportType = DataType.Cards;

        _exportServiceMock
            .Setup(x => x.ExportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvExportResult
            {
                Success = true,
                FilePath = tempPath,
                ExportedCount = 3,
            });

        bool? isBusyAtShowInformation = null;
        _dialogServiceMock
            .Setup(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtShowInformation = _viewModel.IsBusy);

        // Act
        await _viewModel.ExportToFileAsync(tempPath);

        // Assert
        isBusyAtShowInformation.Should().NotBeNull("成功メッセージダイアログが表示されているはず");
        isBusyAtShowInformation.Should().BeFalse("Issue #1383: ダイアログ表示時にはプログレスバーが閉じていること");
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 失敗時、ShowErrorが呼ばれる時点でIsBusy=falseになっていること。
    /// </summary>
    [Fact]
    public async Task ExportToFileAsync_失敗時_ShowError呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"export_{System.Guid.NewGuid()}.csv");
        _viewModel.SelectedExportType = DataType.Cards;

        _exportServiceMock
            .Setup(x => x.ExportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvExportResult
            {
                Success = false,
                ErrorMessage = "書き込み権限がありません",
            });

        bool? isBusyAtShowError = null;
        _dialogServiceMock
            .Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtShowError = _viewModel.IsBusy);

        // Act
        await _viewModel.ExportToFileAsync(tempPath);

        // Assert
        isBusyAtShowError.Should().NotBeNull("エラーダイアログが表示されているはず");
        isBusyAtShowError.Should().BeFalse("Issue #1383: エラーダイアログ表示時にもプログレスバーが閉じていること");
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 例外発生時、BeginBusyスコープが確実にDisposeされIsBusy=falseになること。
    /// 例外パスではダイアログ表示はしないが、プログレスバーは必ず閉じる。
    /// </summary>
    [Fact]
    public async Task ExportToFileAsync_例外発生時_IsBusyがfalseに戻ること()
    {
        // Arrange
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"export_{System.Guid.NewGuid()}.csv");
        _viewModel.SelectedExportType = DataType.Cards;

        _exportServiceMock
            .Setup(x => x.ExportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(new System.InvalidOperationException("意図的な例外"));

        // Act
        await _viewModel.ExportToFileAsync(tempPath);

        // Assert
        _viewModel.IsBusy.Should().BeFalse("例外発生時もusingブロックのDisposeでIsBusyはfalseに戻る");
        _dialogServiceMock.Verify(
            d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "例外時は成功ダイアログを表示しない");
    }

    #endregion

    #region OpenExportedFile / OpenExportFolder（Issue #1465）

    [Fact]
    public void OpenExportedFile_ISafeFileLauncherへ委譲()
    {
        _viewModel.LastExportedFile = "C:\\export.csv";

        _viewModel.OpenExportedFileCommand.Execute(null);

        _safeFileLauncherMock.Verify(l => l.LaunchFile("C:\\export.csv"), Times.Once);
    }

    [Fact]
    public void OpenExportedFile_失敗時_ステータスにエラー()
    {
        _viewModel.LastExportedFile = "C:\\evil.exe";
        _safeFileLauncherMock.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Fail("拡張子NG"));

        _viewModel.OpenExportedFileCommand.Execute(null);

        _viewModel.StatusMessage.Should().Contain("拡張子NG");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    [Fact]
    public void OpenExportFolder_親フォルダ算出後にISafeFileLauncherへ委譲()
    {
        _viewModel.LastExportedFile = "C:\\exports\\file.csv";

        _viewModel.OpenExportFolderCommand.Execute(null);

        _safeFileLauncherMock.Verify(l => l.LaunchFolder("C:\\exports"), Times.Once);
    }

    [Fact]
    public void OpenExportFolder_LastExportedFile未設定_空文字でlauncher呼び出し()
    {
        // LastExportedFile が空のとき Path.GetDirectoryName は null を返すため、
        // ViewModel 側で空文字へフォールバックして launcher に委ねる（launcher 側で Validator が空エラーを返す）。
        _viewModel.LastExportedFile = string.Empty;
        _safeFileLauncherMock.Setup(l => l.LaunchFolder(string.Empty))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Fail("フォルダパスが空です"));

        _viewModel.OpenExportFolderCommand.Execute(null);

        _safeFileLauncherMock.Verify(l => l.LaunchFolder(string.Empty), Times.Once);
        _viewModel.IsStatusError.Should().BeTrue();
    }

    #endregion

    #region 監査ログへのインポート元ファイルパス記録（Issue #1741）

    private const string ImportSourceDirectory = @"C:\temp";
    private const string ImportSourceFileName = "cards_20260811.csv";
    private static readonly string ImportSourceFilePath =
        System.IO.Path.Combine(ImportSourceDirectory, ImportSourceFileName);

    /// <summary>
    /// インポート成功時、監査ログにインポート元ファイルのパスとファイル名が記録されること（Issue #1741）
    /// </summary>
    /// <remarks>
    /// 成功分岐は ClearPreview() で ImportPreviewFile を空にするため、
    /// 監査ログの引数をビュー状態プロパティから読み直すと空文字が記録される。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_OnSuccess_ShouldRecordImportSourceFilePathInOperationLog()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
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
        var log = await GetSingleImportLogAsync();
        log.TargetTable.Should().Be(OperationLogger.Tables.IcCard);
        log.TargetId.Should().Be(
            ImportSourceFileName,
            "操作ログ画面の対象IDにインポート元ファイル名が表示される必要があるため");
        GetAfterDataString(log, "FilePath").Should().Be(
            ImportSourceFilePath,
            "どのファイルを取り込んだかを後から追跡できる必要があるため");
        GetAfterDataString(log, "FileName").Should().Be(ImportSourceFileName);
    }

    /// <summary>
    /// 部分成功時も監査ログにインポート元ファイルのパスが記録されること（Issue #1741）
    /// </summary>
    /// <remarks>
    /// 修正前から正しく記録されていた分岐。成功分岐との非対称が再発しないよう固定する。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_OnPartialSuccess_ShouldRecordImportSourceFilePathInOperationLog()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
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
                    new CsvImportError { LineNumber = 3, Message = "IDmが不正です" }
                }
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        var log = await GetSingleImportLogAsync();
        log.TargetId.Should().Be(ImportSourceFileName);
        GetAfterDataString(log, "FilePath").Should().Be(ImportSourceFilePath);
    }

    /// <summary>
    /// 職員データのインポート成功時も、対象テーブルとファイルパスが正しく記録されること（Issue #1741）
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_StaffImport_OnSuccess_ShouldRecordImportSourceFilePathInOperationLog()
    {
        // Arrange
        SetupValidPreview(DataType.Staff);
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
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
        var log = await GetSingleImportLogAsync();
        log.TargetTable.Should().Be(OperationLogger.Tables.Staff);
        log.TargetId.Should().Be(ImportSourceFileName);
        GetAfterDataString(log, "FilePath").Should().Be(ImportSourceFilePath);
    }

    /// <summary>
    /// Issue #1741: await 中にデータ種別が変わっても、監査ログの対象テーブルは実際の取込先になること
    /// </summary>
    /// <remarks>
    /// データ種別コンボはアクセスキー（Alt+I）を持ち IsBusy でも操作できるため、
    /// SelectedImportType を await 後に読み直すと実際とは異なるテーブル名が記録され得る。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_await中にデータ種別が変わっても対象テーブルは実際の取込先になること()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            // インポート実行中に画面のデータ種別が切り替わる状況を再現する
            .Callback(() => _viewModel.SelectedImportType = DataType.Staff)
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 3
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.SelectedImportType.Should().Be(DataType.Staff, "前提: 実行中に種別が切り替わっていること");
        var log = await GetSingleImportLogAsync();
        log.TargetTable.Should().Be(
            OperationLogger.Tables.IcCard,
            "実際に書き込まれたのはカードテーブルであるため");
        GetAfterDataString(log, "FilePath").Should().Be(ImportSourceFilePath);
    }

    /// <summary>
    /// Issue #1741: 監査ログの記録に失敗しても、確定済みの取り込みを「失敗」として通知しないこと
    /// </summary>
    /// <remarks>
    /// 監査ログ記録はコミット確定後の後処理。ここでの例外を取り込みの catch へ流すと
    /// 「インポートに失敗しました」と通知され、職員が再実行して二重登録を招く
    /// （CLAUDE.md / Issue #1727「コミット確定後の後処理を、成否の判定に巻き込まない」）。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_監査ログ記録の失敗を取り込み失敗として通知しないこと()
    {
        // Arrange: operation_log への INSERT だけが失敗する ViewModel
        var dialogServiceMock = new Mock<IDialogService>();
        var viewModel = CreateViewModelWithFailingAuditLog(dialogServiceMock);
        SetupValidPreview(viewModel);
        viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 3
            });

        // Act
        await viewModel.ExecuteImportAsync();

        // Assert: 取り込みは成功として扱われること
        viewModel.IsStatusError.Should().BeFalse();
        viewModel.StatusMessage.Should().Contain("3件を登録しました");
        viewModel.HasImported.Should().BeTrue();
        dialogServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "取り込みは確定しているためエラーとして通知してはならない");

        // Assert: 記録失敗は「再実行するな」と併せて伝えること
        dialogServiceMock.Verify(
            d => d.ShowWarning(
                It.Is<string>(m => m.Contains("3件")
                                   && m.Contains("操作ログへの記録に失敗")
                                   && m.Contains("再度インポートしないでください")),
                It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Issue #1741: 部分成功でも監査ログの記録失敗を取り込み失敗として通知しないこと
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_部分成功時も監査ログ記録の失敗を取り込み失敗として通知しないこと()
    {
        // Arrange
        var dialogServiceMock = new Mock<IDialogService>();
        var viewModel = CreateViewModelWithFailingAuditLog(dialogServiceMock);
        SetupValidPreview(viewModel);
        viewModel.ImportPreviewFile = ImportSourceFilePath;
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
                    new CsvImportError { LineNumber = 3, Message = "IDmが不正です" }
                }
            });

        // Act
        await viewModel.ExecuteImportAsync();

        // Assert
        viewModel.IsStatusError.Should().BeFalse();
        viewModel.HasImported.Should().BeTrue();
        dialogServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        dialogServiceMock.Verify(
            d => d.ShowWarning(
                It.Is<string>(m => m.Contains("一部エラー") && m.Contains("操作ログへの記録に失敗")),
                It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// operation_log への INSERT だけが失敗する ViewModel を組み立てる（Issue #1741）
    /// </summary>
    /// <remarks>
    /// OperationLogger は具象クラスで LogImportAsync も非 virtual だが、例外を内部で握りつぶさないため、
    /// リポジトリ境界（IOperationLogRepository）で失敗を注入すれば本番と同じ経路で例外が伝播する。
    /// </remarks>
    private DataExportImportViewModel CreateViewModelWithFailingAuditLog(Mock<IDialogService> dialogServiceMock)
    {
        var failingRepository = new Mock<IOperationLogRepository>();
        failingRepository
            .Setup(r => r.InsertAsync(It.IsAny<ICCardManager.Models.OperationLog>()))
            .ThrowsAsync(new InvalidOperationException("operation_log への書き込みに失敗しました"));

        var operationLogger = new OperationLogger(
            failingRepository.Object,
            new CurrentOperatorContext(new SystemClock()));

        return new DataExportImportViewModel(
            _exportServiceMock.Object,
            _importServiceMock.Object,
            dialogServiceMock.Object,
            _cardRepositoryMock.Object,
            operationLogger,
            new WeakReferenceMessenger(),
            _safeFileLauncherMock.Object);
    }

    /// <summary>
    /// operation_log に記録された唯一の IMPORT 行を取得する
    /// </summary>
    private async Task<ICCardManager.Models.OperationLog> GetSingleImportLogAsync()
    {
        var logs = await _operationLogRepository.GetByDateRangeAsync(
            DateTime.Today.AddDays(-1),
            DateTime.Today.AddDays(1));

        return logs.Should()
            .ContainSingle(l => l.Action == OperationLogger.Actions.Import)
            .Which;
    }

    /// <summary>
    /// operation_log の after_data(JSON) から文字列プロパティを取り出す
    /// </summary>
    private static string GetAfterDataString(ICCardManager.Models.OperationLog log, string propertyName)
    {
        log.AfterData.Should().NotBeNullOrEmpty("after_data に payload が記録されていること");

        using var document = System.Text.Json.JsonDocument.Parse(log.AfterData);
        return document.RootElement.GetProperty(propertyName).GetString();
    }

    #endregion

    #region 直接インポート経路（旧API互換）の結果処理（Issue #1785）

    // ImportAsync（「直接インポート(_R)」ボタン）は OpenFileDialog をコマンド内で生成するため
    // 単体テストから起動できない。Issue #1785 で結果処理を RunImportAsync へ共通化したことにより、
    // この経路の設定（clearPreviewOnSuccess: false）を直接検証できるようになった。
    // 共通化前は ExecuteImportAsync 側にしかテストが無く、複製側は無検証だった。

    private const string DirectImportFileName = "staff_20260811.csv";
    private static readonly string DirectImportFilePath =
        System.IO.Path.Combine(ImportSourceDirectory, DirectImportFileName);

    /// <summary>
    /// 直接インポートは成功してもプレビュー表示をクリアしないこと（Issue #1785 で挙動を保存）
    /// </summary>
    /// <remarks>
    /// プレビューは ExecuteImportAsync 経路の入力であり、直接インポートとは無関係。
    /// 共通化にあたって一律クリアにすると、プレビュー確認中の職員の作業状態を勝手に捨てることになる。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_直接インポート_成功してもプレビューをクリアしないこと()
    {
        // Arrange: プレビューを表示したまま直接インポートを実行する状況
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 3
            });

        // Act
        await _viewModel.RunImportAsync(DirectImportFilePath, clearPreviewOnSuccess: false);

        // Assert
        _viewModel.HasPreview.Should().BeTrue("直接インポートはプレビューを入力としないため消してはならない");
        _viewModel.ImportPreviewFile.Should().Be(ImportSourceFilePath);
        _viewModel.LastImportedFile.Should().Be(
            DirectImportFilePath,
            "取り込んだのはダイアログで選んだファイルであるため");
        _viewModel.HasImported.Should().BeTrue();
    }

    /// <summary>
    /// 直接インポートでも、渡されたパスでインポートサービスが呼ばれ監査ログへ記録されること（Issue #1785）
    /// </summary>
    [Fact]
    public async Task RunImportAsync_直接インポート_成功時に監査ログへファイルパスが記録されること()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 4
            });

        // Act
        await _viewModel.RunImportAsync(DirectImportFilePath, clearPreviewOnSuccess: false);

        // Assert
        _importServiceMock.Verify(
            s => s.ImportCardsAsync(DirectImportFilePath, It.IsAny<bool>()),
            Times.Once,
            "プレビューのパスではなく引数で渡したパスを取り込むこと");

        var log = await GetSingleImportLogAsync();
        log.TargetTable.Should().Be(OperationLogger.Tables.IcCard);
        log.TargetId.Should().Be(DirectImportFileName);
        GetAfterDataString(log, "FilePath").Should().Be(
            DirectImportFilePath,
            "どのファイルを取り込んだかを後から追跡できる必要があるため");
        _dialogServiceMock.Verify(
            d => d.ShowInformation(It.Is<string>(m => m.Contains("4件")), "インポート完了"),
            Times.Once);
    }

    /// <summary>
    /// 直接インポートの部分成功時、エラー一覧の表示と監査ログ記録の両方が行われること（Issue #1785）
    /// </summary>
    [Fact]
    public async Task RunImportAsync_直接インポート_部分成功時もエラー一覧と監査ログが揃うこと()
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
                    new CsvImportError { LineNumber = 3, Message = "IDmが不正です" }
                }
            });

        // Act
        await _viewModel.RunImportAsync(DirectImportFilePath, clearPreviewOnSuccess: false);

        // Assert
        _viewModel.ImportErrors.Should().ContainSingle()
            .Which.Should().Be("行3: IDmが不正です");
        _viewModel.HasImported.Should().BeTrue("2件は書き込まれているため");
        _dialogServiceMock.Verify(
            d => d.ShowWarning(It.Is<string>(m => m.Contains("一部エラー")), It.IsAny<string>()),
            Times.Once);

        var log = await GetSingleImportLogAsync();
        GetAfterDataString(log, "FilePath").Should().Be(DirectImportFilePath);
    }

    /// <summary>
    /// 直接インポートでもインポート自体の失敗はエラーダイアログで通知し、監査ログは記録しないこと（Issue #1785）
    /// </summary>
    [Fact]
    public async Task RunImportAsync_直接インポート_失敗時はエラー通知のみで監査ログを残さないこと()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = "ヘッダー行が想定と異なります",
                ImportedCount = 0
            });

        // Act
        await _viewModel.RunImportAsync(DirectImportFilePath, clearPreviewOnSuccess: false);

        // Assert
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.HasImported.Should().BeFalse("1件も書き込まれていないため");
        _dialogServiceMock.Verify(
            d => d.ShowError(It.Is<string>(m => m.Contains("ヘッダー行が想定と異なります")), "インポートエラー"),
            Times.Once);

        var logs = await _operationLogRepository.GetByDateRangeAsync(
            DateTime.Today.AddDays(-1),
            DateTime.Today.AddDays(1));
        logs.Should().NotContain(
            l => l.Action == OperationLogger.Actions.Import,
            "書き込みが発生していない以上インポートとして記録してはならない");
    }

    /// <summary>
    /// 直接インポートでも監査ログ記録の失敗を取り込み失敗として通知しないこと（Issue #1741 / #1785）
    /// </summary>
    /// <remarks>
    /// Issue #1741 の是正は ExecuteImportAsync 側でのみテストされていた。
    /// 共通化後は同じ経路を通ることを、複製側の設定（clearPreviewOnSuccess: false）でも表明する。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_直接インポート_監査ログ記録の失敗を取り込み失敗として通知しないこと()
    {
        // Arrange: operation_log への INSERT だけが失敗する ViewModel
        var dialogServiceMock = new Mock<IDialogService>();
        var viewModel = CreateViewModelWithFailingAuditLog(dialogServiceMock);
        SetupValidPreview(viewModel);
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = true,
                ImportedCount = 3
            });

        // Act
        await viewModel.RunImportAsync(DirectImportFilePath, clearPreviewOnSuccess: false);

        // Assert
        viewModel.IsStatusError.Should().BeFalse();
        viewModel.HasImported.Should().BeTrue();
        dialogServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "取り込みは確定しているためエラーとして通知してはならない");
        dialogServiceMock.Verify(
            d => d.ShowWarning(
                It.Is<string>(m => m.Contains("操作ログへの記録に失敗")
                                   && m.Contains("再度インポートしないでください")),
                It.IsAny<string>()),
            Times.Once);
    }

    #endregion
}
