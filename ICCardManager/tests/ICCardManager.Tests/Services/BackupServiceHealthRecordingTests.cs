using System;
using System.IO;
using System.Threading.Tasks;
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
/// BackupService のバックアップ成功記録の単体テスト（Issue #1689）
/// </summary>
/// <remarks>
/// 呼び出し側（App.PerformStartupTasksAsync）は戻り値を捨てる fire-and-forget のため、
/// 「最後に成功したのはいつか」をサービス内部で永続化しないと誰も知り得ない。
/// ここでは記録が確実に行われること、および記録の失敗がバックアップ本体の成功を
/// 取り消さないことを固定する。
/// </remarks>
public class BackupServiceHealthRecordingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _backupDirectory;
    private readonly DbContext _dbContext;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly BackupService _service;

    public BackupServiceHealthRecordingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"BackupHealthRecordingTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);

        _dbContext = new DbContext(Path.Combine(_testDirectory, "test.db"));
        _dbContext.InitializeDatabase();

        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _backupDirectory });

        _service = new BackupService(
            _dbContext,
            _settingsRepositoryMock.Object,
            NullLogger<BackupService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch
        {
            // クリーンアップ失敗は無視
        }
    }

    #region 成功時の記録

    [Fact]
    public async Task ExecuteAutoBackupAsync_OnSuccess_RecordsLastSuccessTimestamp()
    {
        string recorded = null;
        _settingsRepositoryMock
            .Setup(r => r.SetAsync(SettingsRepository.KeyLastBackupSuccessAt, It.IsAny<string>()))
            .Callback<string, string>((_, value) => recorded = value)
            .ReturnsAsync(true);

        var before = DateTime.Now.AddSeconds(-1);
        var result = await _service.ExecuteAutoBackupAsync();

        result.Should().NotBeNull();
        _settingsRepositoryMock.Verify(
            r => r.SetAsync(SettingsRepository.KeyLastBackupSuccessAt, It.IsAny<string>()),
            Times.Once);

        // 記録された値が「今」の日時として解釈できることを確認する
        DateTime.TryParse(recorded, out var parsed).Should().BeTrue("ISO 8601 形式で保存されること");
        parsed.Should().BeOnOrAfter(before);
        parsed.Should().BeOnOrBefore(DateTime.Now.AddSeconds(1));
    }

    [Fact]
    public async Task ExecuteAutoBackupAsync_OnSuccess_RecordsMachineName()
    {
        // 共有モードでどのPCが実施したかを追跡するための記録
        await _service.ExecuteAutoBackupAsync();

        _settingsRepositoryMock.Verify(
            r => r.SetAsync(SettingsRepository.KeyLastBackupMachine, Environment.MachineName),
            Times.Once);
    }

    #endregion

    #region 失敗時の挙動

    [Fact]
    public async Task ExecuteAutoBackupAsync_WhenRecordingFails_StillReturnsBackupPath()
    {
        // 記録は監視用の補助情報。書き込めなくてもバックアップ自体の成功を取り消さない。
        _settingsRepositoryMock
            .Setup(r => r.SetAsync(SettingsRepository.KeyLastBackupSuccessAt, It.IsAny<string>()))
            .ThrowsAsync(new IOException("設定を保存できません"));

        var result = await _service.ExecuteAutoBackupAsync();

        result.Should().NotBeNull("記録の失敗でバックアップが失敗扱いになってはいけない");
        File.Exists(result).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAutoBackupAsync_WhenBackupFails_DoesNotRecordSuccess()
    {
        // バックアップ先の解決に失敗させるため、設定取得を例外にする
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ThrowsAsync(new IOException("設定を読み取れません"));

        var result = await _service.ExecuteAutoBackupAsync();

        result.Should().BeNull();
        _settingsRepositoryMock.Verify(
            r => r.SetAsync(SettingsRepository.KeyLastBackupSuccessAt, It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region 保存先の解決

    [Fact]
    public async Task ResolveBackupFolderAsync_WithConfiguredPath_ReturnsNormalizedConfiguredPath()
    {
        // 画面に出るフォルダと実際に書かれるフォルダを一致させるための共通経路
        var folder = await _service.ResolveBackupFolderAsync();

        folder.Should().Be(_backupDirectory);
    }

    [Fact]
    public async Task ResolveBackupFolderAsync_MatchesFolderActuallyUsedByAutoBackup()
    {
        var folder = await _service.ResolveBackupFolderAsync();
        var createdFile = await _service.ExecuteAutoBackupAsync();

        Path.GetDirectoryName(createdFile).Should().Be(folder);
    }

    #endregion
}
