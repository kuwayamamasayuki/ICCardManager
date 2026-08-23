using System;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// ロールバック自体が失敗しても、失敗ログと <see cref="MigrationException"/> への
    /// ラップが実行されること（Issue #1745 / #1831 の B 群）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ApplyMigration</c> / <c>RollbackMigration</c> の <c>Rollback()</c> は
    /// <c>LogMigrationAction(…, success: false, …)</c> と <c>MigrationException</c> への
    /// ラップ<b>より前</b>にある。素の <c>Rollback()</c> が二次例外で抜けると、
    /// 失敗ログが <c>operation_log</c> に残らず、例外も <see cref="MigrationException"/> に
    /// ならない。マイグレーションの失敗はアプリ起動そのものに関わるため、
    /// 「起動できないのに原因を追う手掛かりが無い」状態になる。
    /// </para>
    /// <para>
    /// <c>COMMIT</c> 失敗後・接続断後と同じ状態（トランザクションが既に無効化されている）を、
    /// マイグレーション本体の中でテスト側から <c>Rollback()</c> して再現する。
    /// </para>
    /// </remarks>
    public class MigrationRunnerRollbackFailureTests : IDisposable
    {
        private readonly SQLiteConnection _connection;

        public MigrationRunnerRollbackFailureTests()
        {
            _connection = new SQLiteConnection("Data Source=:memory:");
            _connection.Open();

            using var tx = _connection.BeginTransaction();
            new Migration_001_Initial().Up(_connection, tx);
            tx.Commit();
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void ApplyMigration_ロールバックも失敗_MigrationExceptionのまま抜けること()
        {
            // Arrange: 適用の途中でトランザクションを巻き戻してから SQLITE_BUSY を投げる
            //（以降 SUT 側の Rollback() は InvalidOperationException になる）
            var runner = new MigrationRunner(
                _connection, new IMigration[] { new RollbackThenFailMigration() });

            // Act
            Action act = () => runner.ApplyMigration(new RollbackThenFailMigration());

            // Assert
            act.Should().Throw<MigrationException>(
                    "ロールバックの二次例外が本来の失敗要因を置き換えると、MigrationException への" +
                    "ラップも失敗ログも実行されないまま InvalidOperationException が抜ける")
                .WithInnerException<SQLiteException>(
                    "原因（SQLITE_BUSY）が内部例外として保たれること");
        }

        [Fact]
        public void ApplyMigration_ロールバックも失敗_失敗ログが記録されること()
        {
            var runner = new MigrationRunner(
                _connection, new IMigration[] { new RollbackThenFailMigration() });

            try
            {
                runner.ApplyMigration(new RollbackThenFailMigration());
            }
            catch (MigrationException)
            {
                // 期待どおりの失敗。ログの記録を検証する
            }

            CountFailureLogs().Should().BeGreaterThan(0,
                "Rollback() の二次例外で抜けると、この LogMigrationAction が一度も実行されない");
        }

        private int CountFailureLogs()
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM operation_log " +
                "WHERE action = 'MIGRATION_UP' AND after_data LIKE '%\"status\":\"failed\"%'";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// トランザクションを先に巻き戻してから SQLITE_BUSY を投げるテスト用マイグレーション
        /// </summary>
        private sealed class RollbackThenFailMigration : IMigration
        {
            public int Version => 999;

            public string Description => "ロールバック失敗の再現用";

            public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
            {
                transaction.Rollback();
                throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
            }

            public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
            {
                transaction.Rollback();
                throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
            }
        }
    }
}
