using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2264: バックアップ先・データベース保存先の変更が、操作ログ（変更前後の値）とまとめて確定することを
/// 実 DB（インメモリ SQLite）の行と一時フォルダーの設定ファイルで検証する。
/// </summary>
/// <remarks>
/// 「欠陥を突く側」（記録の無い変更が残らない・変更前は DB から読む）と
/// 「正当な挙動を塞いでいない側」（変わらないなら何も書かない・記録しない）を対で置く。
/// </remarks>
public sealed class SettingsChangeServiceTests : IDisposable
{
    private const string OperatorIdm = "FFFF000000000009";
    private const string OperatorName = "庶務 担当";
    private static readonly DateTime ChangedAt = new(2026, 10, 13, 14, 5, 0);

    private readonly DbContext _dbContext;
    private readonly SettingsRepository _settingsRepository;
    private readonly OperationLogRepository _realOperationLogRepository;
    private readonly string _configDirectory;
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();

    public SettingsChangeServiceTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _settingsRepository = new SettingsRepository(
            _dbContext, new Mock<ICacheService>().Object, Options.Create(new CacheOptions()));
        _realOperationLogRepository = new OperationLogRepository(_dbContext);
        _configDirectory = Path.Combine(Path.GetTempPath(), "ICCardManagerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_configDirectory);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch
        {
            // 後片付けの失敗はテスト結果に影響させない（一時フォルダー配下）
        }
        GC.SuppressFinalize(this);
    }

    private string ConfigPath => Path.Combine(_configDirectory, "database_config.txt");

    #region バックアップ先

