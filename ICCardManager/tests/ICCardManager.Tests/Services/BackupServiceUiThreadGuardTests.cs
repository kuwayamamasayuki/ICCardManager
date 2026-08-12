using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
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
/// <para>
/// Issue #1372: 同一フック (<c>DbContext.IsOnUiThread</c>) を書き換える他テストクラスとの
/// 並列実行レースを避けるため、<see cref="DbContextUiThreadHookCollection"/> に属させシリアル実行させる。
/// </para>
/// <para>
/// Issue #1746: `ResolveBackupFolderAsync` / `GetBackupFilesAsync` の UNC 検証オフロードの
/// 検証では <c>PathValidator.UncReachabilityChecker</c> フックも差し替える。差し替えは
/// テスト固有マーカーを含むパスのみ介入し、他の並列テストへ影響しない形にする。
/// </para>
/// </remarks>
[Collection(DbContextUiThreadHookCollection.Name)]
public class BackupServiceUiThreadGuardTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _dbPath;
    private readonly string _backupDirectory;
    private readonly Func<bool> _originalIsOnUiThread;
    private readonly Func<string, int, bool> _originalUncReachabilityChecker;

    public BackupServiceUiThreadGuardTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"BackupServiceUiThreadGuardTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "backup_guard.db");
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);
        _originalIsOnUiThread = DbContext.IsOnUiThread;
        _originalUncReachabilityChecker = PathValidator.UncReachabilityChecker;
    }

    public void Dispose()
    {
        DbContext.IsOnUiThread = _originalIsOnUiThread;
        PathValidator.UncReachabilityChecker = _originalUncReachabilityChecker;
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
    /// 本番では <c>StartupTaskRunner</c> から UI スレッド上で起動される経路を模擬。
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

    /// <summary>
    /// Issue #1746: UNC パス設定時の <see cref="BackupService.ResolveBackupFolderAsync"/> は、
    /// UNC 到達性チェック（最大5秒）の完了を待たずに制御を返すこと。
    /// 同期版 <c>PathValidator.ValidateBackupPath</c> を呼ぶ実装では、設定キャッシュヒット時
    /// （本番の通常経路）に呼び出しスレッド＝UI スレッド上で検証が走り、起動直後の画面が固まる。
    /// </summary>
    /// <remarks>
    /// スレッド ID の比較は「await で解放されたスレッドをプールが Task.Run に再利用する」
    /// 可能性があり理論上不安定なため、「チェック完了前に Task が未完了のまま返る」ことで
    /// 非ブロックを表明する。修正前の同期実装では呼び出し自体がブロックし、
    /// 返ってきた時点で完了済みになるため確実に赤になる。
    /// </remarks>
    [Fact]
    public async Task ResolveBackupFolderAsync_UNC到達性チェックの完了前に制御を返すこと()
    {
        // テスト固有マーカー: 並列実行中の他テストが公開 API へ渡すパスには介入しない
        var uncMarker = $"issue1746-{Guid.NewGuid():N}";
        var uncPath = $@"\\{uncMarker}\share\backup";

        var settingsMock = new Mock<ISettingsRepository>();
        settingsMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = uncPath });

        using var checkerStarted = new ManualResetEventSlim(false);
        using var releaseChecker = new ManualResetEventSlim(false);
        try
        {
            PathValidator.UncReachabilityChecker = (path, timeoutMs) =>
            {
                if (!path.Contains(uncMarker))
                {
                    return PathValidator.DefaultUncReachabilityChecker(path, timeoutMs);
                }
                checkerStarted.Set();
                // 修正前の同期実装がテストを無限に固めないよう、本番の到達性タイムアウトと同じ上限を添える
                releaseChecker.Wait(PathValidator.DefaultUncTimeoutMs);
                return false; // 到達不可 → 既定パスへのフォールバック（既存挙動）を誘発
            };

            using var dbContext = new DbContext(_dbPath);
            var service = new BackupService(
                dbContext,
                settingsMock.Object,
                NullLogger<BackupService>.Instance);

            var task = service.ResolveBackupFolderAsync();

            checkerStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
                "UNC パス設定時は到達性チェックが呼ばれるべき（呼ばれない場合はテストの前提が崩れている）");
            task.IsCompleted.Should().BeFalse(
                "UNC 到達性チェックの完了前に制御が返るべき。同期版 ValidateBackupPath を呼ぶ実装では"
                + "呼び出し自体がブロックし、起動時経路では UI スレッドが最大5秒固まる (Issue #1746)");

            releaseChecker.Set();
            var folder = await task;
            folder.Should().Be(
                PathValidator.NormalizePath(PathValidator.GetDefaultBackupPath()),
                "到達不可の UNC パスは既定パスへフォールバックする既存挙動が保たれるべき");
        }
        finally
        {
            PathValidator.UncReachabilityChecker = _originalUncReachabilityChecker;
        }
    }

    /// <summary>
    /// Issue #1746: <see cref="BackupService.GetBackupFilesAsync"/>（リストア画面から
    /// UI スレッドで呼ばれる）も <see cref="BackupService.ResolveBackupFolderAsync"/> と
    /// 同型の「キャッシュヒット → 同期 UNC 検証」を持っていたため、同じ非ブロック性を表明する。
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_UNC到達性チェックの完了前に制御を返すこと()
    {
        var uncMarker = $"issue1746-{Guid.NewGuid():N}";
        var uncPath = $@"\\{uncMarker}\share\backup";

        var settingsMock = new Mock<ISettingsRepository>();
        settingsMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = uncPath });

        using var checkerStarted = new ManualResetEventSlim(false);
        using var releaseChecker = new ManualResetEventSlim(false);
        try
        {
            PathValidator.UncReachabilityChecker = (path, timeoutMs) =>
            {
                if (!path.Contains(uncMarker))
                {
                    return PathValidator.DefaultUncReachabilityChecker(path, timeoutMs);
                }
                checkerStarted.Set();
                releaseChecker.Wait(PathValidator.DefaultUncTimeoutMs);
                return false;
            };

            using var dbContext = new DbContext(_dbPath);
            var service = new BackupService(
                dbContext,
                settingsMock.Object,
                NullLogger<BackupService>.Instance);

            var task = service.GetBackupFilesAsync();

            checkerStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
                "UNC パス設定時は到達性チェックが呼ばれるべき（呼ばれない場合はテストの前提が崩れている）");
            task.IsCompleted.Should().BeFalse(
                "UNC 到達性チェックの完了前に制御が返るべき。リストア画面は UI スレッドから"
                + "本メソッドを呼ぶため、同期検証では画面が最大5秒固まる (Issue #1746)");

            releaseChecker.Set();
            var files = await task;
            files.Should().NotBeNull(
                "到達不可の UNC パスは既定パスへフォールバックし、一覧取得自体は成功すべき");
        }
        finally
        {
            PathValidator.UncReachabilityChecker = _originalUncReachabilityChecker;
        }
    }
}
