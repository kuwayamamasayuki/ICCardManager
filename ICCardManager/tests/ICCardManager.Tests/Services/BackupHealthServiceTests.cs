using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// BackupHealthService の単体テスト（Issue #1689）
/// </summary>
/// <remarks>
/// 起動時自動バックアップは結果が画面に出ないため、失敗が誰にも伝わらなかった。
/// 本サービスは「最終成功日時・世代数・空き容量」を集約して可視化する。
/// 個々の取得は独立して失敗し得る（フォルダ不達・権限不足）ため、
/// 1項目の失敗で全体が例外にならないことを重点的に固定する。
/// </remarks>
public class BackupHealthServiceTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly Mock<BackupService> _backupServiceMock;
    private readonly BackupHealthService _service;

    public BackupHealthServiceTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();

        _backupServiceMock = new Mock<BackupService>(
            _dbContext,
            _settingsRepositoryMock.Object,
            new Mock<ILogger<BackupService>>().Object);

        // 既定: 保存先は一時フォルダ（実在するので空き容量が取得できる）、バックアップファイルなし
        _backupServiceMock.Setup(s => s.ResolveBackupFolderAsync())
            .ReturnsAsync(Path.GetTempPath());
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ReturnsAsync(new List<BackupFileInfo>());
        _backupServiceMock.SetupGet(s => s.IsSharedMode).Returns(false);

        _service = new BackupHealthService(
            _backupServiceMock.Object,
            _settingsRepositoryMock.Object,
            NullLogger<BackupHealthService>.Instance);
    }

    public void Dispose() => _dbContext?.Dispose();

    private void SetupSetting(string key, string value) =>
        _settingsRepositoryMock.Setup(r => r.GetAsync(key)).ReturnsAsync(value);

    #region 最終成功日時

    [Fact]
    public async Task GetHealthAsync_WithRecordedSuccess_ReturnsParsedDateTimeAndMachineName()
    {
        SetupSetting(SettingsRepository.KeyLastBackupSuccessAt, "2026-07-20 08:30:15");
        SetupSetting(SettingsRepository.KeyLastBackupMachine, "PC-KEIRI-01");

        var health = await _service.GetHealthAsync();

        health.LastSuccessAt.Should().Be(new DateTime(2026, 7, 20, 8, 30, 15));
        health.LastSuccessMachineName.Should().Be("PC-KEIRI-01");
    }

    [Fact]
    public async Task GetHealthAsync_WithNoRecord_ReturnsNullLastSuccess()
    {
        // Issue #1689 導入前からの既存環境では初回起動時点で必ず記録がない。
        // null は「失敗」ではなく「判断材料なし」を意味する。
        SetupSetting(SettingsRepository.KeyLastBackupSuccessAt, null);

        var health = await _service.GetHealthAsync();

        health.LastSuccessAt.Should().BeNull();
    }

    [Fact]
    public async Task GetHealthAsync_WithUnparsableDateValue_ReturnsNullWithoutThrowing()
    {
        // settings は手動編集され得る key-value テーブル。壊れた値で画面が開けなくなってはいけない。
        SetupSetting(SettingsRepository.KeyLastBackupSuccessAt, "not-a-date");

        var health = await _service.GetHealthAsync();

        health.LastSuccessAt.Should().BeNull();
    }

    [Fact]
    public async Task GetHealthAsync_WhenSettingsThrows_ReturnsNullWithoutThrowing()
    {
        _settingsRepositoryMock
            .Setup(r => r.GetAsync(SettingsRepository.KeyLastBackupSuccessAt))
            .ThrowsAsync(new IOException("ネットワークが切断されました"));

        var health = await _service.GetHealthAsync();

        health.Should().NotBeNull("設定取得の失敗で健全性情報全体が落ちてはいけない");
        health.LastSuccessAt.Should().BeNull();
    }

    #endregion

    #region 世代数・上限

    [Fact]
    public async Task GetHealthAsync_ReturnsGenerationCountFromBackupFiles()
    {
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync()).ReturnsAsync(new List<BackupFileInfo>
        {
            new BackupFileInfo { FileName = "backup_20260720_080000.db" },
            new BackupFileInfo { FileName = "backup_20260721_080000.db" },
            new BackupFileInfo { FileName = "backup_20260722_080000.db" }
        });

        var health = await _service.GetHealthAsync();

        health.GenerationCount.Should().Be(3);
    }

    [Fact]
    public async Task GetHealthAsync_ReturnsMaxGenerationsFromAppConstants()
    {
        // 「◯/30 世代」表示と実際の削除しきい値が同じ定数から導かれることを固定する
        var health = await _service.GetHealthAsync();

        health.MaxGenerations.Should().Be(AppConstants.MaxBackupGenerations);
    }

    [Fact]
    public async Task GetHealthAsync_WhenBackupFolderUnreachable_ReturnsZeroGenerationsWithoutThrowing()
    {
        _backupServiceMock.Setup(s => s.GetBackupFilesAsync())
            .ThrowsAsync(new UnauthorizedAccessException("アクセスが拒否されました"));

        var health = await _service.GetHealthAsync();

        health.GenerationCount.Should().Be(0);
    }

    #endregion

    #region 保存先・空き容量

    [Fact]
    public async Task GetHealthAsync_ReturnsResolvedBackupFolderPath()
    {
        // 画面に出るフォルダと実際に書かれるフォルダの食い違いを防ぐため、
        // BackupService の解決結果をそのまま使うことを固定する
        _backupServiceMock.Setup(s => s.ResolveBackupFolderAsync())
            .ReturnsAsync(@"\\fileserver\iccard\backup");

        var health = await _service.GetHealthAsync();

        health.BackupFolderPath.Should().Be(@"\\fileserver\iccard\backup");
    }

    [Fact]
    public async Task GetHealthAsync_WithExistingFolder_ReturnsFreeSpace()
    {
        var health = await _service.GetHealthAsync();

        health.FreeSpaceBytes.Should().NotBeNull();
        health.FreeSpaceBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetHealthAsync_WithUnreachableFolder_ReturnsNullFreeSpace()
    {
        _backupServiceMock.Setup(s => s.ResolveBackupFolderAsync())
            .ReturnsAsync(Path.Combine(Path.GetTempPath(), $"NotExists_{Guid.NewGuid():N}"));

        var health = await _service.GetHealthAsync();

        health.FreeSpaceBytes.Should().BeNull("取得できない場合は 0 ではなく不明（null）として扱う");
    }

    #endregion

    #region 共有モード・VACUUM

    [Fact]
    public async Task GetHealthAsync_PropagatesSharedModeFlag()
    {
        _backupServiceMock.SetupGet(s => s.IsSharedMode).Returns(true);

        var health = await _service.GetHealthAsync();

        health.IsSharedMode.Should().BeTrue();
    }

    [Fact]
    public async Task GetHealthAsync_ReturnsLastVacuumDateAndMachineName()
    {
        SetupSetting(SettingsRepository.KeyLastVacuumDate, "2026-07-10");
        SetupSetting(SettingsRepository.KeyLastVacuumMachine, "PC-KEIRI-03");

        var health = await _service.GetHealthAsync();

        health.LastVacuumDate.Should().Be(new DateTime(2026, 7, 10));
        health.LastVacuumMachineName.Should().Be("PC-KEIRI-03");
    }

    [Fact]
    public async Task RecordVacuumMachineAsync_SavesCurrentMachineName()
    {
        await _service.RecordVacuumMachineAsync();

        _settingsRepositoryMock.Verify(
            r => r.SetAsync(SettingsRepository.KeyLastVacuumMachine, Environment.MachineName),
            Times.Once);
    }

    [Fact]
    public async Task RecordVacuumMachineAsync_WhenSaveFails_DoesNotThrow()
    {
        // 記録は監視用の補助情報。失敗しても VACUUM の完了を取り消さない。
        _settingsRepositoryMock
            .Setup(r => r.SetAsync(SettingsRepository.KeyLastVacuumMachine, It.IsAny<string>()))
            .ThrowsAsync(new IOException("書き込みできません"));

        Func<Task> act = async () => await _service.RecordVacuumMachineAsync();

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region 経過日数の計算（BackupHealthInfo）

    [Theory]
    [InlineData("2026-07-25", "2026-07-25", 0)]   // 同日
    [InlineData("2026-07-24", "2026-07-25", 1)]   // 前日
    [InlineData("2026-07-13", "2026-07-25", 12)]
    [InlineData("2026-07-26", "2026-07-25", 0)]   // 時計のずれ等で未来日付になっても負値にしない
    public void GetDaysSinceLastSuccess_CountsByDate(string lastSuccess, string now, int expected)
    {
        var info = new ICCardManager.Dtos.BackupHealthInfo
        {
            // 時刻成分があっても日付単位で数えることを併せて確認する
            LastSuccessAt = DateTime.Parse(lastSuccess).AddHours(23).AddMinutes(50)
        };

        info.GetDaysSinceLastSuccess(DateTime.Parse(now)).Should().Be(expected);
    }

    [Fact]
    public void GetDaysSinceLastSuccess_WithNoRecord_ReturnsNull()
    {
        var info = new ICCardManager.Dtos.BackupHealthInfo { LastSuccessAt = null };

        info.GetDaysSinceLastSuccess(new DateTime(2026, 7, 25)).Should().BeNull();
    }

    #endregion
}
