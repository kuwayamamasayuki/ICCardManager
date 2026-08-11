using FluentAssertions;
using ICCardManager.Data.Migrations;
using System.Data.SQLite;
using Xunit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Data.Migrations;

/// <summary>
/// MigrationRunnerのテスト
/// </summary>
public class MigrationRunnerTests : IDisposable
{
    private readonly SQLiteConnection _connection;

    public MigrationRunnerTests()
    {
        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetCurrentVersion_NewDatabase_ReturnsZero()
    {
        // Arrange
        var runner = new MigrationRunner(_connection, Array.Empty<IMigration>());

        // Act
        var version = runner.GetCurrentVersion();

        // Assert
        version.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateToLatest_WithSingleMigration_AppliesMigration()
    {
        // Arrange
        var migration = new TestMigration(1, "テストマイグレーション");
        var runner = new MigrationRunner(_connection, new[] { migration });

        // Act
        var appliedCount = runner.MigrateToLatest();

        // Assert
        appliedCount.Should().Be(1);
        runner.GetCurrentVersion().Should().Be(1);
        migration.UpCalled.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateToLatest_WithMultipleMigrations_AppliesAllInOrder()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2"),
            new TestMigration(3, "マイグレーション3")
        };
        var runner = new MigrationRunner(_connection, migrations);

        // Act
        var appliedCount = runner.MigrateToLatest();

        // Assert
        appliedCount.Should().Be(3);
        runner.GetCurrentVersion().Should().Be(3);
        migrations.All(m => m.UpCalled).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateToLatest_AlreadyAtLatest_AppliesNothing()
    {
        // Arrange
        var migration = new TestMigration(1, "テストマイグレーション");
        var runner = new MigrationRunner(_connection, new[] { migration });
        runner.MigrateToLatest(); // 1回目の適用
        migration.ResetCalls();

        // Act
        var appliedCount = runner.MigrateToLatest(); // 2回目

        // Assert
        appliedCount.Should().Be(0);
        migration.UpCalled.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateTo_SpecificVersion_AppliesUpToThatVersion()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2"),
            new TestMigration(3, "マイグレーション3")
        };
        var runner = new MigrationRunner(_connection, migrations);

        // Act
        var appliedCount = runner.MigrateTo(2);

        // Assert
        appliedCount.Should().Be(2);
        runner.GetCurrentVersion().Should().Be(2);
        migrations[0].UpCalled.Should().BeTrue();
        migrations[1].UpCalled.Should().BeTrue();
        migrations[2].UpCalled.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateTo_LowerVersion_RollsBackMigrations()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2"),
            new TestMigration(3, "マイグレーション3")
        };
        var runner = new MigrationRunner(_connection, migrations);
        runner.MigrateToLatest();
        foreach (var m in migrations) m.ResetCalls();

        // Act
        var rollbackCount = runner.MigrateTo(1);

        // Assert
        rollbackCount.Should().Be(2);
        runner.GetCurrentVersion().Should().Be(1);
        migrations[2].DownCalled.Should().BeTrue();
        migrations[1].DownCalled.Should().BeTrue();
        migrations[0].DownCalled.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetAppliedMigrations_ReturnsAppliedMigrationInfo()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2")
        };
        var runner = new MigrationRunner(_connection, migrations);
        runner.MigrateToLatest();

        // Act
        var applied = runner.GetAppliedMigrations();

