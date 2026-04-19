using System;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #1285: MigrationHelpers の冪等 SQL 操作の単体テスト。
    /// </summary>
    public class MigrationHelpersTests : IDisposable
    {
        private readonly SQLiteConnection _connection;

        public MigrationHelpersTests()
        {
            _connection = new SQLiteConnection("Data Source=:memory:");
            _connection.Open();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void HasColumn_ExistingColumn_ReturnsTrue()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx, "t", "name").Should().BeTrue();
        }

        [Fact]
        public void HasColumn_MissingColumn_ReturnsFalse()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx, "t", "nope").Should().BeFalse();
        }

        [Fact]
        public void HasColumn_CaseInsensitive()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx, "t", "NAME").Should().BeTrue();
        }

        [Fact]
        public void HasColumn_InvalidTableName_Throws()
        {
            using var tx = _connection.BeginTransaction();
            Action act = () => MigrationHelpers.HasColumn(_connection, tx, "t; DROP TABLE t;", "x");
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void AddColumnIfNotExists_NewColumn_IsAdded()
        {
            using (var tx = _connection.BeginTransaction())
            {
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra", "INTEGER DEFAULT 0");
                tx.Commit();
            }

            using var tx2 = _connection.BeginTransaction();
            MigrationHelpers.HasColumn(_connection, tx2, "t", "extra").Should().BeTrue();
        }

        [Fact]
        public void AddColumnIfNotExists_ExistingColumn_NoOp()
        {
            using (var tx = _connection.BeginTransaction())
            {
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra", "INTEGER DEFAULT 0");
                tx.Commit();
            }

            using var tx2 = _connection.BeginTransaction();
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx2, "t", "extra", "INTEGER DEFAULT 0");
            act.Should().NotThrow();
            tx2.Commit();
        }

        [Fact]
        public void AddColumnIfNotExists_TwiceInSameTransaction_NoOp()
        {
            using var tx = _connection.BeginTransaction();
            MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra2", "TEXT");
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "extra2", "TEXT");
            act.Should().NotThrow();
            tx.Commit();
        }
    }
}
