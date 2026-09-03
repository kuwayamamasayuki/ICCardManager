using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1951: <see cref="CardRepository"/> / <see cref="StaffRepository"/> の
/// <c>InsertAsync</c> が SQLITE_BUSY / SQLITE_LOCKED を握りつぶし、
/// 共有モードのリトライ（<c>DbContext.ExecuteWithRetryAsync</c>）を無効化していた欠陥の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 欠陥の形: <c>catch (SQLiteException) { return false; }</c> が一過性のロック競合まで
/// <c>bool</c> へ畳んでいたため、リトライ判定（<c>ResultCode == Busy || Locked</c>）に
/// 例外が届かず、他 PC が書き込みロックを持っている一瞬に当たっただけの登録が
/// 恒久的な失敗として報告されていた。
/// </para>
/// <para>
/// テストは <b>対で</b> 置く:
/// ①「欠陥を突く側」= ロックが解けたら登録が成功すること（修正前は無条件に false）、
/// ②「握りつぶしを別の形で再導入していない側」= ロックが解けなければ
/// <c>false</c> ではなく <see cref="SQLiteException"/> として報告されること、
/// ③「リトライしても直らない失敗を巻き込んでいない側」= UNIQUE 制約違反・
/// 主キー重複が従来どおり扱われること。
/// ①だけだと「常に成功を返す」実装でも緑になり、②だけだと catch を丸ごと消した
/// 実装（③が壊れる）でも緑になる。
/// </para>
/// <para>
/// ロック競合は実 SQLite でしか再現しないため、ファイル DB と第 2 の接続で作る。
/// </para>
/// <para>
/// <b>ロックの保持時間は「1 回目の試行が実際に失敗する」ことから逆算する。</b>
/// <c>PRAGMA busy_timeout = 0</c> にしても System.Data.SQLite は内部で再試行しており、
/// 実測では約 2.8 秒ブロックしてから SQLITE_BUSY を返す（開発機で計測）。
/// 保持時間をそれより短く（例: 250ms）すると 1 回目の試行がそのまま成功してしまい、
/// <b>修正前のコードでも緑になる＝検出力ゼロ</b>のテストになる（初版が実際にそうだった）。
/// <see cref="LockHoldMilliseconds"/> はこの実測値より十分長く、かつローカルモードの
/// リトライ枠（100 / 500 / 2,000ms）を使い切る前に解ける値を選んでいる。
/// </para>
/// </remarks>
public class RepositoryInsertRetryTests : IDisposable
{
    /// <summary>
    /// ロックを保持する時間（ミリ秒）。remarks の「逆算」を参照。
    /// </summary>
    private const int LockHoldMilliseconds = 4000;

    private readonly string _dbPath;
    private readonly DbContext _dbContext;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    public RepositoryInsertRetryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ic_insert_retry_{Guid.NewGuid():N}.db");
        _dbContext = new DbContext(_dbPath);
        _dbContext.InitializeDatabase();
        DisableBusyTimeout();

