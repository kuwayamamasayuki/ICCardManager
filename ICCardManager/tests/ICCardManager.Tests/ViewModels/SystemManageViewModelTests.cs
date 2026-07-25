using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// SystemManageViewModelの単体テスト
/// </summary>
public class SystemManageViewModelTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly Mock<BackupService> _backupServiceMock;
    private readonly Mock<INavigationService> _navigationServiceMock;
    private readonly Mock<ICCardManager.Services.ISafeFileLauncher> _safeFileLauncherMock;
    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly Mock<IBackupHealthService> _backupHealthServiceMock;
    private readonly SystemManageViewModel _viewModel;

    private const string TestDatabasePath = @"C:\ProgramData\ICCardManager\iccard.db";

    public SystemManageViewModelTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        var loggerMock = new Mock<ILogger<BackupService>>();

        _backupServiceMock = new Mock<BackupService>(
            _dbContext,
            _settingsRepositoryMock.Object,
            loggerMock.Object);
        _navigationServiceMock = new Mock<INavigationService>();

        // OperationLogger (Issue #1302): 実DB + 実Context でログ書き込みを副作用として許容
        var operationLogRepository = new OperationLogRepository(_dbContext);
        var operatorContext = new CurrentOperatorContext(new SystemClock());
        var operationLogger = new OperationLogger(operationLogRepository, operatorContext);

        _safeFileLauncherMock = new Mock<ICCardManager.Services.ISafeFileLauncher>();
        _safeFileLauncherMock.Setup(l => l.LaunchFolder(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Ok());

        // データベース情報（Issue #1686）: 既定はローカルモード・読み書き可能
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _databaseInfoMock.SetupGet(d => d.DatabasePath).Returns(TestDatabasePath);
        _databaseInfoMock.SetupGet(d => d.IsSharedMode).Returns(false);
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);
        _databaseInfoMock.Setup(d => d.CheckWritable()).Returns(true);

        // Issue #1705: リストアは職員認証を必須とする。既定は認証成功を返す
        // （個別テストで null=キャンセルへ上書きする）。
        _staffAuthServiceMock = new Mock<IStaffAuthService>();
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "0123456789ABCDEF", StaffName = "テスト職員" });

        // Issue #1689: バックアップ健全性。既定は「記録なし」を返し、
        // 健全性表示のテストは個別に GetHealthAsync を上書きする。
        _backupHealthServiceMock = new Mock<IBackupHealthService>();
        _backupHealthServiceMock
            .Setup(s => s.GetHealthAsync())
            .ReturnsAsync(new BackupHealthInfo { MaxGenerations = AppConstants.MaxBackupGenerations });

        _viewModel = new SystemManageViewModel(
            _backupServiceMock.Object,
            _settingsRepositoryMock.Object,
            _navigationServiceMock.Object,
            operationLogger,
            _safeFileLauncherMock.Object,
            _databaseInfoMock.Object,
            _staffAuthServiceMock.Object,
            _backupHealthServiceMock.Object);
    }

    /// <summary>
    /// 共有モード設定の IDatabaseInfo を持つ ViewModel を生成（Issue #1686 のモード表示テスト用）
    /// </summary>
    private SystemManageViewModel CreateViewModelWithSharedMode(string databasePath)
    {
        var sharedInfoMock = new Mock<IDatabaseInfo>();
        sharedInfoMock.SetupGet(d => d.DatabasePath).Returns(databasePath);
        sharedInfoMock.SetupGet(d => d.IsSharedMode).Returns(true);

        var operationLogRepository = new OperationLogRepository(_dbContext);
        var operatorContext = new CurrentOperatorContext(new SystemClock());
        var operationLogger = new OperationLogger(operationLogRepository, operatorContext);

        return new SystemManageViewModel(
            _backupServiceMock.Object,
            _settingsRepositoryMock.Object,
            _navigationServiceMock.Object,
            operationLogger,
            _safeFileLauncherMock.Object,
            sharedInfoMock.Object,
            _staffAuthServiceMock.Object,
            _backupHealthServiceMock.Object);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
    }

    #region 初期状態テスト

    [Fact]
    public void 初期状態でBackupFilesが空であること()
    {
        _viewModel.BackupFiles.Should().BeEmpty();
    }

    [Fact]
    public void 初期状態でSelectedBackupがnullであること()
    {
        _viewModel.SelectedBackup.Should().BeNull();
    }

    [Fact]
    public void 初期状態でStatusMessageが空文字であること()
    {
        _viewModel.StatusMessage.Should().BeEmpty();
    }

    [Fact]
    public void 初期状態でIsStatusErrorがfalseであること()
    {
        _viewModel.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public void 初期状態でHasSelectedBackupがfalseであること()
    {
        _viewModel.HasSelectedBackup.Should().BeFalse();
    }

    #endregion

    #region リストア認証テスト（Issue #1705）

    [Fact]
    public async Task RestoreAsync_認証がキャンセルされた場合_リストアを実行しない()
    {
        // Arrange: バックアップを選択済みだが、職員認証はキャンセル（null）される
        _viewModel.SelectedBackup = new BackupFileInfo
        {
            FileName = "backup_20260101.db",
            FilePath = @"C:\backup\backup_20260101.db",
        };
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult?)null);

        // Act
        await _viewModel.RestoreAsync();

        // Assert: 認証がなければ DB は一切上書きされない（破壊的操作の認可ゲート）
        _backupServiceMock.Verify(b => b.RestoreFromBackup(It.IsAny<string>()), Times.Never);
        _viewModel.StatusMessage.Should().Contain("職員認証");
    }

    [Fact]
    public async Task RestoreAsync_認証が要求されること()
    {
        // Arrange: バックアップを選択済み・認証キャンセルで確認ダイアログ前に停止させる
        _viewModel.SelectedBackup = new BackupFileInfo
        {
            FileName = "backup_20260101.db",
            FilePath = @"C:\backup\backup_20260101.db",
        };
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult?)null);

        // Act
        await _viewModel.RestoreAsync();

        // Assert: リストア操作名で認証が要求される
        _staffAuthServiceMock.Verify(
            a => a.RequestAuthenticationAsync(It.Is<string>(s => s.Contains("リストア"))),
            Times.Once);
    }

    [Fact]
    public async Task RestoreAsync_バックアップ未選択の場合_認証を要求しない()
    {
        // Arrange: 何も選択していない
        _viewModel.SelectedBackup = null;

        // Act
        await _viewModel.RestoreAsync();

        // Assert: 選択ガードで早期 return するため認証は要求されない
        _staffAuthServiceMock.Verify(
            a => a.RequestAuthenticationAsync(It.IsAny<string>()),
            Times.Never);
        _viewModel.StatusMessage.Should().Contain("選択してください");
    }

    [Fact]
    public void 初期状態でLastBackupFileが空文字であること()
    {
        _viewModel.LastBackupFile.Should().BeEmpty();
    }

    #endregion

    #region LoadBackupsAsync テスト

    [Fact]
    public async Task LoadBackupsAsync_バックアップファイルがある場合にBackupFilesに追加されること()
    {
        // Arrange
        var backupFiles = new List<BackupFileInfo>
        {
            new BackupFileInfo { FileName = "backup_001.db", FilePath = "/backups/backup_001.db", CreatedAt = DateTime.Now.AddDays(-2), FileSize = 1024 },
            new BackupFileInfo { FileName = "backup_002.db", FilePath = "/backups/backup_002.db", CreatedAt = DateTime.Now.AddDays(-1), FileSize = 2048 },
            new BackupFileInfo { FileName = "backup_003.db", FilePath = "/backups/backup_003.db", CreatedAt = DateTime.Now, FileSize = 3072 },
        };
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(backupFiles);

        // Act
        await _viewModel.LoadBackupsAsync();

        // Assert
        _viewModel.BackupFiles.Should().HaveCount(3);
        _viewModel.StatusMessage.Should().Contain("3件");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task LoadBackupsAsync_バックアップファイルがない場合にメッセージが表示されること()
    {
        // Arrange
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(Enumerable.Empty<BackupFileInfo>());

        // Act
        await _viewModel.LoadBackupsAsync();

        // Assert
        _viewModel.BackupFiles.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Contain("見つかりません");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task LoadBackupsAsync_例外発生時にエラーメッセージが表示されること()
    {
        // Arrange
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ThrowsAsync(new Exception("disk error"));

        // Act
        await _viewModel.LoadBackupsAsync();

        // Assert
        // Issue #1614: 生の ex.Message（"disk error"）を UI に漏らさず、
        // 「何が／なぜ／どうすれば」を満たす文言を表示する。技術的詳細はログのみに記録。
        _viewModel.StatusMessage.Should().Contain("失敗しました");
        _viewModel.StatusMessage.Should().NotContain("disk error", "技術的詳細はログのみに記録する");
        _viewModel.StatusMessage.Should().MatchRegex("してください。?$|連絡してください。?$",
            "行動指示で終わるべき");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    [Fact]
    public async Task LoadBackupsAsync_2回呼び出しで前回の結果がクリアされること()
    {
        // Arrange - 1回目: 3件
        var firstBatch = new List<BackupFileInfo>
        {
            new BackupFileInfo { FileName = "a.db", FilePath = "/a.db", CreatedAt = DateTime.Now },
            new BackupFileInfo { FileName = "b.db", FilePath = "/b.db", CreatedAt = DateTime.Now },
            new BackupFileInfo { FileName = "c.db", FilePath = "/c.db", CreatedAt = DateTime.Now },
        };
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(firstBatch);

        await _viewModel.LoadBackupsAsync();
        _viewModel.BackupFiles.Should().HaveCount(3);

        // Arrange - 2回目: 1件
        var secondBatch = new List<BackupFileInfo>
        {
            new BackupFileInfo { FileName = "d.db", FilePath = "/d.db", CreatedAt = DateTime.Now },
        };
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(secondBatch);

        // Act
        await _viewModel.LoadBackupsAsync();

        // Assert - 前回の3件がクリアされ、新しい1件のみ
        _viewModel.BackupFiles.Should().HaveCount(1);
        _viewModel.BackupFiles[0].FileName.Should().Be("d.db");
    }

    [Fact]
    public async Task LoadBackupsAsync_成功時にIsStatusErrorがfalseであること()
    {
        // Arrange
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(new[] { new BackupFileInfo { FileName = "a.db", FilePath = "/a.db", CreatedAt = DateTime.Now } });

        // Act
        await _viewModel.LoadBackupsAsync();

        // Assert
        _viewModel.IsStatusError.Should().BeFalse();
    }

    #endregion

    #region CreateBackupCoreAsync テスト (Issue #1417)

    [Fact]
    public async Task CreateBackupCoreAsync_成功時にStatusMessageが完了通知のまま件数表示で上書きされないこと()
    {
        // Arrange: バックアップ成功 + 既存3件 → LoadBackupsInternalAsync(false) が件数を上書きしない経路の検証
        const string backupPath = "/backups/backup_manual_20260501_120000.db";
        _backupServiceMock.Setup(s => s.CreateBackupAsync(backupPath)).ReturnsAsync(true);
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(new List<BackupFileInfo>
            {
                new BackupFileInfo { FileName = "old1.db", FilePath = "/backups/old1.db", CreatedAt = DateTime.Now.AddDays(-1) },
                new BackupFileInfo { FileName = "old2.db", FilePath = "/backups/old2.db", CreatedAt = DateTime.Now.AddDays(-2) },
                new BackupFileInfo { FileName = "old3.db", FilePath = "/backups/old3.db", CreatedAt = DateTime.Now },
            });

        // Act
        await _viewModel.CreateBackupCoreAsync(backupPath);

        // Assert: Issue #1417 のバグ - 完了メッセージが LoadBackupsAsync の件数表示で上書きされる挙動の回帰防止
        _viewModel.StatusMessage.Should().StartWith("バックアップを作成しました:");
        _viewModel.StatusMessage.Should().Contain("backup_manual_20260501_120000.db");
        _viewModel.StatusMessage.Should().NotContain("件のバックアップが見つかりました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task CreateBackupCoreAsync_成功時にLastBackupFileが指定パスに更新されること()
    {
        // Arrange
        const string backupPath = "/backups/manual.db";
        _backupServiceMock.Setup(s => s.CreateBackupAsync(backupPath)).ReturnsAsync(true);
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(Enumerable.Empty<BackupFileInfo>());

        // Act
        await _viewModel.CreateBackupCoreAsync(backupPath);

        // Assert
        _viewModel.LastBackupFile.Should().Be(backupPath);
    }

    [Fact]
    public async Task CreateBackupCoreAsync_成功時にBackupFilesが更新されること()
    {
        // Arrange: バックアップ成功時は内部で LoadBackupsInternalAsync が呼ばれ、一覧が更新される
        const string backupPath = "/backups/manual.db";
        var refreshedList = new List<BackupFileInfo>
        {
            new BackupFileInfo { FileName = "manual.db", FilePath = backupPath, CreatedAt = DateTime.Now },
        };
        _backupServiceMock.Setup(s => s.CreateBackupAsync(backupPath)).ReturnsAsync(true);
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync()).ReturnsAsync(refreshedList);

        // Act
        await _viewModel.CreateBackupCoreAsync(backupPath);

        // Assert
        _viewModel.BackupFiles.Should().HaveCount(1);
        _viewModel.BackupFiles.Single().FilePath.Should().Be(backupPath);
    }

    [Fact]
    public async Task CreateBackupCoreAsync_BackupServiceがfalseを返すとき失敗メッセージが表示されること()
    {
        // Arrange
        const string backupPath = "/backups/manual.db";
        _backupServiceMock.Setup(s => s.CreateBackupAsync(backupPath)).ReturnsAsync(false);

        // Act
        await _viewModel.CreateBackupCoreAsync(backupPath);

        // Assert
        // Issue #1614: メッセージ品質ガイドライン（何が／なぜ／どうすれば）準拠の文言に改善。
        // 完全一致ではなく「何が」キーワード＋行動指示で終わる品質基準で検証する。
        _viewModel.StatusMessage.Should().StartWith("バックアップの作成に失敗しました", "何が: 作成失敗");
        _viewModel.StatusMessage.Should().MatchRegex("してください。?$",
            "どうすれば: 行動指示で終わるべき");
        _viewModel.StatusMessage.Length.Should().BeGreaterThanOrEqualTo(20,
            "3要素を含む十分な説明であるべき");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.LastBackupFile.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateBackupCoreAsync_例外発生時に例外文言を漏らさず品質基準を満たすメッセージが表示されること()
    {
        // Arrange
        const string backupPath = "/backups/manual.db";
        _backupServiceMock.Setup(s => s.CreateBackupAsync(backupPath))
            .ThrowsAsync(new Exception("disk full"));

        // Act
        await _viewModel.CreateBackupCoreAsync(backupPath);

        // Assert
        // Issue #1614: 生の ex.Message（"disk full"）を UI に漏らさない。技術的詳細はログのみ。
        _viewModel.StatusMessage.Should().Contain("失敗");
        _viewModel.StatusMessage.Should().NotContain("disk full", "技術的詳細はログのみに記録する");
        _viewModel.StatusMessage.Should().MatchRegex("してください。?$|連絡してください。?$",
            "行動指示で終わるべき");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    #endregion

    #region LoadBackupsInternalAsync テスト (Issue #1417)

    [Fact]
    public async Task LoadBackupsInternalAsync_announceCountがfalseの場合に件数表示でStatusMessageを上書きしないこと()
    {
        // Arrange: バックアップ成功直後の状態を再現し、その後の LoadBackupsInternalAsync(false) で上書きされないことを検証
        var backupFiles = new List<BackupFileInfo>
        {
            new BackupFileInfo { FileName = "a.db", FilePath = "/a.db", CreatedAt = DateTime.Now },
            new BackupFileInfo { FileName = "b.db", FilePath = "/b.db", CreatedAt = DateTime.Now },
        };
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync()).ReturnsAsync(backupFiles);
        _backupServiceMock.Setup(s => s.CreateBackupAsync(It.IsAny<string>())).ReturnsAsync(true);
        await _viewModel.CreateBackupCoreAsync("/backups/test.db");

        var statusBeforeReload = _viewModel.StatusMessage;
        statusBeforeReload.Should().StartWith("バックアップを作成しました:");

        // Act: 件数告知なしで再読込
        await _viewModel.LoadBackupsInternalAsync(announceCount: false);

        // Assert: 一覧は更新されているが StatusMessage は上書きされていない
        _viewModel.BackupFiles.Should().HaveCount(2);
        _viewModel.StatusMessage.Should().Be(statusBeforeReload);
    }

    [Fact]
    public async Task LoadBackupsInternalAsync_announceCountがtrueの場合は従来通り件数表示すること()
    {
        // Arrange
        var backupFiles = new List<BackupFileInfo>
        {
            new BackupFileInfo { FileName = "a.db", FilePath = "/a.db", CreatedAt = DateTime.Now },
        };
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync()).ReturnsAsync(backupFiles);

        // Act
        await _viewModel.LoadBackupsInternalAsync(announceCount: true);

        // Assert
        _viewModel.StatusMessage.Should().Contain("1件");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    #endregion

    #region HasSelectedBackup テスト

    [Fact]
    public void HasSelectedBackup_SelectedBackupが設定された場合trueであること()
    {
        // Arrange & Act
        _viewModel.SelectedBackup = new BackupFileInfo
        {
            FileName = "backup_test.db",
            FilePath = "/backups/backup_test.db",
            CreatedAt = DateTime.Now
        };

        // Assert
        _viewModel.HasSelectedBackup.Should().BeTrue();
    }

    [Fact]
    public void HasSelectedBackup_SelectedBackupをnullに戻すとfalseになること()
    {
        // Arrange
        _viewModel.SelectedBackup = new BackupFileInfo { FileName = "test.db", FilePath = "/test.db", CreatedAt = DateTime.Now };
        _viewModel.HasSelectedBackup.Should().BeTrue();

        // Act
        _viewModel.SelectedBackup = null;

        // Assert
        _viewModel.HasSelectedBackup.Should().BeFalse();
    }

    #endregion

    #region OnSelectedBackupChanged テスト

    [Fact]
    public void OnSelectedBackupChanged_HasSelectedBackupのPropertyChangedが発火すること()
    {
        // Arrange
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Act
        _viewModel.SelectedBackup = new BackupFileInfo
        {
            FileName = "backup.db",
            FilePath = "/backups/backup.db",
            CreatedAt = DateTime.Now
        };

        // Assert
        changedProperties.Should().Contain("HasSelectedBackup");
        changedProperties.Should().Contain("SelectedBackup");
    }

    #endregion

    #region IsBusy 遷移テスト

    [Fact]
    public async Task LoadBackupsAsync_処理中にIsBusyがtrueになり完了後にfalseに戻ること()
    {
        // Arrange
        var busyStates = new List<bool>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.IsBusy))
            {
                busyStates.Add(_viewModel.IsBusy);
            }
        };

        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(Enumerable.Empty<BackupFileInfo>());

        // Act
        await _viewModel.LoadBackupsAsync();

        // Assert - true（開始）→ false（終了）の順にIsBusyが遷移
        busyStates.Should().HaveCountGreaterOrEqualTo(2);
        busyStates.First().Should().BeTrue();
        busyStates.Last().Should().BeFalse();
    }

    #endregion

    #region OpenBackupFolder（Issue #1465）

    [Fact]
    public void OpenBackupFolder_バックアップ無し_エラー表示()
    {
        _viewModel.OpenBackupFolderCommand.Execute(null);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("バックアップ");
        _safeFileLauncherMock.Verify(l => l.LaunchFolder(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OpenBackupFolder_バックアップ有り_ISafeFileLauncherへ親フォルダで委譲()
    {
        _viewModel.BackupFiles.Add(new BackupFileInfo
        {
            FileName = "backup_001.db",
            FilePath = "C:\\backups\\backup_001.db",
            CreatedAt = DateTime.Now,
            FileSize = 1024
        });

        _viewModel.OpenBackupFolderCommand.Execute(null);

        _safeFileLauncherMock.Verify(l => l.LaunchFolder("C:\\backups"), Times.Once);
    }

    [Fact]
    public void OpenBackupFolder_launcherが失敗を返した場合_エラー表示()
    {
        _viewModel.BackupFiles.Add(new BackupFileInfo
        {
            FileName = "backup_001.db",
            FilePath = "C:\\backups\\backup_001.db",
            CreatedAt = DateTime.Now,
            FileSize = 1024
        });
        _safeFileLauncherMock.Setup(l => l.LaunchFolder(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Fail("フォルダが見つかりません"));

        _viewModel.OpenBackupFolderCommand.Execute(null);

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("見つかりません");
    }

    #endregion

    #region データベース情報の常設表示・接続テスト（Issue #1686）

    [Fact]
    public void DatabasePathDisplay_使用中のDBパスを返すこと()
    {
        _viewModel.DatabasePathDisplay.Should().Be(TestDatabasePath);
    }

    [Fact]
    public void ローカルモード時_モード表示がローカルモードであること()
    {
        _viewModel.DatabaseModeText.Should().Contain("ローカルモード");
        _viewModel.DatabaseModeIcon.Should().Be("💻");
    }

    [Fact]
    public void 共有モード時_モード表示が共有モードであること()
    {
        var sharedViewModel = CreateViewModelWithSharedMode(@"\\server\share\iccard.db");

        sharedViewModel.DatabasePathDisplay.Should().Be(@"\\server\share\iccard.db");
        sharedViewModel.DatabaseModeText.Should().Contain("共有モード");
        sharedViewModel.DatabaseModeIcon.Should().Be("🔗");
    }

    [Fact]
    public async Task 接続テスト_読み書き可能な場合_成功メッセージを表示すること()
    {
        await _viewModel.TestDatabaseConnectionAsync();

        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.StatusMessage.Should().Contain("成功");
        _viewModel.StatusMessage.Should().Contain("書き込み");
        _databaseInfoMock.Verify(d => d.CheckConnection(), Times.Once);
        _databaseInfoMock.Verify(d => d.CheckWritable(), Times.Once);
    }

    [Fact]
    public async Task 接続テスト_到達不可の場合_パス入りの3要素エラーメッセージを表示すること()
    {
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);

        await _viewModel.TestDatabaseConnectionAsync();

        _viewModel.IsStatusError.Should().BeTrue();
        // 3要素: 何が（どのDBに接続できないか）・なぜ（ネットワーク/共有フォルダ）・どうすれば（確認してください）
        _viewModel.StatusMessage.Should().Contain(TestDatabasePath, "どのDBに接続できないかを示す（何が）");
        _viewModel.StatusMessage.Should().Contain("接続できません");
        _viewModel.StatusMessage.Should().EndWith("確認してください。");
        _viewModel.StatusMessage.Length.Should().BeGreaterOrEqualTo(20, "エラーメッセージ品質基準（最小20文字）");
        // 到達できない場合は書込テストまで進まない
        _databaseInfoMock.Verify(d => d.CheckWritable(), Times.Never);
    }

    [Fact]
    public async Task 接続テスト_書込不可の場合_アクセス権を示唆する3要素エラーメッセージを表示すること()
    {
        _databaseInfoMock.Setup(d => d.CheckWritable()).Returns(false);

        await _viewModel.TestDatabaseConnectionAsync();

        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain(TestDatabasePath);
        _viewModel.StatusMessage.Should().Contain("書き込みができません");
        _viewModel.StatusMessage.Should().Contain("アクセス権");
        _viewModel.StatusMessage.Should().EndWith("確認してください。");
        _viewModel.StatusMessage.Length.Should().BeGreaterOrEqualTo(20, "エラーメッセージ品質基準（最小20文字）");
    }

    [Fact]
    public async Task 接続テスト_実行中はIsBusyがtrueになること()
    {
        var busyStates = new List<bool>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemManageViewModel.IsBusy))
                busyStates.Add(_viewModel.IsBusy);
        };

        await _viewModel.TestDatabaseConnectionAsync();

        busyStates.Should().HaveCountGreaterOrEqualTo(2);
        busyStates.First().Should().BeTrue();
        busyStates.Last().Should().BeFalse();
    }

    #endregion

    #region バックアップ健全性表示テスト（Issue #1689）

    private void SetupHealth(BackupHealthInfo health) =>
        _backupHealthServiceMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(health);

    [Fact]
    public async Task バックアップ状況_最終成功日時と経過日数が表示されること()
    {
        SetupHealth(new BackupHealthInfo { LastSuccessAt = DateTime.Now.Date.AddDays(-3).AddHours(8) });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.LastBackupSuccessText.Should().Contain("最終成功:");
        _viewModel.LastBackupSuccessText.Should().Contain("3日前");
    }

    [Theory]
    [InlineData(0, "本日")]
    [InlineData(1, "昨日")]
    [InlineData(5, "5日前")]
    public async Task バックアップ状況_経過日数が自然な日本語で表示されること(int daysAgo, string expected)
    {
        SetupHealth(new BackupHealthInfo { LastSuccessAt = DateTime.Now.Date.AddDays(-daysAgo).AddHours(8) });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.LastBackupSuccessText.Should().Contain(expected);
    }

    [Fact]
    public async Task バックアップ状況_記録がない場合は記録なしと表示されること()
    {
        // 「-」だけだと故障と誤読されるため、次にいつ表示されるかまで案内する
        SetupHealth(new BackupHealthInfo { LastSuccessAt = null });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.LastBackupSuccessText.Should().Contain("記録なし");
        _viewModel.IsBackupStale.Should().BeFalse("判断材料がない状態は警告扱いにしない");
    }

    [Fact]
    public async Task バックアップ状況_しきい値を超えると警告状態になること()
    {
        SetupHealth(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now.Date.AddDays(-(AppConstants.BackupStaleWarningDays + 1))
        });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.IsBackupStale.Should().BeTrue();
        // 色だけでなくアイコンでも状態を伝える（UI/UX原則）
        _viewModel.BackupHealthIcon.Should().Be("⚠");
    }

    [Fact]
    public async Task バックアップ状況_しきい値内なら正常アイコンになること()
    {
        SetupHealth(new BackupHealthInfo { LastSuccessAt = DateTime.Now.Date });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.IsBackupStale.Should().BeFalse();
        _viewModel.BackupHealthIcon.Should().Be("✔");
    }

    [Fact]
    public async Task バックアップ状況_世代数が上限とともに表示されること()
    {
        SetupHealth(new BackupHealthInfo { GenerationCount = 12, MaxGenerations = 30 });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.BackupGenerationText.Should().Be("保持世代: 12 / 30");
    }

    [Fact]
    public async Task バックアップ状況_空き容量が単位付きで表示されること()
    {
        SetupHealth(new BackupHealthInfo { FreeSpaceBytes = 1024L * 1024 * 1024 * 5 });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.BackupFreeSpaceText.Should().Be("保存先の空き容量: 5.0 GB");
    }

    [Fact]
    public async Task バックアップ状況_空き容量が取得できない場合は不明と表示されること()
    {
        SetupHealth(new BackupHealthInfo { FreeSpaceBytes = null });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.BackupFreeSpaceText.Should().Contain("不明");
    }

    [Fact]
    public async Task バックアップ状況_保存先フォルダが表示されること()
    {
        SetupHealth(new BackupHealthInfo { BackupFolderPath = @"\\fileserver\iccard\backup" });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.BackupFolderText.Should().Be(@"保存先: \\fileserver\iccard\backup");
    }

    [Fact]
    public async Task バックアップ状況_最終実施PC名が表示されること()
    {
        SetupHealth(new BackupHealthInfo { LastSuccessMachineName = "PC-KEIRI-01" });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.LastBackupMachineText.Should().Be("最終実施PC: PC-KEIRI-01");
    }

    [Fact]
    public async Task バックアップ状況_VACUUM実施日と実施PCが表示されること()
    {
        SetupHealth(new BackupHealthInfo
        {
            LastVacuumDate = new DateTime(2026, 7, 10),
            LastVacuumMachineName = "PC-KEIRI-03"
        });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.LastVacuumText.Should().Be("最終最適化(VACUUM): 2026/07/10（実施PC: PC-KEIRI-03）");
    }

    [Fact]
    public async Task バックアップ状況_VACUUM未実行なら未実行と表示されること()
    {
        SetupHealth(new BackupHealthInfo { LastVacuumDate = null });

        await _viewModel.LoadBackupHealthAsync();

        _viewModel.LastVacuumText.Should().Be("最終最適化(VACUUM): 未実行");
    }

    [Fact]
    public void バックアップ状況_共有モードでのみPC名関連を表示すること()
    {
        // ローカルモードでは実施PCが自明なため表示しない
        _viewModel.IsSharedMode.Should().BeFalse();

        var sharedViewModel = CreateViewModelWithSharedMode(@"\\server\share\iccard.db");
        sharedViewModel.IsSharedMode.Should().BeTrue();
    }

    [Fact]
    public async Task バックアップ状況_読み込み時に表示プロパティの変更通知が発火すること()
    {
        var changed = new List<string>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        SetupHealth(new BackupHealthInfo { LastSuccessAt = DateTime.Now, GenerationCount = 3 });

        await _viewModel.LoadBackupHealthAsync();

        changed.Should().Contain(nameof(SystemManageViewModel.LastBackupSuccessText));
        changed.Should().Contain(nameof(SystemManageViewModel.BackupGenerationText));
        changed.Should().Contain(nameof(SystemManageViewModel.BackupFreeSpaceText));
        changed.Should().Contain(nameof(SystemManageViewModel.IsBackupStale));
        changed.Should().Contain(nameof(SystemManageViewModel.BackupHealthIcon));
    }

    [Fact]
    public async Task バックアップ一覧読み込み時に健全性も更新されること()
    {
        // 一覧と健全性は同じフォルダの状態を映すため、手動バックアップ直後も同時に更新される
        await _viewModel.LoadBackupsAsync();

        _backupHealthServiceMock.Verify(s => s.GetHealthAsync(), Times.AtLeastOnce);
    }

    #endregion
}
