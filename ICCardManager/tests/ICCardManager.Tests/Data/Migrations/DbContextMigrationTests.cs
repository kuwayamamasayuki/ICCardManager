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
        // Note: レガシーDBでも ledger_detail と operation_log は存在していた
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
CREATE TABLE ledger_detail (
    ledger_id INTEGER,
    use_date TEXT,
    entry_station TEXT,
    exit_station TEXT,
    bus_stops TEXT,
    amount INTEGER,
    balance INTEGER,
    is_charge INTEGER DEFAULT 0,
    is_bus INTEGER DEFAULT 0
);
CREATE TABLE operation_log (
    id INTEGER PRIMARY KEY,
    timestamp TEXT,
    operator_idm TEXT NOT NULL,
    operator_name TEXT NOT NULL,
    target_table TEXT,
    target_id TEXT,
    action TEXT,
    before_data TEXT,
    after_data TEXT
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
        // レガシーDBはバージョン1として認識され、その後Migration_002〜005も適用されるので最終バージョンは5
        _dbContext.GetDatabaseVersion().Should().Be(5);
        TableShouldExist(connection, "schema_migrations");

        // バージョン1（既存DB認識）の記録が存在することを確認
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

        // 現在のマイグレーション数と一致するはず（重複していないこと）
        // Migration_001 + Migration_002 + Migration_003 + Migration_004 + Migration_005 = 5
        count.Should().Be(5);
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
