using FluentAssertions;
using ICCardManager.Data;
using System.Data.SQLite;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Data.Migrations;

/// <summary>
/// DbContextのマイグレーション関連機能のテスト
/// </summary>
public class DbContextMigrationTests : IDisposable
{
    private DbContext? _dbContext;

    public void Dispose()
    {
        _dbContext?.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InitializeDatabase_NewDatabase_AppliesMigrations()
    {
        // Arrange
        _dbContext = new DbContext(":memory:");

        // Act
        _dbContext.InitializeDatabase();

        // Assert
        _dbContext.GetDatabaseVersion().Should().BeGreaterThan(0);
        _dbContext.HasPendingMigrations().Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InitializeDatabase_NewDatabase_CreatesAllTables()
    {
        // Arrange
        _dbContext = new DbContext(":memory:");

        // Act
        _dbContext.InitializeDatabase();

        // Assert
        var connection = _dbContext.GetConnection();
        TableShouldExist(connection, "staff");
        TableShouldExist(connection, "ic_card");
        TableShouldExist(connection, "ledger");
        TableShouldExist(connection, "ledger_detail");
        TableShouldExist(connection, "operation_log");
        TableShouldExist(connection, "settings");
        TableShouldExist(connection, "schema_migrations");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InitializeDatabase_LegacyDatabase_RecordsAsVersion1()
    {
        // Arrange - 既存DBをシミュレート（schema_migrationsなしでstaffテーブルあり）
        _dbContext = new DbContext(":memory:");
        var connection = _dbContext.GetConnection();

        // 既存DBの状態を作成（マイグレーション前の形式）
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE staff (
    staff_idm TEXT PRIMARY KEY,
    name TEXT NOT NULL
);
CREATE TABLE ic_card (
    card_idm TEXT PRIMARY KEY,
    card_type TEXT NOT NULL,
    card_number TEXT NOT NULL
);
CREATE TABLE ledger (
    id INTEGER PRIMARY KEY,
    card_idm TEXT NOT NULL,
    date TEXT NOT NULL,
    summary TEXT NOT NULL,
    balance INTEGER NOT NULL
);
CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value TEXT
);";
            cmd.ExecuteNonQuery();
        }

        // Act
        _dbContext.InitializeDatabase();

        // Assert
        _dbContext.GetDatabaseVersion().Should().Be(1);
        TableShouldExist(connection, "schema_migrations");

        // マイグレーション記録を確認
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT description FROM schema_migrations WHERE version = 1";
        var description = checkCmd.ExecuteScalar()?.ToString();
        description.Should().Contain("既存DB");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InitializeDatabase_CalledTwice_DoesNotDuplicateMigrations()
    {
        // Arrange
        _dbContext = new DbContext(":memory:");

        // Act
        _dbContext.InitializeDatabase();
        _dbContext.InitializeDatabase(); // 2回目

        // Assert
        var connection = _dbContext.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations";
        var count = Convert.ToInt32(cmd.ExecuteScalar());

        // マイグレーションは1つだけ適用されているはず
        count.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDatabaseVersion_BeforeInitialize_ReturnsZero()
    {
        // Arrange
        _dbContext = new DbContext(":memory:");

        // Act
        var version = _dbContext.GetDatabaseVersion();

        // Assert
        version.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HasPendingMigrations_BeforeInitialize_ReturnsTrue()
    {
        // Arrange
        _dbContext = new DbContext(":memory:");

        // Act
        var hasPending = _dbContext.HasPendingMigrations();

        // Assert
        hasPending.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InitializeDatabase_NewDatabase_InsertsDefaultSettings()
    {
        // Arrange
        _dbContext = new DbContext(":memory:");

        // Act
        _dbContext.InitializeDatabase();

        // Assert
        var connection = _dbContext.GetConnection();
        GetSettingValue(connection, "warning_balance").Should().Be("10000");
        GetSettingValue(connection, "font_size").Should().Be("medium");
    }

    private static void TableShouldExist(SQLiteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'";
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull($"テーブル '{tableName}' が存在するべきです");
    }

    private static string? GetSettingValue(SQLiteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar()?.ToString();
    }
}
