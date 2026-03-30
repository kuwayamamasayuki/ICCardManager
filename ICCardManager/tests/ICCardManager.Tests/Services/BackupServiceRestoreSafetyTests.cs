using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1108: BackupServiceのリストア安全性テスト
/// 共有モードで他PCの接続を検出し、リストアを拒否する機能を検証する。
/// </summary>
public class BackupServiceRestoreSafetyTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _backupDirectory;

    public BackupServiceRestoreSafetyTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"RestoreSafetyTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    #region CanAcquireExclusiveLock テスト

    /// <summary>
    /// ファイルが存在しない場合、排他ロックは取得可能（trueを返す）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_ファイル未存在でtrueを返すこと()
    {
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.db");

        BackupService.CanAcquireExclusiveLock(nonExistentPath).Should().BeTrue();
    }

    /// <summary>
    /// ファイルが存在し他プロセスが使用していない場合、排他ロックが取得できること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_未使用ファイルでtrueを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "unlocked.db");
        File.WriteAllText(dbPath, "test");

        BackupService.CanAcquireExclusiveLock(dbPath).Should().BeTrue();
    }

    /// <summary>
    /// ファイルが他プロセスにロックされている場合、falseを返すこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_ロック中ファイルでfalseを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "locked.db");
        File.WriteAllText(dbPath, "test");

        // 他プロセスによるロックをシミュレート
        using var lockStream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        BackupService.CanAcquireExclusiveLock(dbPath).Should().BeFalse();
    }

    /// <summary>
    /// ファイルが読み取り共有で開かれている場合、排他ロックが取得できないこと
    /// （他PCのSQLite接続をシミュレート）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_読み取り共有ロック中でfalseを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "shared_read.db");
        File.WriteAllText(dbPath, "test");

        // SQLiteの典型的なロック（Read/Write + ReadWrite共有）をシミュレート
        using var lockStream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        BackupService.CanAcquireExclusiveLock(dbPath).Should().BeFalse();
    }

    #endregion

    #region CleanupJournalFiles テスト

    /// <summary>
    /// ジャーナルファイルが存在する場合に削除されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupJournalFiles_ジャーナルファイルを削除すること()
    {
        var dbPath = Path.Combine(_testDirectory, "cleanup.db");
        File.WriteAllText(dbPath, "test");

        // ジャーナルファイルを作成
        File.WriteAllText(dbPath + "-journal", "journal");
        File.WriteAllText(dbPath + "-wal", "wal");
        File.WriteAllText(dbPath + "-shm", "shm");

        var service = CreateService(dbPath);
        service.CleanupJournalFiles(dbPath);

        File.Exists(dbPath + "-journal").Should().BeFalse();
        File.Exists(dbPath + "-wal").Should().BeFalse();
        File.Exists(dbPath + "-shm").Should().BeFalse();
        // DBファイル自体は削除されないこと
        File.Exists(dbPath).Should().BeTrue();
    }

    /// <summary>
    /// ジャーナルファイルが存在しない場合もエラーにならないこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupJournalFiles_ファイル未存在でもエラーにならないこと()
    {
        var dbPath = Path.Combine(_testDirectory, "no_journal.db");

        var service = CreateService(dbPath);
        var act = () => service.CleanupJournalFiles(dbPath);

        act.Should().NotThrow();
    }

    #endregion

    #region RestoreFromBackup 共有モード テスト

    /// <summary>
    /// 共有モードで他PCが接続中の場合、リストアが拒否されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_共有モードで他接続ありの場合falseを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "shared_restore.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // バックアップファイルを作成
        var backupPath = Path.Combine(_backupDirectory, "backup.db");
        File.Copy(dbPath, backupPath);

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        // 他プロセスのロックをシミュレート（DBファイルを排他的に開く）
        // まず自PCの接続を閉じてから他PCロックをシミュレートする
        dbContext.CloseConnection();
        using var otherPcLock = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Act
        var result = service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeFalse("他PCが接続中のためリストアは拒否されるべき");
    }

    /// <summary>
    /// 共有モードで他PCが接続していない場合、リストアが成功すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_共有モードで他接続なしの場合成功すること()
    {
        var dbPath = Path.Combine(_testDirectory, "shared_restore_ok.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // バックアップファイルを作成
        var backupPath = Path.Combine(_backupDirectory, "backup_ok.db");
        File.Copy(dbPath, backupPath);

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        // Act（他接続なし）
        var result = service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// リストア成功後にジャーナルファイルが清掃されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_成功後にジャーナルファイルが削除されること()
    {
        var dbPath = Path.Combine(_testDirectory, "journal_cleanup.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // バックアップファイルを作成
        var backupPath = Path.Combine(_backupDirectory, "backup_jc.db");
        File.Copy(dbPath, backupPath);

        // ジャーナルファイルを作成（リストア前の古いジャーナル）
        File.WriteAllText(dbPath + "-journal", "old journal");

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        // Act
        var result = service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        File.Exists(dbPath + "-journal").Should().BeFalse("リストア後にジャーナルファイルが清掃されるべき");
    }

    /// <summary>
    /// IsSharedModeプロパティがDbContextの状態を正しく反映すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void IsSharedMode_DbContextの状態を反映すること()
    {
        var dbPath = Path.Combine(_testDirectory, "shared_check.db");
        using var sharedContext = new DbContext(dbPath);
        var sharedService = new BackupService(
            sharedContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);
        sharedService.IsSharedMode.Should().BeTrue();

        using var localContext = new DbContext();
        var localService = new BackupService(
            localContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);
        localService.IsSharedMode.Should().BeFalse();
    }

    #endregion

    #region ヘルパーメソッド

    private BackupService CreateService(string dbPath)
    {
        var dbContext = new DbContext(dbPath);
        return new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);
    }

    private Mock<ISettingsRepository> CreateSettingsRepositoryMock()
    {
        var mock = new Mock<ISettingsRepository>();
        mock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _backupDirectory });
        return mock;
    }

    #endregion
}
