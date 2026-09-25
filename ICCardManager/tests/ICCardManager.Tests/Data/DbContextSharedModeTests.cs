using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;
using Xunit.Sdk;

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
    public void IsSharedMode_UNCパスを指定した場合trueであること()
    {
        // Issue #1559: 共有モード判定は UNC または マップドネットワークドライブのみ
        using var dbContext = new DbContext(@"\\server\share\iccard.db");

        dbContext.IsSharedMode.Should().BeTrue();
    }

    [Fact]
    public void IsSharedMode_ローカルフルパスを指定した場合falseであること()
    {
        // Issue #1559: ローカルフルパス指定は共有モード扱いにしない
        var dbPath = Path.Combine(_testDirectory, "local.db");
        using var dbContext = new DbContext(dbPath);

        dbContext.IsSharedMode.Should().BeFalse();
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
    public void 接続リース_foreign_keysが有効であること()
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

    /// <summary>
    /// 別の接続（別 PC 相当）が書き込みロックを保持している間、<see cref="DbContext"/> の接続からの
    /// 書き込みは <c>busy_timeout</c> で待機し、ロックが解放されたあとで成功すること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2103: 旧テストは 2 本目の接続の INSERT を 1 本目のコミットの**後**に実行しており、
    /// ロック待ちが一度も起きていなかった。しかも 2 本目の接続は自分で <c>PRAGMA busy_timeout</c> を
    /// 設定していたため、<see cref="DbContext"/> の <c>ConfigurePragmas</c> から busy_timeout を
    /// 消しても緑のままだった。
    /// </para>
    /// <para>
    /// ここでは待つ側に <see cref="DbContext"/> の接続（PRAGMA を DbContext 自身が設定したもの）を使い、
    /// ロックを保持したまま待つ側に書き込みを発行させてから解放する。
    /// </para>
    /// <para>
    /// 待つ側のコマンドは <c>CommandTimeout = 0</c> にする。System.Data.SQLite は SQLITE_BUSY を受けると
    /// <c>CommandTimeout</c>（既定 30 秒）の間、自前で再試行するため、既定のままでは busy_timeout が
    /// 無くてもこの再試行が待機を肩代わりし、DbContext が PRAGMA を設定したかどうかを観測できない。
    /// </para>
    /// <para>
    /// Issue #2135: 判定は**待つ側が自分で測った経過時間**で行う。旧版は「テスト本体が 500ms 待つ間に
    /// 待つ側が終わっていないこと」で判定しており、テスト本体の継続がスレッドプールで busy_timeout
    /// （5 秒）を超えて遅れると、busy_timeout が効いて 5 秒待ったうえで失敗した待つ側を
    /// 「待たずに失敗した」と取り違えた（CI の Debug ジョブで 1 回、所要 7 秒で赤）。
    /// </para>
    /// <list type="bullet">
    /// <item>成功: 解放後に書き込めた。経過時間がロックの保持時間以上であることは計測の健全性の確認であって、
    /// 競合が起きたことの証明ではない（待つ側が合図の直後に解放まで横取りされれば、競合なしでも満たす。
    /// PRAGMA が消えた退行は <c>DbContextResilienceTests</c> の PRAGMA のテストが決定的に捕まえる）</item>
    /// <item>SQLITE_BUSY で失敗し、経過時間が busy_timeout の半分未満 ＝ 待たずに失敗した（busy_timeout が効いていない）→ 赤</item>
    /// <item>SQLITE_BUSY で失敗し、経過時間が busy_timeout の半分以上 ＝ 待機はしたが、テスト本体の解放が間に合わなかった
    /// → 判定できないので DB を作り直してやり直す（「解放後に成功する」を確かめないまま緑にはしない）</item>
    /// </list>
    /// <para>
    /// PRAGMA が無いときの失敗は数十ミリ秒以内に起き、効いているときの失敗は busy_timeout ちょうどで起きるので、
    /// 境界を半分に置けば両者の間は広く空く。テスト本体のスケジューリング遅延は待つ側の経過時間を
    /// **伸ばす方向にしか**働かないため、「待たずに失敗した」を遅延が作り出すことはない。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 同時書き込み_他の接続が書き込みロックを保持中はbusy_timeoutで待機しロック解放後に成功すること()
    {
        var waitedLowerBound = TimeSpan.FromMilliseconds(DbContext.LocalBusyTimeoutMs / 2);
        var inconclusiveAttempts = new List<string>();

        for (var attempt = 1; attempt <= MaxLockContentionAttempts; attempt++)
        {
            var outcome = await RunLockContentionAttemptAsync(
                Path.Combine(_testDirectory, $"concurrent_write_{attempt}.db"));

            if (outcome.Error == null)
            {
                outcome.WaiterElapsed.Should().BeGreaterOrEqualTo(LockHoldDuration - TimerResolutionMargin,
                    "計測は合図より前に始め、解放は合図から保持時間の後なので、成功時の経過時間は保持時間を下回らない");
                outcome.StoredValues.Should().Equal("from_holder", "from_waiter");
                return;
            }

            outcome.Error.Should().BeOfType<SQLiteException>(
                "ロック待ち以外の理由で失敗してはならない: " + outcome.Error);
            ((SQLiteException)outcome.Error).ResultCode.Should().Be(SQLiteErrorCode.Busy,
                "ロック待ち以外の理由で失敗してはならない: " + outcome.Error.Message);
            outcome.WaiterElapsed.Should().BeGreaterOrEqualTo(waitedLowerBound,
                $"待つ側が {outcome.WaiterElapsed.TotalMilliseconds:F0}ms で SQLITE_BUSY により失敗した。" +
                $"busy_timeout（{DbContext.LocalBusyTimeoutMs}ms）が効いていれば、その間は待機してから失敗する");

            // busy_timeout は効いた（待機してから失敗した）が、テスト本体の解放が間に合わなかった。判定できないのでやり直す
            inconclusiveAttempts.Add(
                $"試行 {attempt}: 待つ側は {outcome.WaiterElapsed.TotalMilliseconds:F0}ms 待機して失敗" +
                $"（ロックの保持 {outcome.LockHeld.TotalMilliseconds:F0}ms）");
        }

        throw new XunitException(
            $"{MaxLockContentionAttempts} 回とも、テスト本体がロックを解放する前に待つ側の busy_timeout が満了した。" +
            "busy_timeout 自体は効いている（待機してから失敗した）が、「解放後に成功すること」を確かめられなかった。" +
            "ランナーの負荷が高い可能性がある: " + string.Join(" / ", inconclusiveAttempts));
    }

    /// <summary>
    /// 同時書き込みテストの試行上限。やり直すのは「待つ側は待機したが、テスト本体の解放が間に合わなかった」ときだけ
    /// </summary>
    private const int MaxLockContentionAttempts = 3;

    /// <summary>待つ側が書き込みを発行してから、ロックを保持し続ける時間</summary>
    private static readonly TimeSpan LockHoldDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>Task.Delay はタイマー分解能（約 15ms）ぶん早く完了し得るので、その分だけ下限を緩める</summary>
    private static readonly TimeSpan TimerResolutionMargin = TimeSpan.FromMilliseconds(50);

    private sealed class LockContentionOutcome
    {
        public TimeSpan WaiterElapsed { get; set; }
        public Exception? Error { get; set; }
        public TimeSpan LockHeld { get; set; }
        public List<string> StoredValues { get; } = new List<string>();
    }

    /// <summary>
    /// 素の接続でロックを取り、別の <see cref="DbContext"/> に書き込みを発行させ、
    /// <see cref="LockHoldDuration"/> 保持してから解放する。待つ側の経過時間は待つ側自身が測る。
    /// </summary>
    private static async Task<LockContentionOutcome> RunLockContentionAttemptAsync(string dbPath)
    {
        using (var setup = new DbContext(dbPath))
        {
            setup.InitializeDatabase();
            using var setupLease = setup.LeaseConnection();
            using var createCmd = setupLease.Connection.CreateCommand();
            createCmd.CommandText = "CREATE TABLE IF NOT EXISTS test (id INTEGER PRIMARY KEY, value TEXT)";
            createCmd.ExecuteNonQuery();
        }

        var outcome = new LockContentionOutcome();

        // ロックを保持する側（別 PC 相当）。DbContext を通さない素の接続で、busy_timeout は設定しない
        using var holder = new SQLiteConnection($"Data Source={dbPath}");
        holder.Open();
        using (var beginCmd = holder.CreateCommand())
        {
            // BEGIN IMMEDIATE で RESERVED ロックを取り、他の接続の書き込みを塞ぐ
            beginCmd.CommandText = "BEGIN IMMEDIATE; INSERT INTO test (value) VALUES ('from_holder');";
            beginCmd.ExecuteNonQuery();
        }

        // 待つ側: 別の DbContext（PRAGMA は DbContext.ConfigurePragmas が設定する）
        using var waiterContext = new DbContext(dbPath);
        var insertIssued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterTask = Task.Run(() =>
        {
            using var lease = waiterContext.LeaseConnection();
            using var insertCmd = lease.Connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO test (value) VALUES ('from_waiter')";
            insertCmd.CommandTimeout = 0;
            // 計測は合図より前に始める。テスト本体は合図を見てからロックを保持し始めるので、
            // 成功時の経過時間は必ずロックの保持時間以上になる
            var stopwatch = Stopwatch.StartNew();
            insertIssued.SetResult(true);
            try
            {
                insertCmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                outcome.Error = ex;
            }
            finally
            {
                outcome.WaiterElapsed = stopwatch.Elapsed;
            }
        });

        (await Task.WhenAny(insertIssued.Task, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().BeSameAs(insertIssued.Task, "待つ側が書き込みを発行するところまで進むこと");

        // ロックを保持したまま待つ。待つ側が途中で終わったかどうかはここでは判定しない（待つ側の経過時間で判定する）
        var holding = Stopwatch.StartNew();
        await Task.Delay(LockHoldDuration);

        // ロックを解放 → 待つ側の書き込みが成功する
        using (var commitCmd = holder.CreateCommand())
        {
            commitCmd.CommandText = "COMMIT;";
            commitCmd.ExecuteNonQuery();
        }
        outcome.LockHeld = holding.Elapsed;

        (await Task.WhenAny(waiterTask, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should().BeSameAs(waiterTask, "ロック解放後は待機が解けて書き込みが終わること");
        await waiterTask;

        using var selectCmd = holder.CreateCommand();
        selectCmd.CommandText = "SELECT value FROM test ORDER BY id";
        using var reader = selectCmd.ExecuteReader();
        while (reader.Read())
        {
            outcome.StoredValues.Add(reader.GetString(0));
        }
        return outcome;
    }

    [Fact]
    public void 接続リース_スレッドセーフであること()
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

    /// <summary>
    /// 実機の共有フォルダーで UNC パスの開き方を比べる診断（手動実行用）
    /// </summary>
    /// <remarks>
    /// Issue #2107: 開発機の固定ホスト名に依存し、存在しなければ <c>return</c> で成功扱いになっていた
    /// （CI では名前解決を待ったうえで何も検証せずに緑になる）。回帰の検出は担えないので Skip にする。
    /// 変換そのものの回帰は、純関数として <c>DbContextSharedModeDetectionTests.BuildConnectionString_*</c> が固定している。
    /// 実機で確かめるときは Skip を外し、<c>uncPath</c> を手元の共有フォルダーへ書き換えて実行する。
    /// </remarks>
    [Fact(Skip = "診断用（実機の共有フォルダーが必要）。UNC の変換は DbContextSharedModeDetectionTests.BuildConnectionString_* が検証する")]
    public void UNCパス経由でSQLite接続が可能であること()
    {
        var uncPath = @"\\MASAYUKI-COM\share\iccard.db";

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