        // Assert
        applied.Should().HaveCount(2);
        applied[0].Version.Should().Be(1);
        applied[0].Description.Should().Be("マイグレーション1");
        applied[1].Version.Should().Be(2);
        applied[1].Description.Should().Be("マイグレーション2");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HasPendingMigrations_WithPendingMigrations_ReturnsTrue()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2")
        };
        var runner = new MigrationRunner(_connection, migrations);
        runner.MigrateTo(1);

        // Act
        var hasPending = runner.HasPendingMigrations();

        // Assert
        hasPending.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HasPendingMigrations_AllApplied_ReturnsFalse()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2")
        };
        var runner = new MigrationRunner(_connection, migrations);
        runner.MigrateToLatest();

        // Act
        var hasPending = runner.HasPendingMigrations();

        // Assert
        hasPending.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateToLatest_MigrationFails_RollsBackAndThrows()
    {
        // Arrange
        var migrations = new IMigration[]
        {
            new TestMigration(1, "成功するマイグレーション"),
            new FailingMigration(2, "失敗するマイグレーション")
        };
        var runner = new MigrationRunner(_connection, migrations);

        // Act
        Action act = () => runner.MigrateToLatest();

        // Assert
        act.Should().Throw<MigrationException>()
            .WithMessage("*マイグレーション 2 の適用に失敗しました*");
        runner.GetCurrentVersion().Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrationRecordsAppliedAt_Timestamp()
    {
        // Arrange
        var migration = new TestMigration(1, "テストマイグレーション");
        var runner = new MigrationRunner(_connection, new[] { migration });
        var beforeApply = DateTime.Now.AddSeconds(-1);

        // Act
        runner.MigrateToLatest();
        var applied = runner.GetAppliedMigrations();

        // Assert
        applied[0].AppliedAt.Should().BeAfter(beforeApply);
        applied[0].AppliedAt.Should().BeBefore(DateTime.Now.AddSeconds(1));
    }

    // ===== 新機能のテスト =====

    [Fact]
    [Trait("Category", "Unit")]
    public void GetPendingMigrations_ReturnsPendingMigrationsInOrder()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2"),
            new TestMigration(3, "マイグレーション3")
        };
        var runner = new MigrationRunner(_connection, migrations);
        runner.MigrateTo(1);

        // Act
        var pending = runner.GetPendingMigrations();

        // Assert
        pending.Should().HaveCount(2);
        pending[0].Version.Should().Be(2);
        pending[1].Version.Should().Be(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetPendingMigrations_AllApplied_ReturnsEmpty()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2")
        };
        var runner = new MigrationRunner(_connection, migrations);
        runner.MigrateToLatest();

        // Act
        var pending = runner.GetPendingMigrations();

        // Assert
        pending.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateMigrationSequence_ValidSequence_DoesNotThrow()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(2, "マイグレーション2"),
            new TestMigration(3, "マイグレーション3")
        };
        var runner = new MigrationRunner(_connection, migrations);

        // Act & Assert
        var act = () => runner.ValidateMigrationSequence();
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateMigrationSequence_GapInVersions_ThrowsException()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1"),
            new TestMigration(3, "マイグレーション3") // バージョン2が欠落
        };
        var runner = new MigrationRunner(_connection, migrations);

        // Act & Assert
        var act = () => runner.ValidateMigrationSequence();
        act.Should().Throw<MigrationException>()
            .WithMessage("*バージョン2が見つかりません*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateMigrationSequence_NotStartingWith1_ThrowsException()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(2, "マイグレーション2"), // バージョン1から開始していない
            new TestMigration(3, "マイグレーション3")
        };
        var runner = new MigrationRunner(_connection, migrations);

        // Act & Assert
        var act = () => runner.ValidateMigrationSequence();
        act.Should().Throw<MigrationException>()
            .WithMessage("*バージョン1から開始する必要があります*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateMigrationSequence_DuplicateVersions_ThrowsException()
    {
        // Arrange
        var migrations = new[]
        {
            new TestMigration(1, "マイグレーション1-A"),
            new TestMigration(1, "マイグレーション1-B"), // 重複
            new TestMigration(2, "マイグレーション2")
        };
        var runner = new MigrationRunner(_connection, migrations);

        // Act & Assert
        var act = () => runner.ValidateMigrationSequence();
        act.Should().Throw<MigrationException>()
            .WithMessage("*重複しています*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateMigrationSequence_EmptyMigrations_DoesNotThrow()
    {
        // Arrange
        var runner = new MigrationRunner(_connection, Array.Empty<IMigration>());

        // Act & Assert
        var act = () => runner.ValidateMigrationSequence();
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateToLatest_WithOperationLogTable_LogsMigration()
    {
        // Arrange - operation_logテーブルを作成
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE operation_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT DEFAULT CURRENT_TIMESTAMP,
    operator_idm TEXT NOT NULL,
    operator_name TEXT NOT NULL,
    target_table TEXT,
    target_id TEXT,
    action TEXT,
    before_data TEXT,
    after_data TEXT
)";
            cmd.ExecuteNonQuery();
        }

        var migration = new TestMigration(1, "テストマイグレーション");
        var runner = new MigrationRunner(_connection, new[] { migration });

        // Act
        runner.MigrateToLatest();

        // Assert - ログが記録されていることを確認
        using var checkCmd = _connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM operation_log WHERE action = 'MIGRATION_UP'";
        var logCount = Convert.ToInt32(checkCmd.ExecuteScalar());
        logCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateToLatest_WithoutOperationLogTable_DoesNotFail()
    {
        // Arrange - operation_logテーブルなし
        var migration = new TestMigration(1, "テストマイグレーション");
        var runner = new MigrationRunner(_connection, new[] { migration });

        // Act & Assert - エラーなく実行できること
        var act = () => runner.MigrateToLatest();
        act.Should().NotThrow();
        runner.GetCurrentVersion().Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MigrateToLatest_OperationLogのtimestampがローカル時刻で記録される_Issue1014()
    {
        // Arrange - operation_logテーブルを事前に作成
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE operation_log (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp     TEXT DEFAULT CURRENT_TIMESTAMP,
                operator_idm  TEXT NOT NULL,
                operator_name TEXT NOT NULL,
                target_table  TEXT,
                target_id     TEXT,
                action        TEXT,
                before_data   TEXT,
                after_data    TEXT
            )";
            cmd.ExecuteNonQuery();
        }

        var beforeMigrate = DateTime.Now;

        var migration = new TestMigration(1, "タイムスタンプテスト");
        var runner = new MigrationRunner(_connection, new[] { migration });

        // Act
        runner.MigrateToLatest();

        var afterMigrate = DateTime.Now;

        // Assert: operation_logに記録されたtimestampを検証
        using var selectCmd = _connection.CreateCommand();
        selectCmd.CommandText = "SELECT timestamp FROM operation_log WHERE operator_name = 'MigrationRunner' LIMIT 1";
        var timestampStr = (string)selectCmd.ExecuteScalar();

        timestampStr.Should().NotBeNull();
        var timestamp = DateTime.Parse(timestampStr);

        // ローカル時刻の前後範囲内であることを検証
        // UTCで保存されていた場合、JST環境では9時間ずれるためこの範囲に入らない
        timestamp.Should().BeOnOrAfter(beforeMigrate.AddSeconds(-1));
        timestamp.Should().BeOnOrBefore(afterMigrate.AddSeconds(1));
    }

    // ===== Issue #1738: 共有モードで複数PCが同時起動したときの適用記録の衝突 =====

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyMigration_他PCが同じバージョンを先に記録済みでも例外にならない_Issue1738()
    {
        // Issue #1738: 共有DBを使う2台がほぼ同時に起動すると、双方が MigrateTo 冒頭の
        // GetCurrentVersion() で同じ版数を読み、同じマイグレーションを適用しようとする。
        // ApplyMigration の BeginTransaction() は BEGIN IMMEDIATE のため、後発PCは
        // 先発PCの COMMIT を待ってから Up()（規約により冪等）を再実行し、適用記録の
        // INSERT で PRIMARY KEY と衝突していた。この例外は MigrationException として
        // App.OnStartup の汎用 catch まで到達し、後発PCは「起動エラーが発生しました」
        // ダイアログ + Shutdown(1) でアプリを起動できなかった。
        //
        // 「後発PCが陳腐化したスナップショットで適用する」状態を決定的に再現するため、
        // MigrateTo のスキップ判定を経由せず ApplyMigration を直接呼ぶ（Issue #1484 の
        // BackfillLegacyMigrationVersion1 を直接2回呼ぶテストと同じ手法）。
        var databasePath = CreateTempDatabasePath();
        try
        {
            using var firstPcConnection = OpenSharedDatabase(databasePath);
            using var secondPcConnection = OpenSharedDatabase(databasePath);

            // Arrange: 先発PCがマイグレーション1を適用してコミット済み
            var firstPcMigration = new TestMigration(1, "先発PCが適用したスキーマ更新");
            new MigrationRunner(firstPcConnection, new[] { firstPcMigration }).MigrateToLatest();

            var secondPcMigration = new TestMigration(1, "後発PCが適用したスキーマ更新");
            var secondPcRunner = new MigrationRunner(secondPcConnection, new[] { secondPcMigration });

            // Act: 後発PCが同じバージョンを適用する
            Action act = () => secondPcRunner.ApplyMigration(secondPcMigration);

            // Assert
            act.Should().NotThrow("適用記録が既にあっても後発PCは起動できなければならない");
            secondPcMigration.UpCalled.Should().BeTrue("Up() は冪等前提で再実行される");
            secondPcRunner.GetCurrentVersion().Should().Be(1);
            CountMigrationRecords(secondPcConnection, version: 1)
                .Should().Be(1, "適用記録が二重に増えてはならない");
            ReadMigrationDescription(secondPcConnection, version: 1)
                .Should().Be("先発PCが適用したスキーマ更新",
                    "INSERT OR IGNORE は先に記録された行を上書きしない");
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApplyMigration_適用記録が制約違反で保存できない場合は例外を投げる_Issue1738()
    {
        // Issue #1738: 適用記録の INSERT を OR IGNORE にすると、PRIMARY KEY 衝突
        //（＝他PCが先に記録した正常系）だけでなく NOT NULL 等の本物の制約違反まで
        // 握りつぶされる。記録が入らないまま成功扱いにすると GetCurrentVersion() が
        // 永久に上がらず、毎回の起動で同じ Up() が再適用され続ける無言の劣化になる。
        // そのため「行が書けなかったのか、他PCが先に書いたのか」を読み戻して確定させる。
        var migration = new NullDescriptionMigration(1);
        var runner = new MigrationRunner(_connection, new IMigration[] { migration });

        // Act
        Action act = () => runner.MigrateToLatest();

        // Assert
        act.Should().Throw<MigrationException>()
            .WithMessage("*マイグレーション 1 の適用に失敗しました*")
            .WithInnerException<MigrationException>()
            .WithMessage("*適用記録*schema_migrations*");
        runner.GetCurrentVersion().Should().Be(0, "記録できなかったマイグレーションは適用済みにしない");
    }

    /// <summary>
    /// 共有フォルダ上のDBを模したファイルDBへの接続を開く（Issue #1738）。
    /// 本番の <c>DbContext.ConfigurePragmas</c> と同じく busy_timeout と
    /// journal_mode=DELETE を設定し、BEGIN IMMEDIATE の競合が待機で解決される状態にする。
    /// </summary>
    private static SQLiteConnection OpenSharedDatabase(string databasePath)
    {
        var connection = new SQLiteConnection($"Data Source={databasePath};Version=3;");
        connection.Open();

        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA busy_timeout = 15000; PRAGMA journal_mode = DELETE;";
        pragmaCommand.ExecuteNonQuery();

        return connection;
    }

    private static string CreateTempDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"iccard_migration_race_{Guid.NewGuid():N}.db");

    private static void DeleteTempDatabase(string databasePath)
    {
        SQLiteConnection.ClearAllPools();

        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
        catch (IOException)
        {
            // 一時ファイルの後始末失敗はテスト結果に影響させない
        }
    }

    private static int CountMigrationRecords(SQLiteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = @version";
        command.Parameters.AddWithValue("@version", version);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ReadMigrationDescription(SQLiteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT description FROM schema_migrations WHERE version = @version";
        command.Parameters.AddWithValue("@version", version);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// テスト用マイグレーション
    /// </summary>
    private class TestMigration : IMigration
    {
        public int Version { get; }
        public string Description { get; }
        public bool UpCalled { get; private set; }
        public bool DownCalled { get; private set; }

        public TestMigration(int version, string description)
        {
            Version = version;
            Description = description;
        }

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            UpCalled = true;
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            DownCalled = true;
        }

        public void ResetCalls()
        {
            UpCalled = false;
            DownCalled = false;
        }
    }

    /// <summary>
    /// 失敗するテスト用マイグレーション
    /// </summary>
    private class FailingMigration : IMigration
    {
        public int Version { get; }
        public string Description { get; }

        public FailingMigration(int version, string description)
        {
            Version = version;
            Description = description;
        }

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            throw new InvalidOperationException("マイグレーション失敗");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // ダウングレードは成功する
        }
    }

    /// <summary>
    /// 適用記録の INSERT が制約違反になるテスト用マイグレーション（Issue #1738）。
    /// <c>schema_migrations.description</c> は NOT NULL のため、
    /// INSERT OR IGNORE が本物の制約違反まで握りつぶす状況を作る。
    /// </summary>
    private class NullDescriptionMigration : IMigration
    {
        public int Version { get; }
        public string Description => null;

        public NullDescriptionMigration(int version)
        {
            Version = version;
        }

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // スキーマ変更は行わない（検証対象は適用記録の書き込みのみ）
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
        }
    }
}
