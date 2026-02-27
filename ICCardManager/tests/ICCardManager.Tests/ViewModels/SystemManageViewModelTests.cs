using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
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
    private readonly SystemManageViewModel _viewModel;

    public SystemManageViewModelTests()
    {
        _dbContext = new DbContext(":memory:");
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        var loggerMock = new Mock<ILogger<BackupService>>();

        _backupServiceMock = new Mock<BackupService>(
            _dbContext,
            _settingsRepositoryMock.Object,
            loggerMock.Object);
        _navigationServiceMock = new Mock<INavigationService>();

        _viewModel = new SystemManageViewModel(
            _backupServiceMock.Object,
            _settingsRepositoryMock.Object,
            _navigationServiceMock.Object);
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
        _viewModel.StatusMessage.Should().Contain("失敗しました").And.Contain("disk error");
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
}
