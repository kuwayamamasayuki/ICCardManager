using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// システム管理画面（F6）の手動バックアップから、「最終成功」の表示とバックアップの警告の再判定までを
/// 実サービスでつないだ結合テスト（Issue #2304）
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SystemManageViewModel"/> を実 <see cref="BackupService"/>・実 <see cref="BackupHealthService"/>・
/// 実 DB の <see cref="SettingsRepository"/> で組む。手動確認「F6 の最終成功が本日になり、閉じると警告が消える」の代替。
/// </para>
/// <para>
/// F6 を閉じたときの再判定は <c>MainViewModel.CheckBackupHealthAsync</c> が <see cref="WarningService.CheckBackupHealthWarningAsync"/>
/// の結果で既存の警告を除去して作り直す（その除去は UT-WARN-KEEP No.10 が固定済み）。ここでは同じ
/// <see cref="WarningService"/> が手動バックアップの後に警告を返さないことまでを、同じ DB の上で表明する。
/// </para>
/// </remarks>
public class SystemManageViewModelManualBackupHealthTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _backupDirectory;
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheService;
    private readonly SettingsRepository _settingsRepository;
    private readonly SystemManageViewModel _viewModel;
    private readonly WarningService _warningService;

    public SystemManageViewModelManualBackupHealthTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ManualBackupHealthVmTests_{Guid.NewGuid():N}");
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);

        _dbContext = new DbContext(Path.Combine(_testDirectory, "test.db"));
        _dbContext.InitializeDatabase();

        _cacheService = new CacheService(NullLogger<CacheService>.Instance);
        _settingsRepository = new SettingsRepository(_dbContext, _cacheService, Options.Create(new CacheOptions()));

        // 表示（LastBackupSuccessText）は DateTime.Now で経過日数を数えるため、記録側もシステム時計を使う
        var backupService = new BackupService(_dbContext, _settingsRepository, NullLogger<BackupService>.Instance);
        var healthService = new BackupHealthService(
            backupService, _settingsRepository, NullLogger<BackupHealthService>.Instance);

        var databaseInfo = new Mock<IDatabaseInfo>();
        databaseInfo.SetupGet(d => d.DatabasePath).Returns(Path.Combine(_testDirectory, "test.db"));
        var safeFileLauncher = new Mock<ISafeFileLauncher>();
        safeFileLauncher.Setup(l => l.LaunchFolder(It.IsAny<string>())).Returns(SafeFileLaunchResult.Ok());

        _viewModel = new SystemManageViewModel(
            backupService,
            _settingsRepository,
            new Mock<INavigationService>().Object,
            new OperationLogger(new OperationLogRepository(_dbContext), new CurrentOperatorContext(new SystemClock())),
            safeFileLauncher.Object,
            databaseInfo.Object,
            new Mock<IStaffAuthService>().Object,
            healthService,
            new Mock<IDialogService>().Object);

        _warningService = new WarningService(
            new Mock<ILedgerRepository>().Object,
            databaseInfo.Object,
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
    /// 最終成功が 30 日前で、F6 に警告表示・メイン画面にバックアップの警告が出ている状態を作る
    /// </summary>
    private async Task ArrangeStaleBackupAsync()
    {
        await _settingsRepository.SetAsync(SettingsRepository.KeyBackupPath, _backupDirectory);
        await _settingsRepository.SetAsync(
            SettingsRepository.KeyLastBackupSuccessAt, SqliteDateTimeFormat.ToText(DateTime.Now.AddDays(-30)));

        await _viewModel.LoadBackupHealthAsync();

        // 前提: ここが崩れると「本日になる」「警告が消える」の表明が空振りする
        _viewModel.IsBackupStale.Should().BeTrue();
        _viewModel.LastBackupSuccessText.Should().Contain("30日前");
        (await _warningService.CheckBackupHealthWarningAsync(DateTime.Now)).Should().NotBeNull();
    }

    [Fact]
    public async Task CreateBackupCoreAsync_手動バックアップが成功すると最終成功が本日になりバックアップの警告が消えること()
    {
        await ArrangeStaleBackupAsync();
        var backupFilePath = Path.Combine(_backupDirectory, "backup_manual_20261020_093000.db");

        await _viewModel.CreateBackupCoreAsync(backupFilePath);

        _viewModel.StatusMessage.Should().Be("バックアップを作成しました: backup_manual_20261020_093000.db");
        _viewModel.LastBackupSuccessText.Should().Contain("（本日）", "F6 の「最終成功」は手動バックアップの直後に更新されるべき");
        _viewModel.IsBackupStale.Should().BeFalse();
        (await _warningService.CheckBackupHealthWarningAsync(DateTime.Now)).Should().BeNull(
            "F6 を閉じたときの再判定で、バックアップの警告は消えるべき");
    }

    [Fact]
    public async Task CreateBackupCoreAsync_手動バックアップが失敗したら最終成功は変わらず警告も残ること()
    {
        await ArrangeStaleBackupAsync();

        // 相対パスの保存先は検証で必ず弾かれ、バックアップは作られない
        await _viewModel.CreateBackupCoreAsync(Path.Combine("relative", "backup_manual.db"));

        _viewModel.IsStatusError.Should().BeTrue();
        await _viewModel.LoadBackupHealthAsync();
        _viewModel.LastBackupSuccessText.Should().Contain("30日前");
        _viewModel.IsBackupStale.Should().BeTrue();
        (await _warningService.CheckBackupHealthWarningAsync(DateTime.Now)).Should().NotBeNull(
            "バックアップは成功していないので警告は残るべき");
    }
}
