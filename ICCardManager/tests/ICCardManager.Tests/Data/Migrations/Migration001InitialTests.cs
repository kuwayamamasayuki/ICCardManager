using FluentAssertions;
using ICCardManager.Data.Migrations;
using System.Data.SQLite;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Data.Migrations;

/// <summary>
/// Migration_001_Initialのテスト
/// </summary>
public class Migration001InitialTests : IDisposable
{
    private readonly SQLiteConnection _connection;

    public Migration001InitialTests()
    {
        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();

        // 外部キー制約を有効化
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Up_CreatesAllTables()
    {
        // Arrange
        var migration = new Migration_001_Initial();

        // Act
        using var transaction = _connection.BeginTransaction();
        migration.Up(_connection, transaction);
        transaction.Commit();

        // Assert
        TableShouldExist("staff");
        TableShouldExist("ic_card");
        TableShouldExist("ledger");
        TableShouldExist("ledger_detail");
        TableShouldExist("operation_log");
        TableShouldExist("settings");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Up_CreatesAllIndexes()
    {
        // Arrange
        var migration = new Migration_001_Initial();

        // Act
        using var transaction = _connection.BeginTransaction();
        migration.Up(_connection, transaction);
        transaction.Commit();

        // Assert
        IndexShouldExist("idx_staff_deleted");
        IndexShouldExist("idx_card_deleted");
        IndexShouldExist("idx_ledger_date");
        IndexShouldExist("idx_ledger_summary");
        IndexShouldExist("idx_ledger_card_date");
        IndexShouldExist("idx_ledger_lender");
        IndexShouldExist("idx_detail_ledger");
        IndexShouldExist("idx_detail_bus");
        IndexShouldExist("idx_log_timestamp");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Up_InsertsDefaultSettings()
    {
        // Arrange
        var migration = new Migration_001_Initial();

        // Act
        using var transaction = _connection.BeginTransaction();
        migration.Up(_connection, transaction);
        transaction.Commit();

        // Assert
        GetSettingValue("warning_balance").Should().Be("10000");
        GetSettingValue("font_size").Should().Be("medium");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Down_DropsAllTables()
    {
        // Arrange
        var migration = new Migration_001_Initial();
        using (var transaction = _connection.BeginTransaction())
        {
            migration.Up(_connection, transaction);
            transaction.Commit();
        }

        // Act
        using (var transaction = _connection.BeginTransaction())
        {
            migration.Down(_connection, transaction);
            transaction.Commit();
        }

        // Assert
        TableShouldNotExist("staff");
        TableShouldNotExist("ic_card");
        TableShouldNotExist("ledger");
        TableShouldNotExist("ledger_detail");
        TableShouldNotExist("operation_log");
        TableShouldNotExist("settings");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StaffTable_HasCorrectColumns()
    {
        // Arrange
        var migration = new Migration_001_Initial();
        using var transaction = _connection.BeginTransaction();
        migration.Up(_connection, transaction);
        transaction.Commit();

        // Act & Assert
        var columns = GetTableColumns("staff");
        columns.Should().Contain("staff_idm");
        columns.Should().Contain("name");
        columns.Should().Contain("number");
        columns.Should().Contain("note");
        columns.Should().Contain("is_deleted");
        columns.Should().Contain("deleted_at");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IcCardTable_HasCorrectColumns()
    {
        // Arrange
        var migration = new Migration_001_Initial();
        using var transaction = _connection.BeginTransaction();
        migration.Up(_connection, transaction);
        transaction.Commit();

        // Act & Assert
        var columns = GetTableColumns("ic_card");
        columns.Should().Contain("card_idm");
        columns.Should().Contain("card_type");
        columns.Should().Contain("card_number");
        columns.Should().Contain("note");
        columns.Should().Contain("is_deleted");
        columns.Should().Contain("deleted_at");
        columns.Should().Contain("is_lent");
        columns.Should().Contain("last_lent_at");
        columns.Should().Contain("last_lent_staff");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LedgerTable_HasCorrectColumns()
    {
        // Arrange
        var migration = new Migration_001_Initial();
        using var transaction = _connection.BeginTransaction();
        migration.Up(_connection, transaction);
        transaction.Commit();

        // Act & Assert
        var columns = GetTableColumns("ledger");
        columns.Should().Contain("id");
        columns.Should().Contain("card_idm");
        columns.Should().Contain("lender_idm");
        columns.Should().Contain("date");
        columns.Should().Contain("summary");
        columns.Should().Contain("income");
        columns.Should().Contain("expense");
        columns.Should().Contain("balance");
        columns.Should().Contain("staff_name");
        columns.Should().Contain("note");
        columns.Should().Contain("returner_idm");
        columns.Should().Contain("lent_at");
        columns.Should().Contain("returned_at");
        columns.Should().Contain("is_lent_record");
    }

    private void TableShouldExist(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'";
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull($"テーブル '{tableName}' が存在するべきです");
    }

    private void TableShouldNotExist(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'";
        var result = cmd.ExecuteScalar();
        result.Should().BeNull($"テーブル '{tableName}' は削除されているべきです");
    }

    private void IndexShouldExist(string indexName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='index' AND name='{indexName}'";
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull($"インデックス '{indexName}' が存在するべきです");
    }

    private string? GetSettingValue(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar()?.ToString();
    }

    private List<string> GetTableColumns(string tableName)
    {
        var columns = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1)); // name column
        }
        return columns;
    }
}