        _cardRepository = new CardRepository(_dbContext, CreatePassThroughCache(), Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(_dbContext, CreatePassThroughCache(), Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        TryDeleteDatabaseFiles();
        GC.SuppressFinalize(this);
    }

    #region 欠陥を突く側: 一過性のロックはリトライで回復する

    /// <summary>
    /// カード登録: 他接続が書き込みロックを持っている間に始めた登録が、
    /// ロック解放後のリトライで成功すること（修正前は即座に false を返していた）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task カード登録_一過性のロック競合はリトライで成功すること()
    {
        // Arrange: 別接続が書き込みロックを保持し、1 回目の試行が SQLITE_BUSY で失敗したあとに解放する
        using var release = HoldWriteLockFor(TimeSpan.FromMilliseconds(LockHoldMilliseconds));
        var card = CreateCard("CARD000000000001", "はやかけん", "1");

        // Act
        var success = await _cardRepository.InsertAsync(card);

        // Assert
        success.Should().BeTrue(
            "SQLITE_BUSY は一過性の競合であり、リトライで回復できる。false へ畳むと恒久的な失敗として報告される");
        (await _cardRepository.GetByIdmAsync("CARD000000000001")).Should().NotBeNull(
            "リトライで成功した以上、行が実際に書き込まれていること");
    }

    /// <summary>
    /// 職員登録: 同上
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task 職員登録_一過性のロック競合はリトライで成功すること()
    {
        using var release = HoldWriteLockFor(TimeSpan.FromMilliseconds(LockHoldMilliseconds));
        var staff = CreateStaff("STAFF00000000001", "博多 花子");

        var success = await _staffRepository.InsertAsync(staff);

        success.Should().BeTrue();
        (await _staffRepository.GetByIdmAsync("STAFF00000000001")).Should().NotBeNull();
    }

    #endregion

    #region 対の表明: 回復しないロックは false ではなく例外として報告される

    /// <summary>
    /// カード登録: ロックが解けないままリトライを使い切った場合、
    /// <c>false</c>（＝業務的な失敗）ではなく <see cref="SQLiteException"/> として報告されること。
    /// </summary>
    /// <remarks>
    /// これが無いと「一過性のロックでも常に true を返す」実装でも上のテストが緑になる。
    /// また、失敗の理由が呼び出し元で区別できることを表明している
    /// （false は「対象行が無い／制約違反」を意味し、リトライでは直らない）。
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task カード登録_ロックが解けない場合はSQLiteExceptionとして報告されること()
    {
        using var lockHolder = HoldWriteLockIndefinitely();
        var card = CreateCard("CARD000000000002", "nimoca", "2");

        var act = async () => await _cardRepository.InsertAsync(card);

        var thrown = await act.Should().ThrowAsync<SQLiteException>(
            "握りつぶすとリトライ判定（ResultCode）に届かない");
        DbContext.IsTransientLockError(thrown.Which).Should().BeTrue(
            "報告される ResultCode は Busy / Locked のいずれかであること");
    }

    /// <summary>
    /// 職員登録: 同上
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task 職員登録_ロックが解けない場合はSQLiteExceptionとして報告されること()
    {
        using var lockHolder = HoldWriteLockIndefinitely();
        var staff = CreateStaff("STAFF00000000002", "天神 太郎");

        var act = async () => await _staffRepository.InsertAsync(staff);

        var thrown = await act.Should().ThrowAsync<SQLiteException>();
        DbContext.IsTransientLockError(thrown.Which).Should().BeTrue();
    }

    #endregion

    #region 対の表明: リトライしても直らない失敗は従来どおり扱う

    /// <summary>
    /// カード種別＋管理番号の UNIQUE 制約違反は、従来どおり
    /// <see cref="DuplicateCardNumberException"/> へ変換されること（Issue #1757 の経路を壊さない）。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task カード登録_管理番号の重複は従来どおりDuplicateCardNumberExceptionになること()
    {
        await _cardRepository.InsertAsync(CreateCard("CARD000000000003", "はやかけん", "5"));

        var act = async () => await _cardRepository.InsertAsync(CreateCard("CARD000000000004", "はやかけん", "5"));

        await act.Should().ThrowAsync<DuplicateCardNumberException>(
            "制約違反はリトライしても直らない。ロック競合と同じ扱いにしてはならない");
    }

    /// <summary>
    /// 主キー（IDm）の重複は、従来どおり <c>false</c> へ畳まれること。
    /// </summary>
    /// <remarks>
    /// catch を丸ごと削除して「すべての SQLiteException を投げる」実装にすると、
    /// この表明が赤くなる（呼び出し元は false を業務的な失敗として扱っている）。
    /// リトライで再実行されないこと（＝待たされないこと）も併せて確かめる。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task カード登録_IDmの重複は従来どおりfalseを返すこと()
    {
        await _cardRepository.InsertAsync(CreateCard("CARD000000000005", "SUGOCA", "7"));

        var stopwatch = Stopwatch.StartNew();
        var success = await _cardRepository.InsertAsync(CreateCard("CARD000000000005", "SUGOCA", "8"));
        stopwatch.Stop();

        success.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "制約違反はリトライ対象ではないため、待機を挟まず即座に確定すること");
    }

