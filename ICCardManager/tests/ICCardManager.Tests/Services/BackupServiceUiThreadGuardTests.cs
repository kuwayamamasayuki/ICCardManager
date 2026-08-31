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
using ICCardManager.Tests.Infrastructure;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1361: <see cref="BackupService"/> を UI スレッドから呼び出しても
/// DbContext の UI スレッドガード (Issue #1281) に抵触しないことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 実際の WPF Dispatcher を立ち上げず、<c>DbContext.IsOnUiThread</c> 内部フックを差し替えて
/// 「呼び出し元 = UI、Task.Run 先 = 非 UI」を模擬する。これにより BackupService の async 版が
/// 確実に Task.Run でオフロードしていることを検証できる。
/// </para>
/// <para>
/// Issue #1961: 模擬は <see cref="SimulatedUiThread"/>（スレッドプール外の専用スレッド）で行う。
/// テスト本体のスレッドを UI と見なす従来の書き方は、<c>await</c> で解放されたそのスレッドを
/// プールが SUT の <c>Task.Run</c> に再利用したときに ID が一致し、オフロード先が UI と判定されて
/// 間欠的に赤くなった。専用スレッドは <c>Task.Run</c> の実行先になり得ないため一致が起こり得ない。
/// </para>
/// <para>
/// Issue #1961: ロガーは <c>NullLogger</c> ではなく <see cref="RecordingLogger{T}"/> を渡す。
/// <see cref="BackupService.ExecuteAutoBackupAsync"/> は失敗を例外ではなく <c>null</c> 戻り値で
/// 表す（Issue #1737）ため、UI スレッドガードの発火・I/O 失敗・権限失敗がすべて <c>null</c> に
/// 畳まれる。サービスが記録した理由をアサーションメッセージへ載せ、CI のログだけで切り分けられるようにする。
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

    private Mock<ISettingsRepository> CreateSettingsRepositoryMock(string backupPath = null)
    {
        var mock = new Mock<ISettingsRepository>();
        // ReturnsAsync は完了済みタスクを返すため、await .ConfigureAwait(false) は
        // context switch せず呼び出し元スレッドに留まる。本番のキャッシュヒット経路と同じ挙動になる。
        mock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = backupPath ?? _backupDirectory });
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

        var logger = new RecordingLogger<BackupService>();
        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            logger);

        var backupPath = Path.Combine(_backupDirectory, "ui_thread_sync.db");

        // 初期化完了後に UI スレッド模擬を有効化（セットアップ自体がガードに抵触しないように）
        var result = SimulatedUiThread.Invoke(() => service.CreateBackup(backupPath));

        result.Should().BeFalse(
            "sync 版 CreateBackup は UI スレッドから呼ぶと DbContext.LeaseConnection の "
            + "UI スレッドガード (Issue #1281) で InvalidOperationException が発生し、"
            + "BackupService はこれを catch して false を返すべき。実際のログ:"
            + Environment.NewLine + logger.FormatEntries());
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

        var logger = new RecordingLogger<BackupService>();
        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            logger);

        var backupPath = Path.Combine(_backupDirectory, "ui_thread_async.db");

        var result = await SimulatedUiThread.InvokeAsync(() => service.CreateBackupAsync(backupPath));

        result.Should().BeTrue(
            "CreateBackupAsync は Task.Run 経由でバックグラウンドに DB 接続リースをオフロードし、"
            + "UI スレッドガードに抵触しないべき (Issue #1361)。実際のログ:"
            + Environment.NewLine + logger.FormatEntries());
        File.Exists(backupPath).Should().BeTrue(
            "成功時はバックアップファイルが実際に生成されるべき");
    }

    /// <summary>
    /// Issue #1809: 同期 <see cref="BackupService.RestoreFromBackup"/> を UI スレッド模擬で呼ぶと、
    /// 内部の <c>DbContext.SuspendConnections()</c>（セマフォ同期取得）が UI スレッドガードで
    /// 拒否され、catch 節で握って <c>false</c> を返し、本番 DB には触れないこと。
    /// </summary>
    [Fact]
    public void RestoreFromBackup_sync_UIスレッド模擬時はガードが発火しfalseを返すこと()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();

        var backupPath = Path.Combine(_backupDirectory, "restore_source_sync.db");
        File.Copy(_dbPath, backupPath);

        var logger = new RecordingLogger<BackupService>();
        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            logger);

        var result = SimulatedUiThread.Invoke(() => service.RestoreFromBackup(backupPath));

        result.Should().BeFalse(
            "sync 版 RestoreFromBackup は UI スレッドから呼ぶと DbContext.SuspendConnections の "
            + "UI スレッドガード (Issue #1281 / #1809) で InvalidOperationException が発生し、"
            + "BackupService はこれを catch して false を返すべき。実際のログ:"
            + Environment.NewLine + logger.FormatEntries());
        dbContext.IsConnectionSuspended.Should().BeFalse("ガード発火時は一時停止状態を残さない");
    }

    /// <summary>
    /// Issue #1809: UI スレッド模擬時でも <see cref="BackupService.RestoreFromBackupAsync"/> は
    /// Task.Run でバックグラウンドにオフロードし、ガードに抵触せずリストアが成功すること。
    /// 本番の <c>SystemManageViewModel.RestoreAsync</c>（UI スレッド）はこちらを使う。
    /// </summary>
    [Fact]
    public async Task RestoreFromBackupAsync_UIスレッド模擬時でも成功すること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();

        var backupPath = Path.Combine(_backupDirectory, "restore_source_async.db");
        File.Copy(_dbPath, backupPath);

        var logger = new RecordingLogger<BackupService>();
        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            logger);

        var result = await SimulatedUiThread.InvokeAsync(() => service.RestoreFromBackupAsync(backupPath));

        result.Should().BeTrue(
            "RestoreFromBackupAsync は Task.Run 経由で SuspendConnections（セマフォ同期取得）を "
            + "バックグラウンドへオフロードし、UI スレッドガードに抵触しないべき (Issue #1809)。実際のログ:"
            + Environment.NewLine + logger.FormatEntries());
        dbContext.IsConnectionSuspended.Should().BeFalse("リストア完了後は一時停止が解除されているべき");
    }

    /// <summary>
    /// UI スレッド模擬時でも <see cref="BackupService.ExecuteAutoBackupAsync"/> は
    /// 内部で Task.Run を使用して UI スレッドガードに抵触せず完了する。
    /// 本番では <c>StartupTaskRunner</c> から UI スレッド上で起動される経路を模擬。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1961: <see cref="BackupService.ResolveBackupFolderAsync"/> は完了済み Task を返すよう
    /// 差し替える。実物は内部で <c>PathValidator.ValidateBackupPathAsync</c> の <c>Task.Run</c> を
    /// <c>await ... ConfigureAwait(false)</c> する（Issue #1746）ため、<b>継続が必ずスレッドプールへ移り、
    /// 後続の <c>Task.Run</c> を外しても UI スレッド上を通らない</b>。差し替え前は
    /// <c>BackupDatabaseTo</c> のオフロードを丸ごと削除しても本テストが緑になることを実測した。
    /// </para>
    /// <para>
    /// 完了済み Task にすると <c>await</c> の継続が同期的に進み、呼び出し元＝UI スレッドに留まる。
    /// これは<b>バックアップ先が未設定の本番経路</b>（<c>ResolveBackupFolderDetailAsync</c> は
    /// <c>BackupPath</c> が空なら <c>ValidateBackupPathAsync</c> を通らず、直前の
    /// <c>GetAppSettingsAsync</c> がキャッシュヒットすれば全体が同期完了する）と同じ形であり、
    /// 差し替えは検出力の回復であって本番と乖離した状況の捏造ではない。
    /// なお <c>BackupPath</c> が設定済みの場合は #1746 の <c>Task.Run</c> を必ず通るため、
    /// この経路では継続がスレッドプールへ移り、後続の <c>Task.Run</c> は結果的に冗長になる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAutoBackupAsync_UIスレッド模擬時でもバックアップファイルが生成されること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase(); // 初期化はガード設定の前に実行する（セットアップ自体が UI ガードに抵触しないように）

        var logger = new RecordingLogger<BackupService>();
        var service = CreateServiceWithSynchronousFolderResolution(dbContext, logger);

        var backupFilePath = await SimulatedUiThread.InvokeAsync(() => service.ExecuteAutoBackupAsync());

        backupFilePath.Should().NotBeNull(
            "ExecuteAutoBackupAsync は Task.Run で DB 接続リースをオフロードし、"
            + "UI スレッドガードに抵触せず完了すべき (Issue #1361)。実際のログ:"
            + Environment.NewLine + logger.FormatEntries());
        File.Exists(backupFilePath).Should().BeTrue(
            "成功時はバックアップファイルが実際に生成されるべき");
    }

    /// <summary>
    /// Issue #1961: UI スレッド模擬そのものが生きていることの対の表明。
    /// オフロード先まで UI と判定させると <see cref="BackupService.ExecuteAutoBackupAsync"/> は
    /// UI スレッドガードに抵触し、<c>null</c> を返す。
    /// </summary>
    /// <remarks>
    /// この表明が無いと、模擬を丸ごと外した（＝何も検査していない）実装でも
    /// 上のテストが緑になる。あわせて「null の理由が UI スレッドガードであること」を
    /// 記録ロガーの内容で表明し、I/O 失敗・権限失敗と取り違えていないことを固定する。
    /// </remarks>
    [Fact]
    public async Task ExecuteAutoBackupAsync_オフロード先までUIと判定するとnullを返しガードの例外が記録されること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();

        var logger = new RecordingLogger<BackupService>();
        var service = CreateServiceWithSynchronousFolderResolution(dbContext, logger);

        var backupFilePath = await SimulatedUiThread.InvokeAsync(
            () => service.ExecuteAutoBackupAsync(),
            isOnUiThread: () => true);

        backupFilePath.Should().BeNull(
            "どのスレッドも UI と判定させれば DbContext.LeaseConnection の UI スレッドガード "
            + "(Issue #1281) が発火し、ExecuteAutoBackupAsync は catch して null を返すべき。"
            + "ここが緑にならない場合、UI スレッド模擬が SUT へ届いていない。実際のログ:"
            + Environment.NewLine + logger.FormatEntries());

        logger.Entries.Should().Contain(
            e => e.Exception is InvalidOperationException
                 && e.Exception.Message.Contains("UI スレッドから呼び出せません"),
            "null の理由が UI スレッドガードであることをログから切り分けられるべき (Issue #1961)。実際のログ:"
            + Environment.NewLine + logger.FormatEntries());
    }

    /// <summary>
    /// <see cref="BackupService.ResolveBackupFolderAsync"/> だけを完了済み Task へ差し替えた
    /// 部分モックを作る（Issue #1961）。
    /// </summary>
    private BackupService CreateServiceWithSynchronousFolderResolution(
        DbContext dbContext,
        RecordingLogger<BackupService> logger)
    {
        var serviceMock = new Mock<BackupService>(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            logger)
        {
            CallBase = true,
        };

        serviceMock.Setup(x => x.ResolveBackupFolderAsync())
            .Returns(Task.FromResult(_backupDirectory));

        return serviceMock.Object;
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
        var folder = await AssertReturnsBeforeUncCheckCompletesAsync(
            service => service.ResolveBackupFolderAsync(),
            "UNC 到達性チェックの完了前に制御が返るべき。同期版 ValidateBackupPath を呼ぶ実装では"
            + "呼び出し自体がブロックし、起動時経路では UI スレッドが最大5秒固まる (Issue #1746)");

        folder.Should().Be(
            PathValidator.NormalizePath(PathValidator.GetDefaultBackupPath()),
            "到達不可の UNC パスは既定パスへフォールバックする既存挙動が保たれるべき");
    }

    /// <summary>
    /// Issue #1746: <see cref="BackupService.GetBackupFilesAsync"/>（リストア画面から
    /// UI スレッドで呼ばれる）も <see cref="BackupService.ResolveBackupFolderAsync"/> と
    /// 同型の「キャッシュヒット → 同期 UNC 検証」を持っていたため、同じ非ブロック性を表明する。
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_UNC到達性チェックの完了前に制御を返すこと()
    {
        var files = await AssertReturnsBeforeUncCheckCompletesAsync(
            service => service.GetBackupFilesAsync(),
            "UNC 到達性チェックの完了前に制御が返るべき。リストア画面は UI スレッドから"
            + "本メソッドを呼ぶため、同期検証では画面が最大5秒固まる (Issue #1746)");

        files.Should().NotBeNull(
            "到達不可の UNC パスは既定パスへフォールバックし、一覧取得自体は成功すべき");
    }

    /// <summary>
    /// Issue #1746 の非ブロック性表明の共通手順: 到達不可の UNC パスを設定し、
    /// 到達性チェックがブロックしている間に <paramref name="invokeAsync"/> が
    /// 未完了の Task を返すことを表明したうえで、チェックを解放して結果を返す。
    /// </summary>
    /// <remarks>
    /// アサーション失敗時（<c>checkerStarted</c> のタイムアウト・<c>IsCompleted</c> の失敗）にも
    /// <c>finally</c> でチェッカーを解放して SUT の Task を観測しきる。これを怠ると、
    /// <c>using</c> が破棄した <see cref="ManualResetEventSlim"/> に対して残存プールスレッドが
    /// <c>Wait</c> して <see cref="ObjectDisposedException"/> になり、本来のアサーション失敗が
    /// 二次例外に埋もれる（また未観測 Task が <see cref="Dispose"/> の一時フォルダ削除と競合する）。
    /// </remarks>
    private async Task<T> AssertReturnsBeforeUncCheckCompletesAsync<T>(
        Func<BackupService, Task<T>> invokeAsync,
        string nonBlockingBecause)
    {
        // テスト固有マーカー: 並列実行中の他テストが公開 API へ渡すパスには介入しない
        var uncMarker = $"issue1746-{Guid.NewGuid():N}";
        var uncPath = $@"\\{uncMarker}\share\backup";

        var settingsMock = CreateSettingsRepositoryMock(uncPath);

        using var checkerStarted = new ManualResetEventSlim(false);
        using var releaseChecker = new ManualResetEventSlim(false);
        Task<T> task = null;
        try
        {
            PathValidator.UncReachabilityChecker = (path, timeoutMs) =>
            {
                if (!path.Contains(uncMarker))
                {
                    return PathValidator.DefaultUncReachabilityChecker(path, timeoutMs);
                }
                checkerStarted.Set();
                // 修正前の同期実装への退行時もテストを有限時間で終わらせるための上限。
                // 緑実装では直後の releaseChecker.Set() で即解放されるため待ち切らない。
                // timeoutMs（本番の5秒）をそのまま使うと、CI 高負荷時に「Set → IsCompleted 読取」の
                // 間で待ちが自然満了し Task が完了して偽赤になり得るため、余裕を持った上限にする
                releaseChecker.Wait(TimeSpan.FromSeconds(30));
                return false; // 到達不可 → 既定パスへのフォールバック（既存挙動）を誘発
            };

            using var dbContext = new DbContext(_dbPath);
            var service = new BackupService(
                dbContext,
                settingsMock.Object,
                new RecordingLogger<BackupService>());

            task = invokeAsync(service);

            checkerStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
                "UNC パス設定時は到達性チェックが呼ばれるべき（呼ばれない場合はテストの前提が崩れている）");
            task.IsCompleted.Should().BeFalse(nonBlockingBecause);

            releaseChecker.Set();
            return await task;
        }
        finally
        {
            // アサーション失敗経路でもチェッカーを解放し、SUT の Task を観測しきってから
            // イベントの破棄（using）と Dispose() の一時フォルダ削除に進む
            releaseChecker.Set();
            if (task != null)
            {
                try { await task; }
                catch { /* 本来のアサーション失敗を二次例外で隠さない */ }
            }
            PathValidator.UncReachabilityChecker = _originalUncReachabilityChecker;
        }
    }
}
