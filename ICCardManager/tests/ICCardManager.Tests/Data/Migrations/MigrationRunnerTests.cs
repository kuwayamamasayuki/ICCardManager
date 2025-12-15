using FluentAssertions;
using ICCardManager.Data.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations;

/// <summary>
/// MigrationRunnerのテスト
/// </summary>
public class MigrationRunnerTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MigrationRunnerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
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

        public void Up(SqliteConnection connection, SqliteTransaction transaction)
        {
            UpCalled = true;
        }

        public void Down(SqliteConnection connection, SqliteTransaction transaction)
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

        public void Up(SqliteConnection connection, SqliteTransaction transaction)
        {
            throw new InvalidOperationException("マイグレーション失敗");
        }

        public void Down(SqliteConnection connection, SqliteTransaction transaction)
        {
            // ダウングレードは成功する
        }
    }
}
