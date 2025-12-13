using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// BackupServiceの単体テスト
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;
    private readonly string _backupDirectory;
    private readonly DbContext _dbContext;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        // テスト用の一時ディレクトリを作成
        _testDirectory = Path.Combine(Path.GetTempPath(), $"BackupServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        // テスト用DBファイルパス
        _testDbPath = Path.Combine(_testDirectory, "test.db");

        // バックアップ先ディレクトリ
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);

        // テスト用DBを作成
        _dbContext = new DbContext(_testDbPath);
        _dbContext.InitializeDatabase();

        // ISettingsRepositoryをモック
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _backupDirectory });

        _service = new BackupService(_dbContext, _settingsRepositoryMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();

        // テスト用ディレクトリを削除
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // クリーンアップ失敗は無視
        }

        GC.SuppressFinalize(this);
    }

    #region ExecuteAutoBackupAsync テスト

    /// <summary>
    /// 自動バックアップが正常に作成されることを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_ValidSettings_CreatesBackupFile()
    {
        // Act
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().NotBeNull();
        File.Exists(result).Should().BeTrue();

        // ファイル名が正しいフォーマットか確認
        var fileName = Path.GetFileName(result);
        fileName.Should().StartWith("backup_");
        fileName.Should().EndWith(".db");
    }

    /// <summary>
    /// バックアップファイルがDBの内容をコピーしていることを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_BackupContent_ContainsDatabaseData()
    {
        // Act
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().NotBeNull();

        // バックアップファイルのサイズがソースDBと同じ
        var sourceSize = new FileInfo(_testDbPath).Length;
        var backupSize = new FileInfo(result!).Length;
        backupSize.Should().Be(sourceSize);
    }

    /// <summary>
    /// カスタムパスが設定されている場合、そのパスにバックアップが作成されることを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_CustomBackupPath_UsesCustomPath()
    {
        // Arrange
        var customBackupPath = Path.Combine(_testDirectory, "custom_backup");
        Directory.CreateDirectory(customBackupPath);

        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = customBackupPath });

        // Act
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().NotBeNull();
        Path.GetDirectoryName(result).Should().Be(customBackupPath);
    }

    /// <summary>
    /// バックアップパスが空の場合、デフォルトパスが使用されることを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_EmptyBackupPath_UsesDefaultPath()
    {
        // Arrange
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = string.Empty });

        // Act
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().NotBeNull();
        // デフォルトパスはLocalApplicationData内
        result.Should().Contain("ICCardManager");
        result.Should().Contain("backup");
    }

    /// <summary>
    /// 30世代を超えると古いバックアップが削除されることを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_Over30Generations_DeletesOldBackups()
    {
        // Arrange - 32個のダミーバックアップファイルを作成
        for (int i = 0; i < 32; i++)
        {
            var timestamp = DateTime.Now.AddMinutes(-i).ToString("yyyyMMdd_HHmmss");
            var dummyBackupPath = Path.Combine(_backupDirectory, $"backup_{timestamp}.db");
            await File.WriteAllTextAsync(dummyBackupPath, "dummy");
            // ファイルの作成日時を調整
            File.SetCreationTime(dummyBackupPath, DateTime.Now.AddMinutes(-i));
        }

        // Act
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert - 待機して削除処理を完了させる
        await Task.Delay(500);

        result.Should().NotBeNull();
        var backupFiles = Directory.GetFiles(_backupDirectory, "backup_*.db");
        // 30世代 + 新規1つ = 31ではなく、30以下になるはず
        backupFiles.Length.Should().BeLessOrEqualTo(30);
    }

    /// <summary>
    /// バックアップディレクトリが存在しない場合に自動作成されることを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_DirectoryNotExists_CreatesDirectory()
    {
        // Arrange
        var newBackupPath = Path.Combine(_testDirectory, "new_backup_dir");
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = newBackupPath });

        // Act
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().NotBeNull();
        Directory.Exists(newBackupPath).Should().BeTrue();
    }

    #endregion

    #region CreateBackup テスト

    /// <summary>
    /// 指定パスにバックアップが作成されることを確認
    /// </summary>
    [Fact]
    public void CreateBackup_ValidPath_CreatesBackupFile()
    {
        // Arrange
        var backupPath = Path.Combine(_backupDirectory, "manual_backup.db");

        // Act
        var result = _service.CreateBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        File.Exists(backupPath).Should().BeTrue();
    }

    /// <summary>
    /// 親ディレクトリが存在しない場合に自動作成されることを確認
    /// </summary>
    [Fact]
    public void CreateBackup_ParentDirectoryNotExists_CreatesDirectory()
    {
        // Arrange
        var backupPath = Path.Combine(_testDirectory, "nested", "dir", "backup.db");

        // Act
        var result = _service.CreateBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        File.Exists(backupPath).Should().BeTrue();
    }

    /// <summary>
    /// 既存ファイルを上書きできることを確認
    /// </summary>
    [Fact]
    public void CreateBackup_FileExists_OverwritesFile()
    {
        // Arrange
        var backupPath = Path.Combine(_backupDirectory, "existing_backup.db");
        File.WriteAllText(backupPath, "old content");
        var originalSize = new FileInfo(backupPath).Length;

        // Act
        var result = _service.CreateBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        var newSize = new FileInfo(backupPath).Length;
        newSize.Should().NotBe(originalSize);
    }

    /// <summary>
    /// 無効なパスでfalseを返すことを確認
    /// </summary>
    [Fact]
    public void CreateBackup_InvalidPath_ReturnsFalse()
    {
        // Arrange - 不正なパス（Windows以外では異なる結果になる可能性あり）
        var invalidPath = Path.Combine(_testDirectory, new string('x', 300), "backup.db");

        // Act
        var result = _service.CreateBackup(invalidPath);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region RestoreFromBackup テスト

    /// <summary>
    /// 正常にリストアできることを確認
    /// </summary>
    [Fact]
    public async Task RestoreFromBackup_ValidBackup_RestoresSuccessfully()
    {
        // Arrange - バックアップを作成
        await _service.ExecuteAutoBackupAsync();
        var backupFiles = Directory.GetFiles(_backupDirectory, "backup_*.db");
        var latestBackup = backupFiles[0];
        var backupSize = new FileInfo(latestBackup).Length;

        // リストア先として別のDBファイルを使用
        var restoreTargetPath = Path.Combine(_testDirectory, "restore_target.db");

        // リストア先に初期ファイルを作成
        File.WriteAllText(restoreTargetPath, "initial content");
        var initialSize = new FileInfo(restoreTargetPath).Length;

        // DbContextを作成して即座に破棄（接続を開かずにパスだけ設定）
        var restoreDbContext = new DbContext(restoreTargetPath);
        var restoreService = new BackupService(restoreDbContext, _settingsRepositoryMock.Object);
        restoreDbContext.Dispose(); // 接続を開く前に破棄

        // Act
        var result = restoreService.RestoreFromBackup(latestBackup);

        // Assert
        result.Should().BeTrue();
        File.Exists(restoreTargetPath).Should().BeTrue();
        // バックアップファイルと同じサイズになっていることを確認
        new FileInfo(restoreTargetPath).Length.Should().Be(backupSize);
    }

    /// <summary>
    /// 存在しないファイルからのリストアでfalseを返すことを確認
    /// </summary>
    [Fact]
    public void RestoreFromBackup_FileNotExists_ReturnsFalse()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_backupDirectory, "non_existent.db");

        // Act
        var result = _service.RestoreFromBackup(nonExistentPath);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 空ファイルからのリストアでもファイルコピーは成功することを確認
    /// </summary>
    [Fact]
    public void RestoreFromBackup_EmptyFile_CopiesFile()
    {
        // Arrange
        var emptyFilePath = Path.Combine(_backupDirectory, "empty.db");
        File.WriteAllText(emptyFilePath, string.Empty);

        // リストア先として別のファイルを使用
        var restoreTargetPath = Path.Combine(_testDirectory, "restore_empty_target.db");
        File.WriteAllText(restoreTargetPath, "original content");

        // DbContextを作成して即座に破棄
        var restoreDbContext = new DbContext(restoreTargetPath);
        var restoreService = new BackupService(restoreDbContext, _settingsRepositoryMock.Object);
        restoreDbContext.Dispose();

        // Act
        var result = restoreService.RestoreFromBackup(emptyFilePath);

        // Assert
        result.Should().BeTrue();
        // 空ファイルがコピーされたことを確認
        new FileInfo(restoreTargetPath).Length.Should().Be(0);
    }

    /// <summary>
    /// リストア失敗時（バックアップファイルがロック中）にfalseを返すことを確認
    /// </summary>
    [Fact]
    public void RestoreFromBackup_BackupFileLocked_ReturnsFalse()
    {
        // Arrange
        var restoreTargetPath = Path.Combine(_testDirectory, "restore_failure_target.db");
        File.WriteAllText(restoreTargetPath, "original database content");

        var lockedFilePath = Path.Combine(_backupDirectory, "locked.db");

        // ロックされたファイルを作成
        using var lockedStream = new FileStream(lockedFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        lockedStream.WriteByte(0);

        var restoreDbContext = new DbContext(restoreTargetPath);
        var testService = new BackupService(restoreDbContext, _settingsRepositoryMock.Object);
        restoreDbContext.Dispose();

        // Act
        var result = testService.RestoreFromBackup(lockedFilePath);

        // Assert
        result.Should().BeFalse();
        // 元のファイルが保持されている
        File.Exists(restoreTargetPath).Should().BeTrue();
    }

    /// <summary>
    /// リストア時に元のDBファイルが退避・復元されることを確認
    /// </summary>
    [Fact]
    public async Task RestoreFromBackup_OverwritesExistingDb()
    {
        // Arrange - まずバックアップを作成
        await _service.ExecuteAutoBackupAsync();
        var backupFiles = Directory.GetFiles(_backupDirectory, "backup_*.db");
        var latestBackup = backupFiles[0];
        var backupContent = File.ReadAllBytes(latestBackup);

        // リストア先のDBファイル（異なる内容）
        var restoreTargetPath = Path.Combine(_testDirectory, "overwrite_target.db");
        File.WriteAllText(restoreTargetPath, "different content that should be overwritten");

        var restoreDbContext = new DbContext(restoreTargetPath);
        var restoreService = new BackupService(restoreDbContext, _settingsRepositoryMock.Object);
        restoreDbContext.Dispose();

        // Act
        var result = restoreService.RestoreFromBackup(latestBackup);

        // Assert
        result.Should().BeTrue();
        var restoredContent = File.ReadAllBytes(restoreTargetPath);
        restoredContent.Should().BeEquivalentTo(backupContent);
    }

    #endregion

    #region GetBackupFilesAsync テスト

    /// <summary>
    /// バックアップファイル一覧が取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_WithBackups_ReturnsFileList()
    {
        // Arrange - 3つのバックアップを作成
        for (int i = 0; i < 3; i++)
        {
            var timestamp = DateTime.Now.AddMinutes(-i).ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(_backupDirectory, $"backup_{timestamp}.db");
            await File.WriteAllTextAsync(backupPath, $"backup{i}");
            File.SetCreationTime(backupPath, DateTime.Now.AddMinutes(-i));
            await Task.Delay(10); // タイムスタンプの違いを確保
        }

        // Act
        var result = (await _service.GetBackupFilesAsync()).ToList();

        // Assert
        result.Should().HaveCount(3);
        result.Should().AllSatisfy(f => f.FileName.Should().StartWith("backup_"));
        result.Should().AllSatisfy(f => f.FileName.Should().EndWith(".db"));
    }

    /// <summary>
    /// バックアップファイル一覧が作成日時降順でソートされていることを確認
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_ReturnsFilesOrderedByCreationTimeDesc()
    {
        // Arrange - 時間差で3つのバックアップを作成
        var timestamps = new[] { "20240101_120000", "20240101_130000", "20240101_140000" };
        var baseDate = new DateTime(2024, 1, 1, 12, 0, 0);

        for (int i = 0; i < timestamps.Length; i++)
        {
            var backupPath = Path.Combine(_backupDirectory, $"backup_{timestamps[i]}.db");
            await File.WriteAllTextAsync(backupPath, $"backup{i}");
            File.SetCreationTime(backupPath, baseDate.AddHours(i));
        }

        // Act
        var result = (await _service.GetBackupFilesAsync()).ToList();

        // Assert
        result.Should().HaveCount(3);
        // 降順（新しい順）
        result[0].FileName.Should().Contain("140000");
        result[1].FileName.Should().Contain("130000");
        result[2].FileName.Should().Contain("120000");
    }

    /// <summary>
    /// バックアップディレクトリが存在しない場合に空リストを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_DirectoryNotExists_ReturnsEmptyList()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "non_existent_backup");
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = nonExistentPath });

        // Act
        var result = await _service.GetBackupFilesAsync();

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// バックアップ以外のファイルは取得されないことを確認
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_OtherFiles_NotIncluded()
    {
        // Arrange - バックアップファイルとそうでないファイルを作成
        await File.WriteAllTextAsync(Path.Combine(_backupDirectory, "backup_20240101_120000.db"), "backup");
        await File.WriteAllTextAsync(Path.Combine(_backupDirectory, "other_file.db"), "other");
        await File.WriteAllTextAsync(Path.Combine(_backupDirectory, "backup.txt"), "not a db");

        // Act
        var result = (await _service.GetBackupFilesAsync()).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].FileName.Should().Be("backup_20240101_120000.db");
    }

    /// <summary>
    /// BackupFileInfoに正しい情報が設定されることを確認
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_BackupFileInfo_ContainsCorrectData()
    {
        // Arrange
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(_backupDirectory, $"backup_{timestamp}.db");
        var content = "test backup content";
        await File.WriteAllTextAsync(backupPath, content);
        var creationTime = DateTime.Now;
        File.SetCreationTime(backupPath, creationTime);

        // Act
        var result = (await _service.GetBackupFilesAsync()).First();

        // Assert
        result.FileName.Should().Be($"backup_{timestamp}.db");
        result.FilePath.Should().Be(backupPath);
        result.FileSize.Should().Be(content.Length);
        result.CreatedAt.Should().BeCloseTo(creationTime, TimeSpan.FromSeconds(2));
    }

    #endregion

    #region CleanupOldBackups 統合テスト（ExecuteAutoBackupAsync経由）

    /// <summary>
    /// 30世代以下のバックアップは削除されないことを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_Under30Generations_KeepsAllBackups()
    {
        // Arrange - 29個のダミーバックアップを作成
        for (int i = 0; i < 29; i++)
        {
            var timestamp = DateTime.Now.AddMinutes(-(i + 1)).ToString("yyyyMMdd_HHmmss");
            var dummyBackupPath = Path.Combine(_backupDirectory, $"backup_{timestamp}.db");
            await File.WriteAllTextAsync(dummyBackupPath, "dummy");
            File.SetCreationTime(dummyBackupPath, DateTime.Now.AddMinutes(-(i + 1)));
        }

        // Act - 新しいバックアップを作成（合計30個）
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        await Task.Delay(500);

        result.Should().NotBeNull();
        var backupFiles = Directory.GetFiles(_backupDirectory, "backup_*.db");
        backupFiles.Length.Should().Be(30);
    }

    /// <summary>
    /// ちょうど30世代のバックアップは削除されないことを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_Exactly30Generations_DeletesOldest()
    {
        // Arrange - 30個のダミーバックアップを作成
        for (int i = 0; i < 30; i++)
        {
            var timestamp = DateTime.Now.AddMinutes(-(i + 1)).ToString("yyyyMMdd_HHmmss");
            var dummyBackupPath = Path.Combine(_backupDirectory, $"backup_{timestamp}.db");
            await File.WriteAllTextAsync(dummyBackupPath, "dummy");
            File.SetCreationTime(dummyBackupPath, DateTime.Now.AddMinutes(-(i + 1)));
        }

        // Act - 新しいバックアップを作成（合計31個だが、古いものが削除される）
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        await Task.Delay(500);

        result.Should().NotBeNull();
        var backupFiles = Directory.GetFiles(_backupDirectory, "backup_*.db");
        backupFiles.Length.Should().Be(30);
    }

    /// <summary>
    /// 削除対象がない場合（空のディレクトリ）でもエラーにならないことを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_EmptyDirectory_CompletesSuccessfully()
    {
        // Arrange - バックアップディレクトリを空にする
        foreach (var file in Directory.GetFiles(_backupDirectory))
        {
            File.Delete(file);
        }

        // Act
        var result = await _service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().NotBeNull();
        File.Exists(result).Should().BeTrue();
    }

    #endregion
}
