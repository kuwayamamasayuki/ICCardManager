using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
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
/// 呼び出し側（StartupTaskRunner）は戻り値をログにも UI にも出さないため、
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

    /// <summary>
    /// Issue #1924: 設定した保存先が使える場合、退避理由は立たない。
    /// </summary>
    [Fact]
    public async Task ResolveBackupFolderDetailAsync_設定した保存先が使えるとき退避しないこと()
    {
        var resolution = await _service.ResolveBackupFolderDetailAsync();

        resolution.EffectiveFolderPath.Should().Be(_backupDirectory);
        resolution.ConfiguredFolderPath.Should().Be(_backupDirectory);
        resolution.IsFallback.Should().BeFalse();
        resolution.FallbackReason.Should().BeNull();
    }

    /// <summary>
    /// Issue #1924: 設定した保存先が検証に失敗すると既定パスへ退避し、その理由を返す。
    /// </summary>
    /// <remarks>
    /// 修正前は <c>ResolveBackupFolderAsync</c> が退避後のパスしか返さず、
    /// 「設定した共有フォルダーではなくローカルへ書いている」ことが
    /// Warning ログ以外のどこにも現れなかった。
    /// </remarks>
    [Fact]
    public async Task ResolveBackupFolderDetailAsync_保存先が無効なとき既定へ退避し理由を返すこと()
    {
        // Arrange: 相対パス（検証で必ず弾かれる）を設定する
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = @"relative\backup" });

        // Act
        var resolution = await _service.ResolveBackupFolderDetailAsync();

        // Assert
        resolution.EffectiveFolderPath.Should().Be(PathValidator.GetDefaultBackupPath());
        resolution.ConfiguredFolderPath.Should().Be(@"relative\backup");
        resolution.IsFallback.Should().BeTrue();
        resolution.FallbackReason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Issue #1924: 保存先が未設定で既定を使うのは正常な運用なので、退避扱いにしない。
    /// </summary>
    [Fact]
    public async Task ResolveBackupFolderDetailAsync_保存先が未設定のとき退避扱いにしないこと()
    {
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = string.Empty });

        var resolution = await _service.ResolveBackupFolderDetailAsync();

        resolution.EffectiveFolderPath.Should().Be(PathValidator.GetDefaultBackupPath());
        resolution.IsFallback.Should().BeFalse();
    }

    #endregion
}
