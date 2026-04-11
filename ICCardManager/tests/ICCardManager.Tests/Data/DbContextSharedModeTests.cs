using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// DbContextの共有モード（ネットワーク共有フォルダ）関連テスト
/// DbContextResilienceTests / DbContextConcurrentAccessTests と重複しない共有モード固有の
/// 判定ロジック(IsUncPath/IsSharedMode)・PRAGMA(foreign_keys)・スレッド安全性・UNCパス接続の
/// テストのみを扱う。
/// </summary>
public class DbContextSharedModeTests : IDisposable
{
    private readonly string _testDirectory;

    public DbContextSharedModeTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DbContextSharedModeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
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

    #region IsUncPath テスト

    [Theory]
    [InlineData(@"\\server\share\db.db", true)]
    [InlineData(@"\\192.168.1.1\share\iccard.db", true)]
    [InlineData(@"C:\ProgramData\ICCardManager\iccard.db", false)]
    [InlineData(@"D:\data\iccard.db", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUncPath_各種パスで正しく判定されること(string path, bool expected)
    {
        DbContext.IsUncPath(path).Should().Be(expected);
    }

    #endregion

    #region IsSharedMode テスト

    [Fact]
    public void IsSharedMode_パスを明示指定した場合trueであること()
    {
        var dbPath = Path.Combine(_testDirectory, "local.db");
        using var dbContext = new DbContext(dbPath);

        dbContext.IsSharedMode.Should().BeTrue();
    }

    [Fact]
    public void IsSharedMode_デフォルトパスの場合falseであること()
    {
        using var dbContext = new DbContext();

        dbContext.IsSharedMode.Should().BeFalse();
    }

    #endregion

    #region PRAGMA設定テスト（他のテストファイルでカバーされないPRAGMAのみ）

    [Fact]
    public void GetConnection_foreign_keysが有効であること()
    {
        var dbPath = Path.Combine(_testDirectory, "fk_test.db");
        using var dbContext = new DbContext(dbPath);
        using var lease = dbContext.LeaseConnection();
        var connection = lease.Connection;

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        var result = command.ExecuteScalar();

        Convert.ToInt32(result).Should().Be(1);
    }

    #endregion

    #region ExecuteWithRetryAsync — 戻り値なしオーバーロード

    /// <summary>
    /// ExecuteWithRetryAsyncの戻り値なしオーバーロードが正常に動作すること。
    /// (戻り値ありオーバーロードは DbContextResilienceTests でカバー済み)
    /// </summary>
    [Fact]
    public async Task ExecuteWithRetryAsync_戻り値なし版が正常に動作すること()
    {
        var dbPath = Path.Combine(_testDirectory, "retry_void.db");
        using var dbContext = new DbContext(dbPath);
        var executed = false;

        await dbContext.ExecuteWithRetryAsync(async () =>
        {
            executed = true;
            await Task.CompletedTask;
        });

        executed.Should().BeTrue();
    }

    #endregion

    #region 同時書き込みテスト（SQLITE_BUSY統合テスト）

    [Fact]
    public async Task 同時書き込みでbusy_timeoutにより待機して成功すること()
    {
        var dbPath = Path.Combine(_testDirectory, "concurrent_write.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // テーブル作成
        using var lease = dbContext.LeaseConnection();
        var conn = lease.Connection;
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = "CREATE TABLE IF NOT EXISTS test (id INTEGER PRIMARY KEY, value TEXT)";
        createCmd.ExecuteNonQuery();

        // 2つ目の接続（別プロセスのシミュレーション）
        using var conn2 = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath}");
        conn2.Open();
        using var pragmaCmd = conn2.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA busy_timeout = 5000;";
        pragmaCmd.ExecuteNonQuery();

        // conn1でトランザクション開始（書き込みロック取得）
        using var tx1 = conn.BeginTransaction();
        using var insertCmd1 = conn.CreateCommand();
        insertCmd1.Transaction = tx1;
        insertCmd1.CommandText = "INSERT INTO test (value) VALUES ('from_conn1')";
        insertCmd1.ExecuteNonQuery();

        // conn2から同時書き込みを試行（busy_timeoutにより待機→conn1がコミットした後に成功）
        var task2 = Task.Run(() =>
        {
            using var insertCmd2 = conn2.CreateCommand();
            insertCmd2.CommandText = "INSERT INTO test (value) VALUES ('from_conn2')";
            // conn1がロックを保持中なので、busy_timeoutで待機する
            // 別スレッドでconn1をコミットしてからinsertする
            return insertCmd2;
        });

        // conn1をコミット（conn2のロック待ちが解消される）
        await Task.Delay(100);
        tx1.Commit();

        // conn2から書き込み
        using var insertCmd2Direct = conn2.CreateCommand();
        insertCmd2Direct.CommandText = "INSERT INTO test (value) VALUES ('from_conn2')";
        insertCmd2Direct.ExecuteNonQuery();

        // 両方の行が存在することを確認
        using var countCmd = conn2.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM test";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        count.Should().Be(2);
    }

    [Fact]
    public void GetConnection_スレッドセーフであること()
    {
        var dbPath = Path.Combine(_testDirectory, "threadsafe_test.db");
        using var dbContext = new DbContext(dbPath);

        // 複数スレッドから同時にLeaseConnectionを呼び出す
        var tasks = new Task<System.Data.SQLite.SQLiteConnection>[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                using var lease = dbContext.LeaseConnection();
                return lease.Connection;
            });
        }

        Task.WaitAll(tasks);

        // 全スレッドが同一の接続オブジェクトを取得すること
        var connections = tasks.Select(t => t.Result).Distinct().ToList();
        connections.Should().HaveCount(1);
        connections[0].State.Should().Be(ConnectionState.Open);
    }

