using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;


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
        using var lease = _dbContext.LeaseConnection();
        var connection = lease.Connection;
        TableShouldExist(connection, "staff");
        TableShouldExist(connection, "ic_card");
        TableShouldExist(connection, "ledger");
        TableShouldExist(connection, "ledger_detail");
        TableShouldExist(connection, "operation_log");
        TableShouldExist(connection, "settings");
        TableShouldExist(connection, "schema_migrations");
        TableShouldExist(connection, "ledger_merge_history");

        // Issue #2331: UT-031 No.2 は「全テーブル存在」を約束する。列挙だけだと Migration_007 の
        // ledger_merge_history のように、後から足したテーブルの漏れに気付けないため、
        // 作られたテーブルの集合そのものを期待値と突き合わせる（足したら本テストの期待値も足す）。
        var actualTables = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                actualTables.Add(reader.GetString(0));
            }
        }

        actualTables.Should().BeEquivalentTo(
            new[] { "staff", "ic_card", "ledger", "ledger_detail", "operation_log", "settings", "schema_migrations", "ledger_merge_history" },
            "初期化後のテーブルは 02_DB設計書のテーブル一覧と schema_migrations に一致すること（作業用の一時テーブルを残さないことも含む）");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InitializeDatabase_LegacyDatabase_RecordsAsVersion1()
    {
        // Arrange - 既存DBをシミュレート（schema_migrationsなしでstaffテーブルあり）
        _dbContext = new DbContext(":memory:");

        // 既存DBの状態を作成（マイグレーション前の形式）
        // Note: レガシーDBでも ledger_detail と operation_log は存在していた
        using (var setupLease = _dbContext.LeaseConnection())
        {
            var connection = setupLease.Connection;
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
        }

        // Act
        _dbContext.InitializeDatabase();

        // Assert
        using var lease = _dbContext.LeaseConnection();
        var conn = lease.Connection;
        // レガシーDBはバージョン1として認識され、その後 Migration_002〜014 も適用されるので最終バージョンは 14
        _dbContext.GetDatabaseVersion().Should().Be(14);
        TableShouldExist(conn, "schema_migrations");

        // バージョン1（既存DB認識）の記録が存在することを確認
        using var checkCmd = conn.CreateCommand();
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
        using var lease = _dbContext.LeaseConnection();
        var connection = lease.Connection;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations";
        var count = Convert.ToInt32(cmd.ExecuteScalar());

        // 現在のマイグレーション数と一致するはず（重複していないこと）
        // Migration_001 〜 Migration_014 = 14
        count.Should().Be(14);
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
    public void BackfillLegacyMigrationVersion1_CalledTwice_DoesNotThrowAndKeepsSingleRow()
    {
        // Issue #1484: 共有モードで複数 PC が同時にレガシー DB を起動した際の
        // TOCTOU 競合（テーブル存在チェック直後に他 PC が補填を完了させるレース）を再現する。
        // BackfillLegacyMigrationVersion1 は内部的に INSERT OR IGNORE を使うため、
        // 同じ接続で 2 回呼んでも UNIQUE 制約違反にならず、version=1 行は 1 件のまま維持される。
        _dbContext = new DbContext(":memory:");

        using var lease = _dbContext.LeaseConnection();
        var connection = lease.Connection;

        // Act: 補填処理を 2 回連続で実行（PC1 が補填完了直後に PC2 が同じ INSERT を投げる状況）
        DbContext.BackfillLegacyMigrationVersion1(connection);
        Action secondCall = () => DbContext.BackfillLegacyMigrationVersion1(connection);

        // Assert
        secondCall.Should().NotThrow("schema_migrations の補填 INSERT は冪等であるべき");

        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 1";
        Convert.ToInt32(countCmd.ExecuteScalar()).Should().Be(1, "version=1 の補填行は 1 件のみ存在すべき");
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
        using var lease = _dbContext.LeaseConnection();
        var connection = lease.Connection;
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
    /// <summary>
    /// Issue #2371（レビュー 5 回目）: DbContext に渡した起動時の組織設定の文言（<c>InitialRecordSummaryText</c>）が、
    /// MigrationRunner を通ってマイグレーション 013 の埋め戻しまで届くこと。配線（DbContext → MigrationRunner）を外すと、
    /// 文言を変えた組織の変更後の文言の導入行が埋まらない。
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void InitializeDatabase_PassesCurrentSummaryTextToMigration013(bool configured)
    {
        // Arrange: 版 12 まで適用した DB に、変更後の文言（"前年より持越"）の導入行を入れておく
        _dbContext = new DbContext(":memory:");
        if (configured)
        {
            _dbContext.InitialRecordSummaryText = new ICCardManager.Services.SummaryTextOptions { CarryoverFromPreviousYear = "前年より持越" };
        }

        using (var lease = _dbContext.LeaseConnection())
        {
            var migrations = new ICCardManager.Data.Migrations.IMigration[]
            {
                new ICCardManager.Data.Migrations.Migration_001_Initial(),
                new ICCardManager.Data.Migrations.Migration_002_AddPointRedemption(),
                new ICCardManager.Data.Migrations.Migration_003_AddTripGroupId(),
                new ICCardManager.Data.Migrations.Migration_004_AddPerformanceIndexes(),
                new ICCardManager.Data.Migrations.Migration_005_AddStartingPageNumber(),
                new ICCardManager.Data.Migrations.Migration_006_AddRefundedStatus(),
                new ICCardManager.Data.Migrations.Migration_007_AddMergeHistory(),
                new ICCardManager.Data.Migrations.Migration_008_AddCardTypeNumberUniqueIndex(),
                new ICCardManager.Data.Migrations.Migration_009_AddCarryoverTotals(),
                new ICCardManager.Data.Migrations.Migration_010_AddCompanionCount(),
                new ICCardManager.Data.Migrations.Migration_011_AddLedgerDetailId(),
                new ICCardManager.Data.Migrations.Migration_012_CardUniqueIndexCoversNullIsDeleted(),
            };
            new ICCardManager.Data.Migrations.MigrationRunner(lease.Connection, migrations).MigrateToLatest();

            using var command = lease.Connection.CreateCommand();
            command.CommandText = @"INSERT INTO ic_card (card_idm, card_type, card_number) VALUES ('AAAA000000000001', 'はやかけん', 'A-001');
INSERT INTO ledger (card_idm, date, summary, income, expense, balance) VALUES ('AAAA000000000001', '2026-04-01 00:00:00', '前年より持越', 5000, 0, 5000);";
            command.ExecuteNonQuery();
        }

        // Act: 起動時の初期化（残りの版 13 を適用する）
        _dbContext.InitializeDatabase();

        // Assert
        using var lease2 = _dbContext.LeaseConnection();
        using var select = lease2.Connection.CreateCommand();
        select.CommandText = "SELECT initial_record_kind FROM ledger";
        var kind = select.ExecuteScalar();
        if (configured)
        {
            Convert.ToInt64(kind, System.Globalization.CultureInfo.InvariantCulture).Should().Be(3, "現在の文言が移行まで届き、変更後の文言の導入行も埋まること");
        }
        else
        {
            kind.Should().Be(DBNull.Value, "文言を渡さなければ既定の文言だけと照合する（対照）");
        }
    }
}
