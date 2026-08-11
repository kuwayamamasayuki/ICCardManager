using System;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1737: <see cref="SettingsRepository.SetAsync"/> の書き込みが、
/// 同一接続上で進行中の他コンポーネントのトランザクションに巻き込まれないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前の <c>SetAsync</c> は <see cref="DbContext.LeaseConnectionAsync"/>（セマフォを取らない）で
/// INSERT を発行していた。SQLite のトランザクションは接続単位のため、
/// <c>CleanupOldData</c> が開いているトランザクションの内側にこの INSERT が潜り込み、
/// cleanup が SQLITE_BUSY 等でロールバックすると
/// <c>last_backup_success_at</c> の記録ごと消えていた。
/// </para>
/// <para>
/// 外側の保持側は実際の <c>CleanupOldDataInternal</c> と同じ形
/// （同期 <see cref="DbContext.LeaseConnection"/> ＋ <c>connection.BeginTransaction()</c>）で再現する。
/// <see cref="DbContext.BeginTransactionAsync"/> で代用すると
/// <c>HasActiveTransactionScope</c> が立ち、実際とは別の分岐を通ってしまうため。
/// </para>
/// </remarks>
public class SettingsRepositorySetConcurrencyTests : IDisposable
{
    private const string TestKey = SettingsRepository.KeyLastBackupSuccessAt;
    private const string TestValue = "2026-08-10 09:30:00";

    /// <summary>待機の観測に使う猶予。DB 書き込み自体はインメモリのため数 ms で終わる。</summary>
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>デッドロック検出用のタイムアウト。実処理はミリ秒単位で終わる。</summary>
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(10);

    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly SettingsRepository _repository;

    public SettingsRepositorySetConcurrencyTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheServiceMock = new Mock<ICacheService>();
        _repository = new SettingsRepository(
            _dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SetAsync_他コンポーネントがトランザクション保持中は書き込みを待機すること()
    {
        // Arrange: CleanupOldData 相当がセマフォとトランザクションを保持したまま滞留する
        using var cleanup = new CleanupSimulator(_dbContext);
        await cleanup.StartAndWaitUntilTransactionOpenAsync();

        // Act
        var setTask = _repository.SetAsync(TestKey, TestValue);
        await Task.WhenAny(setTask, Task.Delay(ObservationWindow));

        // Assert
        setTask.IsCompleted.Should().BeFalse(
            "セマフォ保護が無いと INSERT が cleanup のトランザクション内側で即座に実行され、"
            + "ロールバックで道連れになる（Issue #1737）");

        // Cleanup: 保持を解放して後片付け（アサート後に解放しないと待機の観測にならない）
        await cleanup.RollbackAndCompleteAsync();
        (await setTask).Should().BeTrue();
    }

    [Fact]
    public async Task SetAsync_他コンポーネントのロールバックで書き込みが消えないこと()
    {
        // Arrange
        using var cleanup = new CleanupSimulator(_dbContext);
        await cleanup.StartAndWaitUntilTransactionOpenAsync();

        // Act: 保持中に書き込みを開始し、外側はロールバックで終える
        var setTask = _repository.SetAsync(TestKey, TestValue);
        await Task.WhenAny(setTask, Task.Delay(ObservationWindow));
        await cleanup.RollbackAndCompleteAsync();

        var result = await setTask;

        // Assert: バックアップは実際に成功しているのに記録だけが消える、を防ぐ
        result.Should().BeTrue();
        var stored = await _repository.GetAsync(TestKey);
        stored.Should().Be(TestValue,
            "他コンポーネントのロールバックで settings の書き込みが取り消されてはならない（Issue #1737）");
    }

    [Fact]
    public async Task SetAsync_トランザクションスコープ内から呼び出してもデッドロックしないこと()
    {
        // Arrange: Issue #1575 — BeginTransactionAsync は SemaphoreSlim(1,1) を取るため、
        // すでにスコープが開いている状態で SetAsync が自前で開くと自己デッドロックする。
        using var scope = await _dbContext.BeginTransactionAsync();

        // Act
        var setTask = _repository.SetAsync(TestKey, TestValue);
        var winner = await Task.WhenAny(setTask, Task.Delay(DeadlockTimeout));

        // Assert
        winner.Should().BeSameAs(setTask,
            "外側スコープの内側では接続だけを借りて暗黙参加すること（自前で BeginTransactionAsync を開かない）");
        (await setTask).Should().BeTrue();
        scope.Commit();
    }

    [Fact]
    public async Task SaveAppSettingsAsync_単一トランザクション内の複数SetAsyncが完了すること()
    {
        // Arrange: SaveAppSettingsAsync は 1 つのスコープ内で SetAsync を 10 回以上呼ぶ（Issue #1240）。
        // 実運用で最も入れ子が深くなる経路を丸ごと通し、デッドロックしないことを表明する。
        var settings = new AppSettings
        {
            WarningBalance = 3000,
            FontSize = FontSizeOption.Medium,
            BackupPath = @"D:\Backup",
            SoundMode = SoundMode.VoiceMale,
            ToastPosition = ToastPosition.TopRight,
            DepartmentType = DepartmentType.EnterpriseAccount,
            SkipBusStopInputOnReturn = true,
            ReportOutputFolder = @"C:\Reports",
        };

        // Act
        var saveTask = _repository.SaveAppSettingsAsync(settings);
        var winner = await Task.WhenAny(saveTask, Task.Delay(DeadlockTimeout));

        // Assert
        winner.Should().BeSameAs(saveTask, "設定保存が自己デッドロックしてはならない（Issue #1575 / #1737）");
        (await saveTask).Should().BeTrue();
        (await _repository.GetAsync(SettingsRepository.KeyWarningBalance)).Should().Be("3000");
    }

    /// <summary>
    /// <c>DbContext.CleanupOldDataInternal</c> と同じ形で「セマフォ＋トランザクションを保持したまま滞留する
    /// 別コンポーネント」を再現するヘルパー。
    /// </summary>
    private sealed class CleanupSimulator : IDisposable
    {
        private readonly DbContext _dbContext;
        private readonly TaskCompletionSource<bool> _transactionOpened = new();
        private readonly TaskCompletionSource<bool> _releaseSignal = new();

        /// <summary>
        /// 滞留側の作業タスク。<see cref="StartAndWaitUntilTransactionOpenAsync"/> を呼ぶまでは
        /// 未起動のため null であり得る。
        /// </summary>
        /// <remarks>
        /// Issue #1786: 元は非 Null 許容で宣言しており CS8618 が出ていた。
        /// <c>= null!</c> による初期化は「必ず非 null」とコンパイラに宣言することになり、
        /// 実際に null チェックしている <see cref="RollbackAndCompleteAsync"/> と矛盾するため採らない。
        /// </remarks>
        private Task? _worker;

        public CleanupSimulator(DbContext dbContext) => _dbContext = dbContext;

        public async Task StartAndWaitUntilTransactionOpenAsync()
        {
            _worker = Task.Run(() =>
            {
                // 同期 LeaseConnection がセマフォを取り、トランザクションを開く（CleanupOldDataInternal と同型）
                using var lease = _dbContext.LeaseConnection();
                using var transaction = lease.Connection.BeginTransaction();

                using (var command = lease.Connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM ledger WHERE date(date) < date('now', '-6 years', 'localtime')";
                    command.ExecuteNonQuery();
                }

                _transactionOpened.SetResult(true);
                _releaseSignal.Task.GetAwaiter().GetResult();

                // SQLITE_BUSY 等で cleanup が巻き戻るケースを再現する
                transaction.Rollback();
            });

            await _transactionOpened.Task;
        }

        public async Task RollbackAndCompleteAsync()
        {
            _releaseSignal.TrySetResult(true);
            if (_worker != null)
            {
                await _worker;
            }
        }

        public void Dispose() => _releaseSignal.TrySetResult(true);
    }
}