    /// <summary>
    /// 職員登録: 主キー（IDm）の重複は、従来どおり <c>false</c> を返すこと。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task 職員登録_IDmの重複は従来どおりfalseを返すこと()
    {
        await _staffRepository.InsertAsync(CreateStaff("STAFF00000000003", "大橋 一郎"));

        var stopwatch = Stopwatch.StartNew();
        var success = await _staffRepository.InsertAsync(CreateStaff("STAFF00000000003", "大橋 二郎"));
        stopwatch.Stop();

        success.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    #endregion

    #region ヘルパー

    private static ICacheService CreatePassThroughCache()
    {
        // キャッシュを挟むと GetByIdmAsync 前の InvalidateCache の有無で結果が変わるため、
        // 本テストではファクトリをそのまま実行する（DB の状態だけを見る）
        var mock = new Mock<ICacheService>();
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
                It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan __) => factory());
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
                It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan __) => factory());
        return mock.Object;
    }

    /// <summary>
    /// DbContext 側の接続の busy_timeout を 0 にする。
    /// </summary>
    /// <remarks>
    /// 既定（ローカルモード 5,000ms）のままだと、ロックを 250ms で解放するテストは
    /// 待機だけで成功してしまい、修正前のコードでも緑になる（＝検出力ゼロ）。
    /// </remarks>
    private void DisableBusyTimeout()
    {
        using var lease = _dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 0;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 別接続で書き込みロック（RESERVED）を取り、指定時間後に解放する。
    /// </summary>
    private IDisposable HoldWriteLockFor(TimeSpan duration)
    {
        var holder = new WriteLockHolder(_dbPath);
        _ = Task.Run(async () =>
        {
            await Task.Delay(duration).ConfigureAwait(false);
            holder.Release();
        });
        return holder;
    }

    /// <summary>
    /// 別接続で書き込みロックを取り、Dispose されるまで保持する。
    /// </summary>
    private IDisposable HoldWriteLockIndefinitely() => new WriteLockHolder(_dbPath);

    private static IcCard CreateCard(string idm, string cardType, string cardNumber) => new IcCard
    {
        CardIdm = idm,
        CardType = cardType,
        CardNumber = cardNumber,
        StartingPageNumber = 1
    };

    private static Staff CreateStaff(string idm, string name) => new Staff
    {
        StaffIdm = idm,
        Name = name
    };

    private void TryDeleteDatabaseFiles()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-journal", _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 後始末の失敗はテスト結果に影響させない
            }
        }
    }

    /// <summary>
    /// 独立した SQLite 接続で BEGIN IMMEDIATE を保持する（＝他 PC が書き込み中の状態）
    /// </summary>
    private sealed class WriteLockHolder : IDisposable
    {
        private readonly SQLiteConnection _connection;
        private SQLiteTransaction _transaction;
        private int _released;

        public WriteLockHolder(string dbPath)
        {
            _connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _connection.Open();

            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 0;";
                pragma.ExecuteNonQuery();
            }

            _transaction = _connection.BeginTransaction();

            // 書き込みを 1 文発行して RESERVED ロックを確実に取る（＝他 PC が書き込み中の状態）
            using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText =
                "INSERT OR REPLACE INTO settings (key, value) VALUES ('__insert_retry_test_lock__', '1')";
            command.ExecuteNonQuery();
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                _transaction?.Rollback();
            }
            catch (SQLiteException)
            {
                // 解放時の失敗はテスト対象ではない
            }

            _transaction?.Dispose();
            _transaction = null;
            _connection.Close();
            _connection.Dispose();
        }

        public void Dispose() => Release();
    }

    #endregion
}
