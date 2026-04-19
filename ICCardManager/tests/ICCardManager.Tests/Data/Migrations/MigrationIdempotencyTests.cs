using System;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #1285: 全マイグレーションの Up() が二重実行に対して安全（冪等）であることを検証。
    /// </summary>
    /// <remarks>
    /// MigrationRunner は schema_migrations でバージョン管理するが、共有モードでの競合や
    /// 部分適用状態を想定し、各マイグレーション自体の Up() が二重適用でも例外を投げないことを担保する。
    /// テストは MigrationRunner を介さず、Migration のインスタンスを作って直接 Up() を 2 回呼ぶ。
    /// </remarks>
    public class MigrationIdempotencyTests : IDisposable
    {
        private readonly SQLiteConnection _connection;

        public MigrationIdempotencyTests()
        {
            _connection = new SQLiteConnection("Data Source=:memory:");
            _connection.Open();
            // Initial migration で土台となる全テーブルを作成
            RunMigrationOnce(new Migration_001_Initial());
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        private void RunMigrationOnce(IMigration migration)
        {
            using var tx = _connection.BeginTransaction();
            migration.Up(_connection, tx);
            tx.Commit();
        }

        private Action RunMigrationTwice(IMigration migration)
        {
            return () =>
            {
                using (var tx1 = _connection.BeginTransaction())
                {
                    migration.Up(_connection, tx1);
                    tx1.Commit();
                }
                using (var tx2 = _connection.BeginTransaction())
                {
                    migration.Up(_connection, tx2);
                    tx2.Commit();
                }
            };
        }

        [Fact]
        public void Migration_001_Initial_Up_IsIdempotent()
        {
            // コンストラクタで既に一度適用済み。追加で 1 回実行しても例外が出ないこと
            using var tx = _connection.BeginTransaction();
            Action act = () => new Migration_001_Initial().Up(_connection, tx);
            act.Should().NotThrow();
            tx.Commit();
        }

        [Fact]
        public void Migration_002_AddPointRedemption_Up_IsIdempotent()
        {
            Action act = RunMigrationTwice(new Migration_002_AddPointRedemption());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_003_AddTripGroupId_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            Action act = RunMigrationTwice(new Migration_003_AddTripGroupId());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_004_AddPerformanceIndexes_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            Action act = RunMigrationTwice(new Migration_004_AddPerformanceIndexes());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_005_AddStartingPageNumber_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            Action act = RunMigrationTwice(new Migration_005_AddStartingPageNumber());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_006_AddRefundedStatus_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            Action act = RunMigrationTwice(new Migration_006_AddRefundedStatus());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_007_AddMergeHistory_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            RunMigrationOnce(new Migration_006_AddRefundedStatus());
            Action act = RunMigrationTwice(new Migration_007_AddMergeHistory());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_008_AddCardTypeNumberUniqueIndex_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            RunMigrationOnce(new Migration_006_AddRefundedStatus());
            RunMigrationOnce(new Migration_007_AddMergeHistory());
            Action act = RunMigrationTwice(new Migration_008_AddCardTypeNumberUniqueIndex());
            act.Should().NotThrow();
        }

        [Fact]
        public void Migration_009_AddCarryoverTotals_Up_IsIdempotent()
        {
            RunMigrationOnce(new Migration_002_AddPointRedemption());
            RunMigrationOnce(new Migration_003_AddTripGroupId());
            RunMigrationOnce(new Migration_004_AddPerformanceIndexes());
            RunMigrationOnce(new Migration_005_AddStartingPageNumber());
            RunMigrationOnce(new Migration_006_AddRefundedStatus());
            RunMigrationOnce(new Migration_007_AddMergeHistory());
            RunMigrationOnce(new Migration_008_AddCardTypeNumberUniqueIndex());
            Action act = RunMigrationTwice(new Migration_009_AddCarryoverTotals());
            act.Should().NotThrow();
        }
    }
}
