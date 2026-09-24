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
/// Issue #1108: BackupServiceのリストア安全性テスト
/// 共有モードで他PCの接続を検出し、リストアを拒否する機能を検証する。
/// </summary>
public class BackupServiceRestoreSafetyTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _backupDirectory;

    public BackupServiceRestoreSafetyTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"RestoreSafetyTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    #region CanAcquireExclusiveLock テスト

    /// <summary>
    /// ファイルが存在しない場合、排他ロックは取得可能（trueを返す）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_ファイル未存在でtrueを返すこと()
    {
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.db");

        BackupService.CanAcquireExclusiveLock(nonExistentPath).Should().BeTrue();
    }

    /// <summary>
    /// ファイルが存在し他プロセスが使用していない場合、排他ロックが取得できること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_未使用ファイルでtrueを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "unlocked.db");
        File.WriteAllText(dbPath, "test");

        BackupService.CanAcquireExclusiveLock(dbPath).Should().BeTrue();
    }

    /// <summary>
    /// ファイルが他プロセスにロックされている場合、falseを返すこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_ロック中ファイルでfalseを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "locked.db");
        File.WriteAllText(dbPath, "test");

        // 他プロセスによるロックをシミュレート
        using var lockStream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        BackupService.CanAcquireExclusiveLock(dbPath).Should().BeFalse();
    }

    /// <summary>
    /// ファイルが読み取り共有で開かれている場合、排他ロックが取得できないこと
    /// （他PCのSQLite接続をシミュレート）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CanAcquireExclusiveLock_読み取り共有ロック中でfalseを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "shared_read.db");
        File.WriteAllText(dbPath, "test");

        // SQLiteの典型的なロック（Read/Write + ReadWrite共有）をシミュレート
        using var lockStream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        BackupService.CanAcquireExclusiveLock(dbPath).Should().BeFalse();
    }

    #endregion

    #region CleanupJournalFiles テスト

    /// <summary>
    /// ジャーナルファイルが存在する場合に削除されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupJournalFiles_ジャーナルファイルを削除すること()
    {
        var dbPath = Path.Combine(_testDirectory, "cleanup.db");
        File.WriteAllText(dbPath, "test");

        // ジャーナルファイルを作成
        File.WriteAllText(dbPath + "-journal", "journal");
        File.WriteAllText(dbPath + "-wal", "wal");
        File.WriteAllText(dbPath + "-shm", "shm");

        var service = CreateService(dbPath);
        service.CleanupJournalFiles(dbPath);

        File.Exists(dbPath + "-journal").Should().BeFalse();
        File.Exists(dbPath + "-wal").Should().BeFalse();
        File.Exists(dbPath + "-shm").Should().BeFalse();
        // DBファイル自体は削除されないこと
        File.Exists(dbPath).Should().BeTrue();
    }

    /// <summary>
    /// ジャーナルファイルが存在しない場合もエラーにならないこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CleanupJournalFiles_ファイル未存在でもエラーにならないこと()
    {
        var dbPath = Path.Combine(_testDirectory, "no_journal.db");

        var service = CreateService(dbPath);
        var act = () => service.CleanupJournalFiles(dbPath);

        act.Should().NotThrow();
    }

    #endregion

    #region RestoreFromBackup 共有モード テスト

    /// <summary>
    /// 共有モードで他PCが接続中の場合、リストアが拒否され、DB が書き換わらないこと
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2103: 旧テストはローカルの一時パスで <c>IsSharedMode == false</c> のまま動いており、
    /// さらに他 PC の接続を <c>FileShare.ReadWrite</c> のハンドルで模していたため、拒否の実際の理由は
    /// 「開いたままのハンドルで <c>File.Move</c> が IOException になる」ことだった。
    /// 共有モードの判定（<c>IsSharedMode &amp;&amp; !CanAcquireExclusiveLock(...)</c>）を丸ごと消しても緑だった。
    /// </para>
    /// <para>
    /// ここでは <c>forceSharedMode: true</c> で共有モードにし、他 PC のハンドルに
    /// <c>FileShare.Delete</c> を付ける。これで <c>File.Move</c> は成功する（名前の変更を妨げない）ため、
    /// リストアを止められるのはガードだけになる。対の表明（ローカルモードでは同じハンドルがあっても
    /// リストアが進む）が、このハンドル自体はリストアを妨げないことを示す。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_共有モードで他接続ありの場合falseを返しDBを書き換えないこと()
    {
        var dbPath = Path.Combine(_testDirectory, "shared_restore.db");
        using var dbContext = new DbContext(dbPath, null, forceSharedMode: true);
        dbContext.InitializeDatabase();

        // バックアップファイルを作成してから、現在の DB だけに印を付ける
        var backupPath = Path.Combine(_backupDirectory, "backup.db");
        File.Copy(dbPath, backupPath);
        WriteMarker(dbContext, "current");

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        // 他 PC の接続を模す。FileShare.Delete を付けるので File.Move（名前の変更）は妨げない
        dbContext.CloseConnection();
        using (OpenOtherPcHandle(dbPath))
        {
            // Act
            var result = service.RestoreFromBackup(backupPath);

            // Assert
            result.Should().BeFalse("他PCが接続中のためリストアは拒否されるべき");
        }

        File.Exists(dbPath + ".temp").Should().BeFalse("拒否した場合は現在の DB を退避しない");
        ReadMarker(dbPath).Should().Be("current", "拒否した場合は現在の DB をそのまま残す");
    }

    /// <summary>
    /// 対の表明（Issue #2103）: ローカルモードでは、同じ「他のハンドル」があってもリストアが進むこと。
    /// </summary>
    /// <remarks>
    /// 他 PC の接続を検出して拒否するのは共有モードだけ（Issue #1108）。この表明が無いと、
    /// 上のテストが「ガードで拒否された」のか「ハンドルのせいで File.Move が失敗した」のかを区別できない。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_ローカルモードでは他のハンドルがあってもリストアが進むこと()
    {
        var dbPath = Path.Combine(_testDirectory, "local_restore.db");
        using var dbContext = new DbContext(dbPath, null, forceSharedMode: false);
        dbContext.InitializeDatabase();

        var backupPath = Path.Combine(_backupDirectory, "backup_local.db");
        File.Copy(dbPath, backupPath);
        WriteMarker(dbContext, "current");

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        dbContext.CloseConnection();
        using (OpenOtherPcHandle(dbPath))
        {
            // Act
            var result = service.RestoreFromBackup(backupPath);

            // Assert
            result.Should().BeTrue("ローカルモードは他 PC の接続を検出しない（このハンドルは File.Move を妨げない）");
        }

        ReadMarker(dbPath).Should().BeNull("バックアップ（印を付ける前の DB）でリストアされている");
    }

    /// <summary>
    /// 共有モードで他PCが接続していない場合、リストアが成功すること
    /// </summary>
    /// <remarks>
    /// Issue #2103: 旧テストは共有モードになっておらず、ガードを通っていなかった。
    /// 共有モードでは自 PC の接続が残っていても排他ロックの確認に失敗するため、
    /// <c>SuspendConnections</c> が自 PC の接続を閉じてから確認していることもこのテストが担う。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_共有モードで他接続なしの場合成功すること()
    {
        var dbPath = Path.Combine(_testDirectory, "shared_restore_ok.db");
        using var dbContext = new DbContext(dbPath, null, forceSharedMode: true);
        dbContext.InitializeDatabase();

        // バックアップファイルを作成
        var backupPath = Path.Combine(_backupDirectory, "backup_ok.db");
        File.Copy(dbPath, backupPath);
        WriteMarker(dbContext, "current");

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        // Act（他接続なし。自 PC の接続は開いたまま渡す）
        var result = service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        ReadMarker(dbPath).Should().BeNull("バックアップ（印を付ける前の DB）でリストアされている");
    }

    private const string MarkerKey = "issue2103_restore_marker";

    /// <summary>
    /// 他 PC の SQLite 接続を模したハンドル。<c>FileShare.Delete</c> を付けるため名前の変更は妨げないが、
    /// <c>FileShare.None</c> での排他オープン（<see cref="BackupService.CanAcquireExclusiveLock"/>）は失敗させる。
    /// </summary>
    private static FileStream OpenOtherPcHandle(string dbPath) =>
        new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    private static void WriteMarker(DbContext dbContext, string value)
    {
        using var lease = dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
        command.Parameters.AddWithValue("@key", MarkerKey);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    private static string? ReadMarker(string dbPath)
    {
        using var connection = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key";
        command.Parameters.AddWithValue("@key", MarkerKey);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// リストア成功後にジャーナルファイルが清掃されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_成功後にジャーナルファイルが削除されること()
    {
        var dbPath = Path.Combine(_testDirectory, "journal_cleanup.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // バックアップファイルを作成
        var backupPath = Path.Combine(_backupDirectory, "backup_jc.db");
        File.Copy(dbPath, backupPath);

        // ジャーナルファイルを作成（リストア前の古いジャーナル）
        File.WriteAllText(dbPath + "-journal", "old journal");

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        // Act
        var result = service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        File.Exists(dbPath + "-journal").Should().BeFalse("リストア後にジャーナルファイルが清掃されるべき");
    }

    /// <summary>
    /// IsSharedModeプロパティがDbContextの状態を正しく反映すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void IsSharedMode_DbContextの状態を反映すること()
    {
        // Issue #1559: UNCパスで共有モードを発動
        using var sharedContext = new DbContext(@"\\server\share\shared_check.db");
        var sharedService = new BackupService(
            sharedContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);
        sharedService.IsSharedMode.Should().BeTrue();

        using var localContext = new DbContext();
        var localService = new BackupService(
            localContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);
        localService.IsSharedMode.Should().BeFalse();
    }

    #endregion

    #region Issue #1166: 接続一時停止テスト

    /// <summary>
    /// SuspendConnections中に新規の接続リース取得がInvalidOperationExceptionをスローすること
    /// </summary>
    /// <remarks>
    /// Issue #1988: 生の接続を返す <c>GetConnection()</c> は削除したため <c>LeaseConnectionAsync()</c> で
    /// 表明する。検査している性質は変わらない（停止の判定は <c>GetConnectionInternal</c> にあり、
    /// <c>GetConnection</c> 固有ではなかった）。
    /// <para>
    /// <b>同期版の <c>LeaseConnection()</c> では表明できない</b>: <c>SuspendConnections</c> は
    /// スコープの Dispose まで接続セマフォを保持し続けるため（Issue #1809）、同期リースは
    /// 例外にならず<b>ブロックして解除後に成功する</b>のが仕様。セマフォを取らない
    /// <c>LeaseConnectionAsync()</c> だけが停止中に <c>GetConnectionInternal</c> へ到達して拒否される。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SuspendConnections_接続リース取得が例外をスローすること()
    {
        var dbPath = Path.Combine(_testDirectory, "suspend_test.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // Act: 接続を一時停止
        using (dbContext.SuspendConnections())
        {
            // Assert: 新規リースの取得が例外をスロー
            dbContext.IsConnectionSuspended.Should().BeTrue();
            var act = async () =>
            {
                using var suspendedLease = await dbContext.LeaseConnectionAsync();
            };
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*一時停止中*");
        }

        // スコープ終了後は接続可能に復帰
        dbContext.IsConnectionSuspended.Should().BeFalse();
        using var lease = dbContext.LeaseConnection();
        lease.Connection.Should().NotBeNull();
        lease.Connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// SuspendConnectionsのDisposeで接続が再許可されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void SuspendConnections_Dispose後に接続リース取得が成功すること()
    {
        var dbPath = Path.Combine(_testDirectory, "suspend_resume.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        var scope = dbContext.SuspendConnections();
        dbContext.IsConnectionSuspended.Should().BeTrue();

        scope.Dispose();
        dbContext.IsConnectionSuspended.Should().BeFalse();

        // 再接続が正常に動作すること（Issue #1988: GetConnection() は削除済み）
        using var lease = dbContext.LeaseConnection();
        lease.Connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// リストア中にバックグラウンドからの接続取得が拒否され、リストアが安全に完了すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void RestoreFromBackup_リストア中は接続リース取得が拒否されること()
    {
        var dbPath = Path.Combine(_testDirectory, "restore_guard.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // バックアップを作成
        var backupPath = Path.Combine(_backupDirectory, "backup_guard.db");
        File.Copy(dbPath, backupPath);

        var service = new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);

        // Act: リストア実行（内部でSuspendConnectionsが使われる）
        var result = service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        // リストア完了後は接続が可能に復帰していること
        dbContext.IsConnectionSuspended.Should().BeFalse();
        using var lease = dbContext.LeaseConnection();
        lease.Connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// SuspendConnectionsを複数回Disposeしてもエラーにならないこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void SuspendConnections_複数回Disposeしてもエラーにならないこと()
    {
        var dbPath = Path.Combine(_testDirectory, "double_dispose.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        var scope = dbContext.SuspendConnections();
        scope.Dispose();
        var act = () => scope.Dispose();
        act.Should().NotThrow();

        // 接続は可能に復帰していること
        dbContext.IsConnectionSuspended.Should().BeFalse();
    }

    #endregion

    #region ヘルパーメソッド

    private BackupService CreateService(string dbPath)
    {
        var dbContext = new DbContext(dbPath);
        return new BackupService(
            dbContext,
            CreateSettingsRepositoryMock().Object,
            NullLogger<BackupService>.Instance);
    }

    private Mock<ISettingsRepository> CreateSettingsRepositoryMock()
    {
        var mock = new Mock<ISettingsRepository>();
        mock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _backupDirectory });
        return mock;
    }

    #endregion
}
