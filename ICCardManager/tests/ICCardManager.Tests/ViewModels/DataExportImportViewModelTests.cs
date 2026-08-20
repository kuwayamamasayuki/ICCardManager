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
    // この経路を直接検証できるようになった。共通化前は ExecuteImportAsync 側にしかテストが無く、
    // 複製側は無検証だった。
    // Issue #1782 で clearPreviewAfterImport 引数を撤廃し、経路によらず取り込み確定時は
    // プレビューを畳む挙動へ統一した。

    private const string DirectImportFileName = "staff_20260811.csv";
    private static readonly string DirectImportFilePath =
        System.IO.Path.Combine(ImportSourceDirectory, DirectImportFileName);

    /// <summary>
    /// 直接インポートは成功したら別ファイルのプレビュー表示もクリアすること（Issue #1782）
    /// </summary>
    /// <remarks>
    /// Issue #1785 では「プレビューは ExecuteImportAsync 経路の入力であり直接インポートとは無関係」
    /// として残す挙動を保存したが、無関係なのは<b>入力元</b>だけだった。プレビューの登録・スキップ件数は
    /// 取り込み前の DB を基準に算出した値で、別ファイルの取り込みが確定した時点で古くなる。
    /// 残すと「インポート実行」ボタン（<c>IsEnabled="{Binding HasPreview}"</c>）が別ファイルに対して
    /// 有効なまま残り、押せば修正前のファイルを修正済みデータの上へ丸ごと取り込み直せてしまう。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_直接インポート_成功したら別ファイルのプレビューもクリアすること()
    {
        // Arrange: 別ファイルのプレビューを表示したまま直接インポートを実行する状況
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
        await _viewModel.RunImportAsync(DirectImportFilePath);

        // Assert
        _viewModel.HasPreview.Should().BeFalse(
            "陳腐化したプレビューを残すと「インポート実行」ボタンが有効なまま残るため");
        _viewModel.ImportPreviewFile.Should().BeEmpty();
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
        await _viewModel.RunImportAsync(DirectImportFilePath);

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
        await _viewModel.RunImportAsync(DirectImportFilePath);

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
        await _viewModel.RunImportAsync(DirectImportFilePath);

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
    /// 共通化後は同じ経路を通ることを、直接インポート側からの呼び出しでも表明する。
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
        await viewModel.RunImportAsync(DirectImportFilePath);

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

    #region 部分成功時のプレビュー破棄（Issue #1781）

    // 部分成功（一部エラー）分岐は ClearPreview() を呼んでいなかったため、
    // HasPreview=true・ImportPreviewFile=同じファイルのまま「インポート実行」ボタン
    // （IsEnabled="{Binding HasPreview}"）が有効に残り、押すと同じ CSV を丸ごと取り込み直せた。
    // ledger_detail のインポートは skip-existing を持たないため、登録済みの明細がそのまま重複し
    // カードの残高チェーンが壊れる。

    /// <summary>
    /// 部分成功でもプレビューが破棄されること（成功分岐と対称にする、Issue #1781）
    /// </summary>
    [Fact]
    public async Task ExecuteImportAsync_部分成功時にプレビューを破棄すること()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 98, errorCount: 2));

        _viewModel.HasPreview.Should().BeTrue("プレビューがセットアップされていること");

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasPreview.Should().BeFalse(
            "「インポート実行」ボタンの IsEnabled が HasPreview に束縛されているため");
        _viewModel.ImportPreviewFile.Should().BeEmpty();
        _viewModel.PreviewItems.Should().BeEmpty();
    }

    /// <summary>
    /// 部分成功のあと「インポート実行」を押し直しても取り込みが走らないこと（Issue #1781）
    /// </summary>
    /// <remarks>
    /// HasPreview の値だけを見るテストでは、この Issue の実害（同じファイルの再取り込みによる
    /// 二重登録）を表明できない。「次の操作が効かないこと」まで検証する
    /// （CLAUDE.md「状態値だけでなく次の操作が効くことをテストで表明する」）。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_部分成功後に再実行しても同じファイルを取り込み直さないこと()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 98, errorCount: 2));

        // Act: 職員がエラー行を直そうとして「インポート実行」をもう一度押す
        await _viewModel.ExecuteImportAsync();
        await _viewModel.ExecuteImportAsync();

        // Assert
        _importServiceMock.Verify(
            s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()),
            Times.Once,
            "登録済みの98件が重複するため、2回目の取り込みは走ってはならない");
        _viewModel.StatusMessage.Should().Contain(
            "プレビュー",
            "再実行が拒否された理由と次の操作を案内すること");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// 部分成功でもエラー一覧は残ること（Issue #1781）
    /// </summary>
    /// <remarks>
    /// エラー一覧の表示条件は ImportErrors.Count（DataExportImportDialog.xaml:570）で
    /// HasPreview とは独立しているため、プレビューを畳んでも修正対象の行は画面に残る。
    /// 「完了メッセージを出す欄を、その完了処理で消える表示条件に紐付けない」（Issue #1727）の確認。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_部分成功でプレビューを破棄してもエラー一覧は残ること()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 98, errorCount: 2));

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasPreview.Should().BeFalse();
        _viewModel.ImportErrors.Should().HaveCount(2, "どの行を直せばよいかは画面に残す必要があるため");
        _viewModel.ImportErrors.Should().Contain("行3: IDmが不正です");
        _viewModel.LastImportedFile.Should().Be(
            ImportSourceFilePath,
            "どのファイルを取り込んだかはプレビューを畳んだ後も表示され続けること");
    }

    /// <summary>
    /// 1件も登録されなかった一部エラーではプレビューを維持すること（Issue #1781）
    /// </summary>
    /// <remarks>
    /// 破棄の判断根拠は「成否」ではなく「書き込みが確定したか」。全行エラーで 0 件なら
    /// 再実行しても二重登録は起きないため、CSV を直してそのまま押し直せる状態を残す。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_一部エラーでも1件も登録されていなければプレビューを維持すること()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 0, errorCount: 2));

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        _viewModel.HasPreview.Should().BeTrue("二重登録の危険がないため作業状態を捨てない");
        _viewModel.ImportPreviewFile.Should().Be(ImportSourceFilePath);
        _viewModel.HasImported.Should().BeFalse();
    }

    /// <summary>
    /// 部分成功の警告文言が二重登録の回避手順を案内すること（Issue #1781）
    /// </summary>
    /// <remarks>
    /// 「詳細はエラー一覧を確認してください」だけでは、直した CSV を丸ごと取り込み直す運用を招く。
    /// ledger_detail は skip-existing を持たないため、これは確実に二重登録になる。
    /// error-messages.md の3要素（何が／なぜ／どうすれば）で検証する。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_部分成功の警告文言が二重登録の回避手順を案内すること()
    {
        // Arrange
        SetupValidPreview(DataType.LedgerDetails);
        _importServiceMock
            .Setup(s => s.ImportLedgerDetailsAsync(It.IsAny<string>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 98, errorCount: 2));

        string warningMessage = null;
        _dialogServiceMock
            .Setup(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => warningMessage = message);

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        warningMessage.Should().NotBeNull("部分成功は警告ダイアログで通知されること");

        // 何が: 登録できた件数とエラー件数
        warningMessage.Should().Contain("98件").And.Contain("2件");

        // なぜ: 登録済みの行は取り込み確定済みで、再取り込みは二重登録になる
        warningMessage.Should().Contain("二重登録");

        // どうすれば: エラー行だけの CSV を作り、あらためてプレビューする
        warningMessage.Should().Contain("エラー一覧");
        warningMessage.Should().Contain("プレビュー");
        warningMessage.TrimEnd().Should().EndWith(
            "してください。",
            "行動指示型で終わること（error-messages.md）");
    }

    /// <summary>
    /// 1件も登録されなかった一部エラーでは「二重登録」を警告しないこと（Issue #1781）
    /// </summary>
    /// <remarks>
    /// 起きていない事象を警告すると、職員は存在しない重複を探して原因究明が止まる
    /// （error-messages.md「エラー文言で原因を断定する前に、その原因が成立する構成かを確認する」）。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_1件も登録されていなければ二重登録を警告しないこと()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 0, errorCount: 2));

        string warningMessage = null;
        _dialogServiceMock
            .Setup(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => warningMessage = message);

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        warningMessage.Should().NotBeNull();
        warningMessage.Should().NotContain("二重登録", "1件も書き込まれていないため重複は起こり得ない");
        warningMessage.Should().Contain("エラー一覧");
        warningMessage.TrimEnd().Should().EndWith("してください。");
    }

    /// <summary>
    /// 1件も登録されなかった場合、見出しが「完了」と述べないこと（Issue #1745）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 従来この分岐は無条件に「部分成功」とみなし、タイトル・本文とも
    /// 「インポート完了（一部エラー）」「インポートが完了しましたが」と表示していた。
    /// 本文の <c>BuildPartialImportGuidance(0)</c> は正しく「登録が確定した行はありません」と
    /// 述べるため、見出しと本文が同一ダイアログ内で矛盾し、職員はデータが入ったのか判断できない
    /// （見出しと実態を一致させる、Issue #1783 と同じ判断）。
    /// </para>
    /// <para>
    /// Issue #1745 で利用履歴のインポートがトランザクション化され、カード／職員と同様に
    /// 1件でも失敗すれば全件ロールバックするようになったため、<c>ImportedCount = 0</c> は
    /// 例外的な形ではなく**通常の失敗形**になった。管理者マニュアル §5.6.5 も
    /// 「1件も登録されません」と案内しており、その案内と画面が食い違わないようにする。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_1件も登録されていなければ完了と表示しないこと()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 0, errorCount: 2));

        string warningMessage = null;
        string warningTitle = null;
        _dialogServiceMock
            .Setup(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, title) =>
            {
                warningMessage = message;
                warningTitle = title;
            });

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert: ダイアログのタイトル・本文とも「完了」と述べない
        warningTitle.Should().NotBeNull();
        warningTitle.Should().NotContain("完了", "1件も書き込まれていないため完了ではない");
        warningTitle.Should().Contain("中断");
        warningMessage.Should().NotContain("完了", "本文の「登録が確定した行はありません」と矛盾させない");
        warningMessage.Should().Contain("2件", "エラー件数は伝える");

        // Assert: ステータス欄も同様。取り込めていない以上エラー表示にする
        _viewModel.StatusMessage.Should().NotContain("完了");
        _viewModel.IsStatusError.Should().BeTrue("1件も取り込めていないため成功色で表示しない");
    }

    /// <summary>
    /// 1件でも登録が確定していれば従来どおり「完了（一部エラー）」と表示すること（Issue #1745）
    /// </summary>
    /// <remarks>
    /// 失敗側だけを固定すると、実装が「常に中断と表示する」へ退化しても検出できない。
    /// 利用履歴詳細のインポートはトランザクションを持たず部分成功し得るため、この経路は実在する。
    /// </remarks>
    [Fact]
    public async Task ExecuteImportAsync_1件でも登録されていれば完了と表示すること()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 1, errorCount: 2));

        string warningTitle = null;
        _dialogServiceMock
            .Setup(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, title) => warningTitle = title);

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        warningTitle.Should().Contain("完了", "書き込みが確定しているため完了として通知する");
        warningTitle.Should().NotContain("中断");
        _viewModel.IsStatusError.Should().BeFalse("確定した行があるためエラー色にはしない");
    }

    /// <summary>
    /// 直接インポートは部分成功でもプレビューを破棄すること（Issue #1782）
    /// </summary>
    /// <remarks>
    /// 破棄の判定は「成否」ではなく「書き込みが確定したか」で行う（Issue #1781）。
    /// 部分成功でも登録済みの行は確定しているため、表示中のプレビューは古くなる。
    /// Issue #1785 で成功分岐について保存した「残す」性質は、Issue #1782 で
    /// 経路によらず破棄する方針へ改めた（本テストはその方針を部分成功分岐でも表明する）。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_直接インポート_部分成功でもプレビューを破棄すること()
    {
        // Arrange: 別ファイルのプレビューを表示したまま直接インポートを実行する状況
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 98, errorCount: 2));

        // Act
        await _viewModel.RunImportAsync(DirectImportFilePath);

        // Assert
        _viewModel.HasPreview.Should().BeFalse("98件の書き込みが確定した時点でプレビューは古くなるため");
        _viewModel.ImportPreviewFile.Should().BeEmpty();
    }

    /// <summary>
    /// 直接インポートが1件も登録できなかった一部エラーでは、プレビューを破棄しないこと（Issue #1782）
    /// </summary>
    /// <remarks>
    /// 破棄の条件は「書き込みが確定したか」であって「直接インポートを実行したか」ではない。
    /// 経路を条件から外した（Issue #1782）ことで、確定していない場合まで破棄する実装へ
    /// 倒れていないことを表明する。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_直接インポート_1件も登録されなければプレビューを残すこと()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 0, errorCount: 2));

        // Act
        await _viewModel.RunImportAsync(DirectImportFilePath);

        // Assert
        _viewModel.HasPreview.Should().BeTrue("1件も確定していないためプレビューは古くなっていない");
        _viewModel.ImportPreviewFile.Should().Be(ImportSourceFilePath);
    }

    /// <summary>
    /// 部分成功の警告文言を検証するための結果を組み立てる（Issue #1781）
    /// </summary>
    /// <remarks>
    /// 部分成功分岐の条件は Success=false かつ ErrorMessage が空であること。
    /// この2条件を各テストへ散らすと、条件が変わったときに空振りするテストが生まれる。
    /// </remarks>
    private static CsvImportResult CreatePartialSuccessResult(int importedCount, int errorCount)
    {
        var errors = new List<CsvImportError>();
        for (var i = 0; i < errorCount; i++)
        {
            errors.Add(new CsvImportError
            {
                LineNumber = 3 + i,
                Message = i == 0 ? "IDmが不正です" : $"{i + 1}件目の不正な行です"
            });
        }

        return new CsvImportResult
        {
            Success = false,
            ErrorMessage = null,
            ImportedCount = importedCount,
            ErrorCount = errorCount,
            SkippedCount = 0,
            Errors = errors
        };
    }

    #endregion

    #region 直接インポートによる陳腐化プレビューの破棄（Issue #1782）

    // 故障シナリオ（Issue #1782）:
    //   1. 職員が cards_old.csv をプレビュー（HasPreview=true）
    //   2. 誤りに気付き「直接インポート」で修正版 cards_fixed.csv を取り込み、成功
    //   3. プレビュー欄は cards_old.csv を表示したままで「インポート実行」も有効
    //   4. 職員がそれを押すと、修正前の cards_old.csv が修正済みデータの上へ再取り込みされる

    private static readonly string StalePreviewFilePath =
        System.IO.Path.Combine(ImportSourceDirectory, "cards_old.csv");

    private static readonly string FixedImportFilePath =
        System.IO.Path.Combine(ImportSourceDirectory, "cards_fixed.csv");

    /// <summary>
    /// 直接インポートの後に「インポート実行」を押しても、古いファイルが再取り込みされないこと（Issue #1782）
    /// </summary>
    /// <remarks>
    /// <c>HasPreview</c> の値だけを見るテストでは、この Issue の実害（ボタンが有効なまま残り、
    /// 押すと古いファイルを取り込み直せること）を表現できない。実際に続けて
    /// <see cref="DataExportImportViewModel.ExecuteImportAsync"/> を呼び、
    /// インポートサービスへ渡ったパスが直接インポートで選んだ1件だけであることを表明する。
    /// </remarks>
    [Fact]
    public async Task 直接インポート後にインポート実行を押しても古いファイルは再取り込みされないこと()
    {
        // Arrange: cards_old.csv のプレビューを表示したまま、修正版を直接インポートする
        SetupValidPreview();
        _viewModel.ImportPreviewFile = StalePreviewFilePath;

        var importedFiles = new List<string>();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string, bool>((path, _) => importedFiles.Add(path))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 });

        // Act: 直接インポート → 未処理に見えるパネルの「インポート実行」を続けて押す
        await _viewModel.RunImportAsync(FixedImportFilePath);
        await _viewModel.ExecuteImportAsync();

        // Assert
        importedFiles.Should().ContainSingle(
            "「インポート実行」で古いファイルが取り込まれてはならない")
            .Which.Should().Be(FixedImportFilePath);
        _viewModel.IsStatusError.Should().BeTrue(
            "プレビューが無い状態での「インポート実行」は入力不足として弾かれること");
        _viewModel.StatusMessage.Should().Contain(
            "プレビュー",
            "何をすればよいか（先にプレビュー）を示すこと");
    }

    /// <summary>
    /// 別ファイルのプレビューを破棄したとき、完了ダイアログでその旨を案内すること（Issue #1782）
    /// </summary>
    /// <remarks>
    /// プレビューを黙って消すと、職員には作業状態が理由不明に失われたように見える。
    /// error-messages.md の3要素（何が／なぜ／どうすれば）で案内する。
    /// </remarks>
    [Fact]
    public async Task 直接インポートで別ファイルのプレビューを破棄したら完了ダイアログで案内すること()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = StalePreviewFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 });

        string informationMessage = null;
        _dialogServiceMock
            .Setup(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => informationMessage = message);

        // Act
        await _viewModel.RunImportAsync(FixedImportFilePath);

        // Assert
        informationMessage.Should().NotBeNull("インポート完了ダイアログが表示されること");
        informationMessage.Should().Contain(
            "cards_old.csv",
            "何が消えたのかを特定できること（ClearPreview 前に確定させた値であること）");
        informationMessage.Should().Contain(
            "最新ではなくなった",
            "なぜ消したのか（取り込みで件数が古くなる）を示すこと");
        informationMessage.TrimEnd().Should().EndWith(
            "してください。",
            "行動指示型で終わること（error-messages.md）");
    }

    /// <summary>
    /// プレビュー経由の取り込みでは、プレビュー破棄の案内を出さないこと（Issue #1782）
    /// </summary>
    /// <remarks>
    /// この経路で消えるのは、いま取り込んだファイル自身のプレビューであり消えて当然。
    /// 起きていない事象（別ファイルの作業状態の消失）を案内すると、職員は失われていない作業を探す
    /// （error-messages.md「原因を断定する前に、その原因が成立する構成かを確認する」）。
    /// </remarks>
    [Fact]
    public async Task プレビュー経由の取り込みではプレビュー破棄を案内しないこと()
    {
        // Arrange
        SetupValidPreview();
        _viewModel.ImportPreviewFile = ImportSourceFilePath;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 });

        string informationMessage = null;
        _dialogServiceMock
            .Setup(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => informationMessage = message);

        // Act
        await _viewModel.ExecuteImportAsync();

        // Assert
        informationMessage.Should().NotBeNull();
        informationMessage.Should().NotContain(
            "別ファイル",
            "取り込んだファイル自身のプレビューが消えただけで、失われた作業は無いため");
        _viewModel.HasPreview.Should().BeFalse("従来どおりプレビューは畳まれること");
    }

    /// <summary>
    /// プレビューが表示されていなければ、破棄の案内を出さないこと（Issue #1782）
    /// </summary>
    [Fact]
    public void BuildDiscardedPreviewNotice_プレビュー未表示なら案内しないこと()
    {
        var notice = DataExportImportViewModel.BuildDiscardedPreviewNotice(
            hadPreview: false,
            previewFilePath: StalePreviewFilePath,
            importedFilePath: FixedImportFilePath);

        notice.Should().BeEmpty();
    }

    /// <summary>
    /// 大文字小文字だけが異なる同一パスは「別ファイル」として案内しないこと（Issue #1782）
    /// </summary>
    /// <remarks>
    /// Windows のパスは大文字小文字を区別しない。区別する比較にすると、
    /// 同じファイルを取り込んだだけで「別ファイルのプレビューを消した」と誤って案内する。
    /// </remarks>
    [Fact]
    public void BuildDiscardedPreviewNotice_大文字小文字違いの同一パスは案内しないこと()
    {
        var notice = DataExportImportViewModel.BuildDiscardedPreviewNotice(
            hadPreview: true,
            previewFilePath: StalePreviewFilePath,
            importedFilePath: StalePreviewFilePath.ToUpperInvariant());

        notice.Should().BeEmpty();
    }

    #endregion

    #region インポート失敗時に「インポート完了」を表示しないこと（Issue #1783）

    // 故障シナリオ（Issue #1783）:
    //   1. 職員が対象カードを解決できない利用履歴 CSV を取り込む
    //   2. ImportLedgersAsync が Success=false / ErrorMessage 付きで返る
    //   3. 赤い「インポートに失敗しました」ダイアログと
    //      「インポート完了: C:\temp\ledger.csv」（DataExportImportDialog.xaml:354）が同時に表示され、
    //      職員はデータが入ったのかどうか判断できない
    //
    // 成功時・部分成功時に LastImportedFile が設定されることは既存テスト
    // （Issue #1782 / #1781 の各リージョン）が表明済みのため、ここでは重複させない。

    private static readonly string FailedImportFilePath =
        System.IO.Path.Combine(ImportSourceDirectory, "ledger_broken.csv");

    /// <summary>
    /// インポート自体が失敗したときは「インポート完了」表示を出さないこと（Issue #1783）
    /// </summary>
    /// <remarks>
    /// <c>LastImportedFile</c> は結果表示であり、結果を検査する前に代入してはならない。
    /// 同じ ViewModel のエクスポート側は <c>if (result.Success)</c> の内側で
    /// <c>LastExportedFile</c> を代入しており、インポート側だけが取り残されていた。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_失敗時は完了表示のファイル名を設定しないこと()
    {
        // Arrange
        _viewModel.SelectedImportType = DataType.Cards;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = "指定されたファイルが見つかりません。",
                ImportedCount = 0
            });

        // Act
        await _viewModel.RunImportAsync(FailedImportFilePath);

        // Assert
        _viewModel.LastImportedFile.Should().BeEmpty(
            "エラーダイアログの隣に「インポート完了: <ファイル名>」が並ぶと、"
            + "データが入ったのかどうか職員が判断できないため");
        _dialogServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), "インポートエラー"),
            Times.Once);
    }

    /// <summary>
    /// 全行エラーで1件も登録されなかったときも完了表示を出さないこと（Issue #1783）
    /// </summary>
    /// <remarks>
    /// 判定は「成否」ではなく「書き込みが確定したか」（Issue #1781 の <c>importCommitted</c>）に相乗りさせる。
    /// 全行エラーはダイアログこそ「インポート完了（一部エラー）」だが登録件数は 0 件で、
    /// 取り込まれたファイルとして表示すると実態と食い違う。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_全行エラーで1件も登録されなければ完了表示を出さないこと()
    {
        // Arrange
        _viewModel.SelectedImportType = DataType.Cards;
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(CreatePartialSuccessResult(importedCount: 0, errorCount: 2));

        // Act
        await _viewModel.RunImportAsync(FailedImportFilePath);

        // Assert
        _viewModel.LastImportedFile.Should().BeEmpty(
            "1件も書き込まれていないファイルを「インポート完了」として表示してはならないため");
        _viewModel.ImportErrors.Should().HaveCount(2, "どの行を直すかは画面に残す必要があるため");
    }

    /// <summary>
    /// 直前の成功で出ていた完了表示が、次のインポート失敗で消えること（Issue #1783）
    /// </summary>
    /// <remarks>
    /// 失敗分岐で代入しないだけでは不十分。前回成功時のファイル名が残り続けるため、
    /// 同じファイルを直して取り込み直して失敗した場合に、
    /// 「インポートに失敗しました」と「インポート完了: 同じファイル名」が再び同居する。
    /// 表示は最新の操作の結果を表すべきなので、取り込み開始時にクリアする。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_直前の成功の完了表示が次の失敗で消えること()
    {
        // Arrange: 1回目は成功、2回目は同じファイルで失敗
        _viewModel.SelectedImportType = DataType.Cards;
        _importServiceMock
            .SetupSequence(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 })
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = "ヘッダー行が想定と異なります",
                ImportedCount = 0
            });

        // Act
        await _viewModel.RunImportAsync(ImportSourceFilePath);
        var afterSuccess = _viewModel.LastImportedFile;

        await _viewModel.RunImportAsync(ImportSourceFilePath);

        // Assert
        afterSuccess.Should().Be(ImportSourceFilePath, "1回目は取り込みが確定しているため");
        _viewModel.LastImportedFile.Should().BeEmpty(
            "前回成功時のファイル名が残ると、失敗通知の隣に同じファイル名の完了表示が並ぶため");
    }

    /// <summary>
    /// 直前の成功で出ていた完了表示が、次のインポートの例外で消えること（Issue #1783）
    /// </summary>
    /// <remarks>
    /// 例外経路（共有フォルダーの切断など）は結果オブジェクトを返さないため、
    /// 「失敗分岐で代入しない」だけでは前回の表示が残る。開始時のクリアで両経路をまとめて塞ぐ。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_直前の成功の完了表示が次の例外で消えること()
    {
        // Arrange
        _viewModel.SelectedImportType = DataType.Cards;
        _importServiceMock
            .SetupSequence(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 })
            .ThrowsAsync(new InvalidOperationException("ネットワークパスが見つかりません"));

        // Act
        await _viewModel.RunImportAsync(ImportSourceFilePath);
        await _viewModel.RunImportAsync(FailedImportFilePath);

        // Assert
        _viewModel.LastImportedFile.Should().BeEmpty(
            "例外で中断した取り込みを、前回のファイル名で「完了」と表示してはならないため");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    #endregion

    #region Issue #1784: インポート結果ダイアログ表示時にプログレスバー(IsBusy)が閉じていること

    // Issue #1383 はエクスポート経路について「BeginBusy スコープを抜けて IsBusy=false が確定してから
    // MessageBox を表示する」形へ是正したが、インポート経路は結果ダイアログをスコープの内側で
    // 表示したままだった。DataExportImportDialog.xaml の全面オーバーレイ Border（Grid.RowSpan=6・
    // 不透明 OverlayBrush）と不確定 ProgressBar、「インポート中...」の文字がモーダル表示中ずっと
    // 重なって描かれるため、職員は「インポートが完了しました」の下でプログレスバーが回り続けるのを見て
    // 処理が続いているのか判断できない。
    //
    // 検証は「IsBusy の最終値」ではなく「ダイアログ呼び出し時点の値」で行う。
    // 最終値は修正前でも false（using が必ず Dispose する）のため、Callback でその瞬間を
    // 捕捉しないと修正前のコードでも通ってしまう。

    private const string ProgressBarClosedReason =
        "Issue #1784: 結果ダイアログの表示中にプログレスバーが残っていてはならない";

    /// <summary>
    /// 成功時、ShowInformation が呼ばれる時点で IsBusy=false になっていること
    /// </summary>
    [Fact]
    public async Task RunImportAsync_成功時_ShowInformation呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 });

        bool? isBusyAtDialog = null;
        _dialogServiceMock
            .Setup(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtDialog = _viewModel.IsBusy);

        // Act
        await _viewModel.RunImportAsync(ImportSourceFilePath);

        // Assert
        isBusyAtDialog.Should().NotBeNull("完了ダイアログが表示されているはず");
        isBusyAtDialog.Should().BeFalse(ProgressBarClosedReason);
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 失敗時、ShowError が呼ばれる時点で IsBusy=false になっていること
    /// </summary>
    [Fact]
    public async Task RunImportAsync_失敗時_ShowError呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult
            {
                Success = false,
                ErrorMessage = "対象カードを解決できませんでした"
            });

        bool? isBusyAtDialog = null;
        _dialogServiceMock
            .Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtDialog = _viewModel.IsBusy);

        // Act
        await _viewModel.RunImportAsync(ImportSourceFilePath);

        // Assert
        isBusyAtDialog.Should().NotBeNull("エラーダイアログが表示されているはず");
        isBusyAtDialog.Should().BeFalse(ProgressBarClosedReason);
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 部分成功（一部エラー）時、ShowWarning が呼ばれる時点で IsBusy=false になっていること
    /// </summary>
    [Fact]
    public async Task RunImportAsync_部分成功時_ShowWarning呼び出し時点でIsBusyがfalseであること()
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

        bool? isBusyAtDialog = null;
        _dialogServiceMock
            .Setup(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtDialog = _viewModel.IsBusy);

        // Act
        await _viewModel.RunImportAsync(ImportSourceFilePath);

        // Assert
        isBusyAtDialog.Should().NotBeNull("一部エラーの警告ダイアログが表示されているはず");
        isBusyAtDialog.Should().BeFalse(ProgressBarClosedReason);
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 成功したが監査ログ記録に失敗した場合も、ShowWarning 呼び出し時点で IsBusy=false であること
    /// </summary>
    /// <remarks>
    /// 成功分岐には ShowInformation と ShowWarning の2つの出口があり、後者だけ取り残される形の
    /// 修正漏れを防ぐために独立したテストを置く（Issue #1741 で追加された分岐）。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_監査ログ記録失敗時_ShowWarning呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange: operation_log への INSERT だけが失敗する ViewModel
        var dialogServiceMock = new Mock<IDialogService>();
        var viewModel = CreateViewModelWithFailingAuditLog(dialogServiceMock);
        SetupValidPreview(viewModel);
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 });

        bool? isBusyAtDialog = null;
        dialogServiceMock
            .Setup(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtDialog = viewModel.IsBusy);

        // Act
        await viewModel.RunImportAsync(ImportSourceFilePath);

        // Assert
        isBusyAtDialog.Should().NotBeNull("操作ログ記録失敗の警告ダイアログが表示されているはず");
        isBusyAtDialog.Should().BeFalse(ProgressBarClosedReason);
        viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 例外発生時、ShowError が呼ばれる時点で IsBusy=false になっていること
    /// </summary>
    /// <remarks>
    /// 共有フォルダーの切断等で ImportXxxAsync が送出する経路。catch 節はスコープの内側にあるため、
    /// 結果分岐と同様にダイアログ表示だけをスコープの外へ持ち出す必要がある。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_例外発生時_ShowError呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("ネットワークパスが見つかりません"));

        bool? isBusyAtDialog = null;
        _dialogServiceMock
            .Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtDialog = _viewModel.IsBusy);

        // Act
        await _viewModel.RunImportAsync(ImportSourceFilePath);

        // Assert
        isBusyAtDialog.Should().NotBeNull("例外時もエラーダイアログが表示されているはず");
        isBusyAtDialog.Should().BeFalse(ProgressBarClosedReason);
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 結果ダイアログの表示自体が例外を投げても、確定済みの取り込みを巻き添えにしないこと
    /// </summary>
    /// <remarks>
    /// Issue #1784 でダイアログ表示を `BeginBusy` スコープの外へ出したことにより、
    /// 表示処理は `try` の外側に位置するようになった。ここを素通しにすると、
    /// 取り込みがコミットされ監査ログも記録された**後**の通知失敗が
    /// `AsyncRelayCommand` 経由で未捕捉例外になり、職員は成功直後にクラッシュを見て
    /// 「データが入ったのか」を判断できない（Issue #1783 が消したはずの曖昧さの再発）。
    /// 「コミット確定後の後処理を、成否の判定に巻き込まない」（CLAUDE.md / Issue #1727）に従い、
    /// 表示の失敗はログへ逃がし、ステータス欄に残る結果表示で情報を保つ。
    /// </remarks>
    [Fact]
    public async Task RunImportAsync_結果ダイアログの表示が例外を投げても確定済みの取り込みを巻き添えにしないこと()
    {
        // Arrange
        SetupValidPreview();
        _importServiceMock
            .Setup(s => s.ImportCardsAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new CsvImportResult { Success = true, ImportedCount = 3 });
        _dialogServiceMock
            .Setup(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("ウィンドウは既に破棄されています"));

        // Act
        Func<Task> act = () => _viewModel.RunImportAsync(ImportSourceFilePath);

        // Assert
        await act.Should().NotThrowAsync(
            "取り込みは確定済みであり、通知の失敗を取り込みの失敗として伝播させてはならない");
        _viewModel.IsStatusError.Should().BeFalse(
            "ダイアログを出せなかっただけで、取り込み自体は成功しているため");
        _viewModel.StatusMessage.Should().Contain(
            "インポート完了",
            "ダイアログが出せなくてもステータス欄には結果が残ること");
        _viewModel.LastImportedFile.Should().Be(
            ImportSourceFilePath,
            "書き込みが確定した事実は通知の成否に左右されないため");
        _viewModel.IsBusy.Should().BeFalse("表示が失敗してもプログレスバーは閉じたままであること");
    }

    #endregion

    #region Issue #1816: カード読み取りの fire-and-forget が例外を握りつぶさないこと

    /// <summary>
    /// 読み取り中に DB 例外が出たら、例外を呼び出し元へ抜かずステータスへ案内すること
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_読み取り中の例外_ステータスへ案内し例外を伝播しないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        _viewModel.IsWaitingForCardTouch = true;

        // Act
        Func<Task> act = () => _viewModel.HandleCardReadAsync(idm);

        // Assert
        await act.Should().NotThrowAsync("fire-and-forget の呼び出し元は例外を観測できないため");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().NotContain(
            "database is locked", "生の例外メッセージを職員へ出さないこと（Issue #1614）");
        _viewModel.StatusMessage.Should().EndWith("してください。");
        _viewModel.IsWaitingForCardTouch.Should().BeTrue("タッチ待ちへ戻して再試行できること");
        _viewModel.TouchedCardIdm.Should().BeEmpty("確認の済んでいないカードを指定に残さないこと");
    }

    /// <summary>
    /// 対のテスト: 登録済みカードを読み取れた場合はタッチ待ちを解除しエラーにしないこと
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_登録済みカード_タッチ待ちを解除しエラーにしないこと()
    {
        // Arrange
        var idm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new ICCardManager.Models.IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "H-001"
        });
        _viewModel.IsWaitingForCardTouch = true;

        // Act
        await _viewModel.HandleCardReadAsync(idm);

        // Assert
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.IsWaitingForCardTouch.Should().BeFalse();
        _viewModel.TouchedCardIdm.Should().Be(idm);
    }

    /// <summary>
    /// Issue #1816: タッチ待ちでない状態で本体が実行されても、状態を書き換えないこと
    /// </summary>
    [Fact]
    public async Task HandleCardReadAsync_タッチ待ちでなければ何もしないこと()
    {
        // Arrange
        var firstIdm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), false)).ReturnsAsync(
            new ICCardManager.Models.IcCard { CardIdm = firstIdm, CardType = "はやかけん", CardNumber = "H-001" });
        _viewModel.IsWaitingForCardTouch = true;
        await _viewModel.HandleCardReadAsync(firstIdm);
        _viewModel.IsWaitingForCardTouch.Should().BeFalse("前提: 1 件目の読み取りでタッチ待ちが解除される");

        // Act
        await _viewModel.HandleCardReadAsync("0807060504030201");

        // Assert
        _viewModel.TouchedCardIdm.Should().Be(firstIdm, "2 件目が 1 件目の読み取り結果を上書きしないこと");
        _cardRepositoryMock.Verify(r => r.GetByIdmAsync("0807060504030201", false), Times.Never);
    }

    #endregion
}