    [Fact]
    public async Task ChangeBackupPathAsync_値と変更日時とPC名を書き_変更前後を操作ログに記録すること()
    {
        await _settingsRepository.SetAsync(SettingsRepository.KeyBackupPath, @"C:\OldBackup");
        var service = CreateService(_realOperationLogRepository);

        var changed = await service.ChangeBackupPathAsync(@"\\fileserver\backup");

        changed.Should().BeTrue();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(@"\\fileserver\backup");
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPathChangedAt)).Should().Be("2026-10-13 14:05:00");
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPathChangedMachine)).Should().Be(Environment.MachineName);

        var log = (await GetSettingLogsAsync(OperationLogger.SettingNames.BackupPath)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.OperatorIdm.Should().Be(OperatorIdm);
        log.OperatorName.Should().Be(OperatorName);
        ReadValue(log.BeforeData).Should().Be(@"C:\OldBackup");
        ReadValue(log.AfterData).Should().Be(@"\\fileserver\backup");
        ReadProperty(log.AfterData, "MachineName").Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task ChangeBackupPathAsync_未設定からの変更は変更前を既定のバックアップ先として記録すること()
    {
        // backup_path が未設定（既定の場所を使っている）
        var service = CreateService(_realOperationLogRepository);

        await service.ChangeBackupPathAsync(@"\\fileserver\backup");

        var log = (await GetSettingLogsAsync(OperationLogger.SettingNames.BackupPath)).Single();
        ReadValue(log.BeforeData).Should().Be(SettingsChangeService.DescribeBackupPath(string.Empty),
            "空のまま記録すると「値: →\\\\fileserver\\backup」と変更前が抜けて読める");
        ReadValue(log.BeforeData).Should().StartWith("既定のバックアップ先（");
        ReadValue(log.AfterData).Should().Be(@"\\fileserver\backup", "設定した値はそのまま記録する");
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(@"\\fileserver\backup");
    }

    [Fact]
    public async Task ChangeBackupPathAsync_変更前は画面が読んだ値ではなくDBの現在値を記録すること()
    {
        // 他 PC が先に変更している（画面はそれより前の値を読み込んでいた）
        await _settingsRepository.SetAsync(SettingsRepository.KeyBackupPath, @"\\other-pc-set\backup");
        var service = CreateService(_realOperationLogRepository);

        await service.ChangeBackupPathAsync(@"D:\Backup");

        var log = (await GetSettingLogsAsync(OperationLogger.SettingNames.BackupPath)).Single();
        ReadValue(log.BeforeData).Should().Be(@"\\other-pc-set\backup");
    }

    [Fact]
    public async Task ChangeBackupPathAsync_操作ログの記録に失敗したら_バックアップ先も変更日時も書かないこと()
    {
        await _settingsRepository.SetAsync(SettingsRepository.KeyBackupPath, @"C:\OldBackup");
        var service = CreateService(FailingOperationLogRepository());

        var act = () => service.ChangeBackupPathAsync(@"\\attacker\share");

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(@"C:\OldBackup",
            "記録の無い変更を残さない（変更と操作ログは同じトランザクション）");
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPathChangedAt)).Should().BeNull();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPathChangedMachine)).Should().BeNull();
    }

    [Fact]
    public async Task ChangeBackupPathAsync_DBの現在値と同じなら何も書かず記録もしないこと()
    {
        await _settingsRepository.SetAsync(SettingsRepository.KeyBackupPath, @"D:\Backup");
        var service = CreateService(_realOperationLogRepository);

        var changed = await service.ChangeBackupPathAsync(@"D:\Backup");

        changed.Should().BeFalse();
        (await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPathChangedAt)).Should().BeNull(
            "起きていない変更の日時を記録しない");
        (await GetSettingLogsAsync(OperationLogger.SettingNames.BackupPath)).Should().BeEmpty();
    }

    #endregion

    #region データベース保存先

    [Fact]
    public async Task ChangeDatabaseConfigAsync_設定ファイルを書き_変更前後を操作ログに記録すること()
    {
        File.WriteAllText(ConfigPath, @"C:\old\iccard.db");
        var service = CreateService(_realOperationLogRepository);

        var changed = await service.ChangeDatabaseConfigAsync(ConfigPath, @"\\fileserver\iccard\iccard.db");

        changed.Should().BeTrue();
        File.ReadAllText(ConfigPath).Should().Be(@"\\fileserver\iccard\iccard.db");
        var log = (await GetSettingLogsAsync(OperationLogger.SettingNames.DatabasePath)).Single();
        log.OperatorIdm.Should().Be(OperatorIdm);
        ReadValue(log.BeforeData).Should().Be(@"C:\old\iccard.db");
        ReadValue(log.AfterData).Should().Be(@"\\fileserver\iccard\iccard.db");
        ReadProperty(log.AfterData, "MachineName").Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task ChangeDatabaseConfigAsync_デフォルトに戻すと設定ファイルを削除し_既定の保存先として記録すること()
    {
        File.WriteAllText(ConfigPath, @"\\fileserver\iccard\iccard.db");
        var service = CreateService(_realOperationLogRepository);

        var changed = await service.ChangeDatabaseConfigAsync(ConfigPath, databaseFilePath: null);

        changed.Should().BeTrue();
        File.Exists(ConfigPath).Should().BeFalse();
        var log = (await GetSettingLogsAsync(OperationLogger.SettingNames.DatabasePath)).Single();
        ReadValue(log.BeforeData).Should().Be(@"\\fileserver\iccard\iccard.db");
        ReadValue(log.AfterData).Should().Be(SettingsChangeService.DescribeDatabaseConfig(string.Empty));
        ReadValue(log.AfterData).Should().StartWith("既定の保存先（");
    }

    [Fact]
    public async Task ChangeDatabaseConfigAsync_操作ログの記録に失敗したら設定ファイルを元に戻すこと()
    {
        File.WriteAllText(ConfigPath, @"C:\old\iccard.db");
        var service = CreateService(FailingOperationLogRepository());

        var act = () => service.ChangeDatabaseConfigAsync(ConfigPath, @"\\attacker\share\iccard.db");

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(ConfigPath).Should().Be(@"C:\old\iccard.db",
            "操作ログに残らない保存先の変更を、再起動後に効かせない");
    }

    [Fact]
    public async Task ChangeDatabaseConfigAsync_設定ファイルが無かったときに記録に失敗したら作った設定ファイルを消すこと()
    {
        var service = CreateService(FailingOperationLogRepository());

        var act = () => service.ChangeDatabaseConfigAsync(ConfigPath, @"\\attacker\share\iccard.db");

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(ConfigPath).Should().BeFalse();
    }

    [Fact]
    public async Task ChangeDatabaseConfigAsync_同じ内容なら何も書かず記録もしないこと()
    {
        File.WriteAllText(ConfigPath, @"\\fileserver\iccard\iccard.db");
        var service = CreateService(_realOperationLogRepository);

        var changed = await service.ChangeDatabaseConfigAsync(ConfigPath, @"\\fileserver\iccard\iccard.db");
        var resetWithoutFile = await service.ChangeDatabaseConfigAsync(
            Path.Combine(_configDirectory, "missing.txt"), databaseFilePath: null);

        changed.Should().BeFalse();
        resetWithoutFile.Should().BeFalse("設定ファイルが無ければ既にデフォルト");
        (await GetSettingLogsAsync(OperationLogger.SettingNames.DatabasePath)).Should().BeEmpty();
    }

    #endregion

    #region 職員の登録状況（認証の免除）

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RequiresOperatorAuthenticationAsync_職員が居るときだけ認証を求めること(bool hasActiveStaff, bool expected)
    {
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(hasActiveStaff);
        var service = CreateService(_realOperationLogRepository);

        (await service.RequiresOperatorAuthenticationAsync()).Should().Be(expected,
            "職員が 0 人なら誰も認証できないので免除する（#2263 と同じ判定）");
    }

    #endregion

    #region ヘルパー

    private SettingsChangeService CreateService(IOperationLogRepository operationLogRepository)
    {
        var operatorContext = new Mock<ICurrentOperatorContext>();
        string? operatorIdmOut = OperatorIdm;
        string? operatorNameOut = OperatorName;
        operatorContext.Setup(c => c.TryGetOperator(out operatorIdmOut, out operatorNameOut)).Returns(true);

        return new SettingsChangeService(
            _dbContext,
            _settingsRepository,
            new OperationLogger(operationLogRepository, operatorContext.Object),
            new StaffManagementService(
                _dbContext, _staffRepositoryMock.Object,
                new OperationLogger(operationLogRepository, operatorContext.Object),
                NullLogger<StaffManagementService>.Instance),
            new FixedSystemClock(ChangedAt),
            NullLogger<SettingsChangeService>.Instance);
    }

    private IOperationLogRepository FailingOperationLogRepository()
    {
        var mock = new Mock<IOperationLogRepository>();
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("operation_log への書き込みに失敗（テスト）"));
        return mock.Object;
    }

    private async Task<List<OperationLog>> GetSettingLogsAsync(string settingName)
        => (await _realOperationLogRepository.GetByTargetAsync(OperationLogger.Tables.Settings, settingName)).ToList();

    private static string? ReadValue(string? json) => ReadProperty(json, "Value");

    private static string? ReadProperty(string? json, string name)
    {
        json.Should().NotBeNull();
        using var doc = JsonDocument.Parse(json!);
        return doc.RootElement.GetProperty(name).GetString();
    }

    #endregion
}