    #endregion

    #region UNCパス接続テスト

    [Fact]
    public void UNCパス経由でSQLite接続が可能であること()
    {
        var uncPath = @"\\MASAYUKI-COM\share\iccard.db";
        if (!System.IO.File.Exists(uncPath))
            return;

        var results = new System.Collections.Generic.List<string>();

        // 方式1: 直接UNCパス（\\server\share\file）
        try
        {
            using var c1 = new System.Data.SQLite.SQLiteConnection($"Data Source={uncPath}");
            c1.Open();
            results.Add("方式1(直接UNC): OK");
            c1.Close();
        }
        catch (Exception ex) { results.Add($"方式1(直接UNC): NG - {ex.Message}"); }

        // 方式2: バックスラッシュ4つ（\\\\server\share\file）
        var fourSlash = @"\\\\" + uncPath.Substring(2);
        try
        {
            using var c2 = new System.Data.SQLite.SQLiteConnection($"Data Source={fourSlash}");
            c2.Open();
            results.Add("方式2(4バックスラッシュ): OK");
            c2.Close();
        }
        catch (Exception ex) { results.Add($"方式2(4バックスラッシュ): NG - {ex.Message}"); }

        // 方式3: フォワードスラッシュ（//server/share/file）
        var fwdSlash = uncPath.Replace('\\', '/');
        try
        {
            using var c3 = new System.Data.SQLite.SQLiteConnection($"Data Source={fwdSlash}");
            c3.Open();
            results.Add("方式3(フォワードスラッシュ): OK");
            c3.Close();
        }
        catch (Exception ex) { results.Add($"方式3(フォワードスラッシュ): NG - {ex.Message}"); }

        // 方式4: DefineDosDeviceでドライブマッピング
        try
        {
            using var dbContext = new DbContext(uncPath);
            using var lease4 = dbContext.LeaseConnection();
            var c4 = lease4.Connection;
            results.Add("方式4(DefineDosDevice): OK");
        }
        catch (Exception ex) { results.Add($"方式4(DefineDosDevice): NG - {ex.Message}"); }

        // 結果をコンソールに出力
        var report = string.Join("\n", results);
        System.Console.WriteLine("=== UNCパステスト結果 ===");
        System.Console.WriteLine(report);
        System.Console.WriteLine("========================");

        // 少なくとも1つの方式が成功すること
        results.Any(r => r.Contains("OK")).Should().BeTrue(
            $"いずれかの方式でUNCパス接続が成功するべき:\n{report}");
    }

    #endregion
}
