using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1687: 旧バージョンのアプリによる新スキーマDBへの接続ブロックと、
/// settings への min_app_version（要求アプリバージョン）記録を検証する。
/// </summary>
/// <remarks>
/// ブロック判定は「DBの schema_migrations 最大バージョン &gt; アプリの把握する
/// 最大マイグレーションバージョン」。旧バージョンのアプリ実行を直接模擬できないため、
/// DBへ未来のバージョン行（9999）を挿入して「新しいアプリで更新されたDB」を再現する。
/// </remarks>
public class DbContextVersionGuardTests : IDisposable
{
    private readonly string _testDirectory;

    public DbContextVersionGuardTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"VersionGuard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    private string CreateDbPath() => Path.Combine(_testDirectory, $"{Guid.NewGuid():N}.db");

    private static void ExecuteSql(DbContext dbContext, string sql)
    {
        using var lease = dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string QueryScalar(DbContext dbContext, string sql)
    {
        using var lease = dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static string ReadMinAppVersion(DbContext dbContext)
        => QueryScalar(dbContext,
            $"SELECT value FROM settings WHERE key = '{DbContext.MinAppVersionSettingKey}'");

    [Fact]
    public void InitializeDatabase_min_app_versionに自バージョンが記録されること()
    {
        var dbPath = CreateDbPath();
        using var dbContext = new DbContext(dbPath);

        dbContext.InitializeDatabase();

        ReadMinAppVersion(dbContext).Should().Be(
            AppVersionInfo.CurrentString,
            "旧バージョンをブロックした際に表示する要求バージョンとして記録される");
    }

    [Fact]
    public void InitializeDatabase_保存済みのより高いバージョンを下書きしないこと()
    {
        // Arrange - より新しいバージョンのPCが先に記録した状況を模擬
        var dbPath = CreateDbPath();
        using (var first = new DbContext(dbPath))
        {
            first.InitializeDatabase();
            ExecuteSql(first,
                $"UPDATE settings SET value = '99.0.0' WHERE key = '{DbContext.MinAppVersionSettingKey}'");
        }

        // Act - このPC（現行バージョン）が起動
        using var second = new DbContext(dbPath);
        second.InitializeDatabase();

        // Assert
        ReadMinAppVersion(second).Should().Be("99.0.0", "引き上げのみで下げない（複数PC運用）");
    }

    [Fact]
    public void InitializeDatabase_保存済みのより低いバージョンは引き上げること()
    {
        var dbPath = CreateDbPath();
        using (var first = new DbContext(dbPath))
        {
            first.InitializeDatabase();
            ExecuteSql(first,
                $"UPDATE settings SET value = '0.0.1' WHERE key = '{DbContext.MinAppVersionSettingKey}'");
        }

        using var second = new DbContext(dbPath);
        second.InitializeDatabase();

        ReadMinAppVersion(second).Should().Be(AppVersionInfo.CurrentString);
    }

    [Fact]
    public void InitializeDatabase_DBスキーマがアプリより新しい場合はブロックすること()
    {
        // Arrange - 新しいアプリがマイグレーション9999を適用済みのDBを模擬
        var dbPath = CreateDbPath();
        using (var newer = new DbContext(dbPath))
        {
            newer.InitializeDatabase();
            ExecuteSql(newer,
                "INSERT INTO schema_migrations (version, description, applied_at) " +
                "VALUES (9999, '未来のマイグレーション', '2099-01-01 00:00:00')");
            ExecuteSql(newer,
                $"UPDATE settings SET value = '99.0.0' WHERE key = '{DbContext.MinAppVersionSettingKey}'");
        }

        // Act
        using var older = new DbContext(dbPath);
        Action act = () => older.InitializeDatabase();

        // Assert
        var ex = act.Should().Throw<DatabaseVersionMismatchException>(
            "旧バージョンが未知のスキーマへ書き込むとデータ不整合を招くため").Which;
        ex.DatabaseSchemaVersion.Should().Be(9999);
        ex.AppSchemaVersion.Should().BeLessThan(9999).And.BeGreaterThan(0);
        ex.RequiredAppVersion.Should().Be("99.0.0");
        ex.ErrorCode.Should().Be("DB008");
        ex.UserFriendlyMessage.Should().Contain("99.0.0", "要求バージョンを明示する")
            .And.Contain("更新してください", "行動指示で終わる（エラーメッセージ3要素）");
    }

    [Fact]
    public void InitializeDatabase_要求バージョン未記録でもブロックしフォールバック文言となること()
    {
        // Arrange - min_app_version 記録前に新スキーマ化された端境期のDBを模擬
        var dbPath = CreateDbPath();
        using (var newer = new DbContext(dbPath))
        {
            newer.InitializeDatabase();
            ExecuteSql(newer,
                "INSERT INTO schema_migrations (version, description, applied_at) " +
                "VALUES (9999, '未来のマイグレーション', '2099-01-01 00:00:00')");
            ExecuteSql(newer,
                $"DELETE FROM settings WHERE key = '{DbContext.MinAppVersionSettingKey}'");
        }

        // Act
        using var older = new DbContext(dbPath);
        Action act = () => older.InitializeDatabase();

        // Assert
        var ex = act.Should().Throw<DatabaseVersionMismatchException>().Which;
        ex.RequiredAppVersion.Should().BeNull();
        ex.UserFriendlyMessage.Should().Contain("管理者", "バージョン不明時は管理者への確認を促す");
    }

    [Fact]
    public void InitializeDatabase_ブロックされてもDBは変更されないこと()
    {
        // Arrange
        var dbPath = CreateDbPath();
        using (var newer = new DbContext(dbPath))
        {
            newer.InitializeDatabase();
            ExecuteSql(newer,
                "INSERT INTO schema_migrations (version, description, applied_at) " +
                "VALUES (9999, '未来のマイグレーション', '2099-01-01 00:00:00')");
            ExecuteSql(newer,
                $"UPDATE settings SET value = '99.0.0' WHERE key = '{DbContext.MinAppVersionSettingKey}'");
        }

        // Act
        using var older = new DbContext(dbPath);
        try { older.InitializeDatabase(); } catch (DatabaseVersionMismatchException) { }

        // Assert - min_app_version が旧バージョン側の値で上書きされていない
        ReadMinAppVersion(older).Should().Be("99.0.0", "ブロック時は記録処理まで到達しない");
    }
}
