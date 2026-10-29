using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// 手動バックアップの成功記録の単体テスト（Issue #2304）
/// </summary>
/// <remarks>
/// <para>
/// バックアップの警告（BackupStale）は「システム管理画面（F6）で…手動バックアップを実行してください」と案内するが、
/// 修正前は手動バックアップが <c>last_backup_success_at</c> を書かず、案内どおりに実行しても警告が消えなかった。
/// 既存の <c>MainViewModelTests.CheckBackupHealthAsync_手動バックアップで解消したら警告を取り除くこと</c> は
/// <see cref="IBackupHealthService.GetHealthAsync"/> のモックで「解消した状態」を作っていたため、記録が無いことを検出できなかった。
/// </para>
/// <para>
/// ここでは本物の <see cref="SettingsRepository"/>・<see cref="BackupHealthService"/>・<see cref="WarningService"/> を使い、
/// 実際に DB へ記録された値を読み返して表明する。
/// </para>
/// </remarks>
public class BackupServiceManualBackupRecordingTests : IDisposable
{
    /// <summary>テストの「現在」。警告の判定とバックアップの記録の両方がこの時刻を使う。</summary>
    private static readonly DateTime Now = new(2026, 10, 20, 9, 30, 0);

    /// <summary>しきい値（7 日）を超えて成功していない状態の、前回の成功日時。</summary>
    private static readonly DateTime StaleSuccessAt = Now.AddDays(-(AppConstants.BackupStaleWarningDays + 3));

    private readonly string _testDirectory;
    private readonly string _backupDirectory;
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheService;
    private readonly SettingsRepository _settingsRepository;
    private readonly BackupService _service;
    private readonly WarningService _warningService;

    public BackupServiceManualBackupRecordingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ManualBackupRecordingTests_{Guid.NewGuid():N}");
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);

        _dbContext = new DbContext(Path.Combine(_testDirectory, "test.db"));
        _dbContext.InitializeDatabase();

        _cacheService = new CacheService(NullLogger<CacheService>.Instance);
        _settingsRepository = new SettingsRepository(_dbContext, _cacheService, Options.Create(new CacheOptions()));

        _service = new BackupService(
            _dbContext,
            _settingsRepository,
            NullLogger<BackupService>.Instance,
            new FixedSystemClock(Now));

        var healthService = new BackupHealthService(
            _service,
            _settingsRepository,
            NullLogger<BackupHealthService>.Instance);
        _warningService = new WarningService(
            new Mock<ILedgerRepository>().Object,
            new Mock<IDatabaseInfo>().Object,
            backupHealthService: healthService);
    }

    public void Dispose()
    {
        _cacheService.Dispose();
        _dbContext.Dispose();
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

    /// <summary>
    /// 前回の成功が古く、バックアップの警告が出ている状態を作る
    /// </summary>
    private async Task ArrangeStaleBackupAsync()
    {
        await _settingsRepository.SetAsync(SettingsRepository.KeyBackupPath, _backupDirectory);
        await _settingsRepository.SetAsync(
            SettingsRepository.KeyLastBackupSuccessAt, SqliteDateTimeFormat.ToText(StaleSuccessAt));
        await _settingsRepository.SetAsync(SettingsRepository.KeyLastBackupMachine, "OTHER-PC");

        // 前提: 警告が出ている（ここが崩れると「消えた」の表明が空振りする）
        (await _warningService.CheckBackupHealthWarningAsync(Now)).Should().NotBeNull(
            "前回の成功がしきい値を超えて古いので、バックアップの警告が出ているはず");
    }

    [Fact]
    public async Task CreateManualBackupAsync_成功すると成功日時とPC名が記録されバックアップの警告が消えること()
    {
        await ArrangeStaleBackupAsync();
        var backupFilePath = Path.Combine(_backupDirectory, "backup_manual_20261020_093000.db");

        var result = await _service.CreateManualBackupAsync(backupFilePath);

        result.Should().BeTrue();
        File.Exists(backupFilePath).Should().BeTrue();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyLastBackupSuccessAt))
            .Should().Be(SqliteDateTimeFormat.ToText(Now), "手動バックアップの成功日時が記録されるべき");
        (await _settingsRepository.GetAsync(SettingsRepository.KeyLastBackupMachine))
            .Should().Be(Environment.MachineName, "手動バックアップを実施した PC 名が記録されるべき");
        (await _warningService.CheckBackupHealthWarningAsync(Now)).Should().BeNull(
            "案内どおりに手動バックアップを成功させたので、F6 を閉じたときの再判定で警告は消えるべき");
    }

    [Fact]
    public async Task CreateManualBackupAsync_自動バックアップと別の保存先でも成功として記録されること()
    {
        // 警告の原因が自動バックアップの保存先の障害であるとき、案内どおりに実行できるのは別の保存先だけ
        await ArrangeStaleBackupAsync();
        var otherFolder = Path.Combine(_testDirectory, "usb");
        var backupFilePath = Path.Combine(otherFolder, "backup_manual_20261020_093000.db");

        var result = await _service.CreateManualBackupAsync(backupFilePath);

        result.Should().BeTrue();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyLastBackupSuccessAt))
            .Should().Be(SqliteDateTimeFormat.ToText(Now));
        (await _warningService.CheckBackupHealthWarningAsync(Now)).Should().BeNull();
    }

    [Fact]
    public async Task CreateManualBackupAsync_失敗したら記録せずバックアップの警告も残ること()
    {
        await ArrangeStaleBackupAsync();

        // 相対パスの保存先は検証で必ず弾かれ、バックアップは作られない
        var result = await _service.CreateManualBackupAsync(Path.Combine("relative", "backup_manual.db"));

        result.Should().BeFalse();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyLastBackupSuccessAt))
            .Should().Be(SqliteDateTimeFormat.ToText(StaleSuccessAt), "失敗したバックアップを成功として記録してはならない");
        (await _settingsRepository.GetAsync(SettingsRepository.KeyLastBackupMachine))
            .Should().Be("OTHER-PC");
        (await _warningService.CheckBackupHealthWarningAsync(Now)).Should().NotBeNull(
            "バックアップは成功していないので警告は残るべき");
    }

    [Fact]
    public async Task CreateBackupAsync_リストア前バックアップの経路は成功しても記録しないこと()
    {
        // リストア前バックアップは CreateBackupAsync を使う。リストアが成功すると DB は置き換わり、記録も消えるため数えない
        await ArrangeStaleBackupAsync();
        var backupFilePath = Path.Combine(_backupDirectory, "backup_pre_restore_20261020_093000.db");

        var result = await _service.CreateBackupAsync(backupFilePath);

        result.Should().BeTrue();
        File.Exists(backupFilePath).Should().BeTrue();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyLastBackupSuccessAt))
            .Should().Be(SqliteDateTimeFormat.ToText(StaleSuccessAt));
    }

    [Fact]
    public async Task CreateManualBackupAsync_記録に失敗してもバックアップの成功は取り消さないこと()
    {
        // 記録は監視用の補助情報（自動バックアップの RecordBackupSuccessAsync と同じ扱い）
        var settingsMock = new Mock<ISettingsRepository>();
        settingsMock.Setup(r => r.SetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("設定を保存できません"));
        var service = new BackupService(
            _dbContext, settingsMock.Object, NullLogger<BackupService>.Instance, new FixedSystemClock(Now));
        var backupFilePath = Path.Combine(_backupDirectory, "backup_manual_20261020_093000.db");

        var result = await service.CreateManualBackupAsync(backupFilePath);

        result.Should().BeTrue("記録の失敗でバックアップが失敗扱いになってはいけない");
        File.Exists(backupFilePath).Should().BeTrue();
        settingsMock.Verify(
            r => r.SetAsync(SettingsRepository.KeyLastBackupSuccessAt, SqliteDateTimeFormat.ToText(Now)),
            Times.Once);
    }
}
