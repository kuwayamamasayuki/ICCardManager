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

        // Issue #1466: 引数検証の追加テスト
        // column / typeAndConstraints を regex で防御層化したことを固定する。

        [Theory]
        [InlineData("x; DROP TABLE t;")]
        [InlineData("1col")]
        [InlineData("col-name")]
        [InlineData("col name")]
        [InlineData("col'name")]
        [InlineData("col\"name")]
        [InlineData("")]
        [InlineData(" ")]
        public void HasColumn_InvalidColumnName_Throws(string invalidColumn)
        {
            using var tx = _connection.BeginTransaction();
            Action act = () => MigrationHelpers.HasColumn(_connection, tx, "t", invalidColumn);
            act.Should().Throw<ArgumentException>()
                .Where(e => e.ParamName == "column");
        }

        [Theory]
        [InlineData("1bad")]
        [InlineData("bad-name")]
        [InlineData("a b")]
        [InlineData("a;b")]
        public void HasColumn_InvalidTableName_RegexBased_Throws(string invalidTable)
        {
            using var tx = _connection.BeginTransaction();
            Action act = () => MigrationHelpers.HasColumn(_connection, tx, invalidTable, "name");
            act.Should().Throw<ArgumentException>()
                .Where(e => e.ParamName == "table");
        }

        [Theory]
        [InlineData("x; DROP TABLE t;")]
        [InlineData("1col")]
        [InlineData("col-name")]
        [InlineData("col name")]
        [InlineData("")]
        public void AddColumnIfNotExists_InvalidColumnName_Throws(string invalidColumn)
        {
            using var tx = _connection.BeginTransaction();
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", invalidColumn, "INTEGER");
            act.Should().Throw<ArgumentException>()
                .Where(e => e.ParamName == "column");
        }

        [Theory]
        [InlineData("VARCHAR(100)")]                  // SQLite の標準型外
        [InlineData("INTEGER; DROP TABLE t")]         // セミコロン混入
        [InlineData("TEXT DEFAULT 'a;b'")]            // リテラル内セミコロン拒否
        [InlineData("TEXT DEFAULT 'O''Reilly'")]      // クォート二重化拒否（保守的方針）
        [InlineData("INTEGER UNIQUE")]                // 未対応制約
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("INTEGER DEFAULT abc")]           // 識別子をデフォルトに混入
        public void AddColumnIfNotExists_InvalidTypeAndConstraints_Throws(string invalidType)
        {
            using var tx = _connection.BeginTransaction();
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "newcol", invalidType);
            act.Should().Throw<ArgumentException>()
                .WithMessage("*typeAndConstraints*");
        }

        [Theory]
        [InlineData("INTEGER")]
        [InlineData("INTEGER DEFAULT 0")]
        [InlineData("INTEGER DEFAULT 1")]
        [InlineData("TEXT")]
        [InlineData("REAL NOT NULL")]
        [InlineData("REAL DEFAULT 1.5")]
        [InlineData("TEXT DEFAULT NULL")]
        [InlineData("TEXT DEFAULT 'abc'")]
        [InlineData("INTEGER REFERENCES ic_card(idm)")]
        [InlineData("INTEGER DEFAULT -1")]
        [InlineData("NUMERIC DEFAULT 0")]
        [InlineData("BLOB")]
        public void AddColumnIfNotExists_ValidTypeAndConstraints_Accepts(string validType)
        {
            using var tx = _connection.BeginTransaction();
            // 列名を毎回ユニークにして AddColumn が成功するように
            var colName = "c_" + Math.Abs(validType.GetHashCode());
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", colName, validType);
            act.Should().NotThrow();
            tx.Commit();
        }

        [Fact]
        public void EnsureValidIdentifier_ErrorMessage_ContainsActualValue()
        {
            using var tx = _connection.BeginTransaction();
            Action act = () => MigrationHelpers.HasColumn(_connection, tx, "t", "bad-col");
            act.Should().Throw<ArgumentException>()
                .Where(e => e.Message.Contains("bad-col"))
                .Where(e => e.Message.Contains("識別子"))
                .Where(e => e.Message.Length >= 20);
        }

        [Fact]
        public void EnsureValidTypeAndConstraints_ErrorMessage_ContainsActualValue()
        {
            using var tx = _connection.BeginTransaction();
            Action act = () =>
                MigrationHelpers.AddColumnIfNotExists(_connection, tx, "t", "newcol", "VARCHAR(100)");
            act.Should().Throw<ArgumentException>()
                .Where(e => e.Message.Contains("VARCHAR(100)"))
                .Where(e => e.Message.Length >= 20);
        }
    }
}
