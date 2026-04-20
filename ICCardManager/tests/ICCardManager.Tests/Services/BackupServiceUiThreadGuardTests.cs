using System;
using System.IO;
using System.Threading;
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
/// Issue #1361: <see cref="BackupService"/> を UI スレッドから呼び出しても
/// DbContext の UI スレッドガード (Issue #1281) に抵触しないことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 実際の WPF Dispatcher を立ち上げず、<c>DbContext.IsOnUiThread</c> 内部フックを
/// ManagedThreadId 判定に差し替え、「現スレッド = UI、Task.Run 先 = 非 UI」を模擬する。
/// これにより BackupService の async 版が確実に Task.Run でオフロードしていることを検証できる。
/// </para>
/// <para>
/// 自動バックアップ (<see cref="BackupService.ExecuteAutoBackupAsync"/>) は
/// <c>_settingsRepository.GetAppSettingsAsync()</c> のキャッシュヒット時に同期完了するため
/// <c>ConfigureAwait(false)</c> があっても UI スレッドに留まる。本テストは
/// <c>ReturnsAsync</c> によって同期完了する設定を与えることで、本番の "キャッシュヒット経路" を再現する。
/// </para>
/// </remarks>
public class BackupServiceUiThreadGuardTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _dbPath;
    private readonly string _backupDirectory;
    private readonly Func<bool> _originalIsOnUiThread;

    public BackupServiceUiThreadGuardTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"BackupServiceUiThreadGuardTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "backup_guard.db");
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);
        _originalIsOnUiThread = DbContext.IsOnUiThread;
    }

    public void Dispose()
    {
        DbContext.IsOnUiThread = _originalIsOnUiThread;
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    private Mock<ISettingsRepository> CreateSettingsRepositoryMock()
    {
        var mock = new Mock<ISettingsRepository>();
        // ReturnsAsync は完了済みタスクを返すため、await .ConfigureAwait(false) は
        // context switch せず呼び出し元スレッドに留まる。本番のキャッシュヒット経路と同じ挙動になる。
        mock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _backupDirectory });
        return mock;
    }

    /// <summary>
    /// UI スレッド模擬時に同期 <see cref="BackupService.CreateBackup"/> を呼ぶと、
    /// DbContext の UI スレッドガードが発火し、catch 節で握って <c>false</c> を返す。
    /// sync 版をテスト経路で残置する場合の期待動作を固定化する。
    /// </summary>
    [Fact]
    public void CreateBackup_sync_UIスレッド模擬時はガードが発火しfalseを返すこと()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase(); // 初期化はガード設定の前に実行する（セットアップ自体が UI ガードに抵触しないように）

        // 初期化完了後に UI スレッド模擬を有効化: 現スレッド = UI、Task.Run 先 = 非 UI
        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        DbContext.IsOnUiThread = () => Thread.CurrentThread.ManagedThreadId == uiThreadId;

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        var backupPath = Path.Combine(_backupDirectory, "ui_thread_sync.db");

        var result = service.CreateBackup(backupPath);

        result.Should().BeFalse(
            "sync 版 CreateBackup は UI スレッドから呼ぶと DbContext.LeaseConnection の "
            + "UI スレッドガード (Issue #1281) で InvalidOperationException が発生し、"
            + "BackupService はこれを catch して false を返すべき");
        File.Exists(backupPath).Should().BeFalse(
            "ガード発火時はバックアップファイルが生成されるべきでない");
    }

    /// <summary>
    /// UI スレッド模擬時でも <see cref="BackupService.CreateBackupAsync"/> は
    /// Task.Run でバックグラウンドにオフロードし、ガードに抵触せず成功すべき。
    /// </summary>
    [Fact]
    public async Task CreateBackupAsync_UIスレッド模擬時でも成功すること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();

        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        DbContext.IsOnUiThread = () => Thread.CurrentThread.ManagedThreadId == uiThreadId;

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        var backupPath = Path.Combine(_backupDirectory, "ui_thread_async.db");

        var result = await service.CreateBackupAsync(backupPath);

        result.Should().BeTrue(
            "CreateBackupAsync は Task.Run 経由でバックグラウンドに DB 接続リースをオフロードし、"
            + "UI スレッドガードに抵触しないべき (Issue #1361)");
        File.Exists(backupPath).Should().BeTrue(
            "成功時はバックアップファイルが実際に生成されるべき");
    }

    /// <summary>
    /// UI スレッド模擬時でも <see cref="BackupService.ExecuteAutoBackupAsync"/> は
    /// 内部で Task.Run を使用して UI スレッドガードに抵触せず完了する。
    /// 本番では <c>App.PerformStartupTasksAsync</c> から UI スレッド上で fire-and-forget 起動される経路を模擬。
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_UIスレッド模擬時でもバックアップファイルが生成されること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase(); // 初期化はガード設定の前に実行する（セットアップ自体が UI ガードに抵触しないように）

        // 初期化完了後に UI スレッド模擬を有効化: 現スレッド = UI、Task.Run 先 = 非 UI
        var uiThreadId = Thread.CurrentThread.ManagedThreadId;
        DbContext.IsOnUiThread = () => Thread.CurrentThread.ManagedThreadId == uiThreadId;

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        var backupFilePath = await service.ExecuteAutoBackupAsync();

        backupFilePath.Should().NotBeNull(
            "ExecuteAutoBackupAsync は Task.Run で DB 接続リースをオフロードし、"
            + "UI スレッドガードに抵触せず完了すべき (Issue #1361)");
        File.Exists(backupFilePath).Should().BeTrue(
            "成功時はバックアップファイルが実際に生成されるべき");
    }
}
