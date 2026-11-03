#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// <see cref="MigratedDatabaseTemplate"/> の規約テスト。
/// テンプレートのコピーが「空から <c>InitializeDatabase()</c> した DB」と同じ形であることを固定する。
/// </summary>
public class MigratedDatabaseTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "MigratedDatabaseTemplateTests_" + Guid.NewGuid().ToString("N"));

    public MigratedDatabaseTemplateTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // 後片付けの失敗でテストを落とさない
        }
        catch (UnauthorizedAccessException)
        {
            // 後片付けの失敗でテストを落とさない
        }
    }

    private string NewPath(string name = "db.db") => Path.Combine(_dir, Guid.NewGuid().ToString("N") + "_" + name);

    /// <summary>空から全マイグレーションを適用した DB（比較の基準）。</summary>
    private string CreateFromScratch()
    {
        var path = NewPath("scratch.db");
        using (var ctx = new DbContext(path))
        {
            ctx.InitializeDatabase();
        }
        SQLiteConnection.ClearAllPools();
        return path;
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        connection.Close();
        SQLiteConnection.ClearAllPools();
    }

    private static void AssertSameShape(DatabaseShape actual, DatabaseShape expected)
    {
        actual.SchemaObjects.Should().Equal(expected.SchemaObjects, "sqlite_master の（種類, 名前, 表, SQL）が一致するはずのため");
        actual.MigrationVersions.Should().Equal(expected.MigrationVersions, "schema_migrations の版数が一致するはずのため");
        actual.SettingKeys.Should().Equal(expected.SettingKeys, "settings のキーが一致するはずのため");
    }

    [Fact]
    public void コピーは空から初期化したDBと同じスキーマと版数を持つこと()
    {
        var scratch = CreateFromScratch();
        var copy = MigratedDatabaseTemplate.CopyTo(NewPath("copy.db"));

        AssertSameShape(MigratedDatabaseTemplate.ReadShape(copy), MigratedDatabaseTemplate.ReadShape(scratch));
        MigratedDatabaseTemplate.ReadShape(copy).MigrationVersions.Should().NotBeEmpty("版数が空だと比較が空振りするため");
    }

    [Fact]
    public void 検出ロジックはスキーマの食い違いを見つけること()
    {
        var baseline = CreateFromScratch();

        var tableDropped = CreateFromScratch();
        Execute(tableDropped, "DROP TABLE operation_log");
        MigratedDatabaseTemplate.ReadShape(tableDropped).SchemaObjects
            .Should().NotEqual(MigratedDatabaseTemplate.ReadShape(baseline).SchemaObjects, "表を 1 つ DROP したら見つかるはずのため");

        var versionDeleted = CreateFromScratch();
        Execute(versionDeleted, "DELETE FROM schema_migrations WHERE version = (SELECT MAX(version) FROM schema_migrations)");
        MigratedDatabaseTemplate.ReadShape(versionDeleted).MigrationVersions
            .Should().NotEqual(MigratedDatabaseTemplate.ReadShape(baseline).MigrationVersions, "版数を 1 行消したら見つかるはずのため");
    }

    [Fact]
    public void コピーにInitializeDatabaseを呼んでもスキーマと版数は変わらないこと()
    {
        var copy = MigratedDatabaseTemplate.CopyTo(NewPath("copy.db"));
        var before = MigratedDatabaseTemplate.ReadShape(copy);

        using (var ctx = new DbContext(copy))
        {
            ctx.InitializeDatabase();
        }
        SQLiteConnection.ClearAllPools();

        AssertSameShape(MigratedDatabaseTemplate.ReadShape(copy), before);
    }

    [Fact]
    public void コピーは互いに独立していること()
    {
        var a = MigratedDatabaseTemplate.CopyTo(NewPath("a.db"));
        var b = MigratedDatabaseTemplate.CopyTo(NewPath("b.db"));

        Execute(a, "CREATE TABLE only_in_a (id INTEGER)");

        MigratedDatabaseTemplate.ReadShape(a).SchemaObjects.Should().Contain(o => o.Contains("only_in_a"));
        MigratedDatabaseTemplate.ReadShape(b).SchemaObjects.Should().NotContain(o => o.Contains("only_in_a"), "コピーは別ファイルのため");
    }

    [Theory]
    [InlineData("")]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void コピー先に残骸があれば使わないこと(string leftoverSuffix)
    {
        var destination = NewPath("copy.db");
        File.WriteAllBytes(destination + leftoverSuffix, new byte[0]);

        Action act = () => MigratedDatabaseTemplate.CopyTo(destination);

        act.Should().Throw<InvalidOperationException>("前のテストの残骸を使うと別のテストの状態が混ざるため");
    }

    [Fact]
    public void コピー先のディレクトリが無ければ作ること()
    {
        var destination = Path.Combine(_dir, "sub", "deeper", "copy.db");

        MigratedDatabaseTemplate.CopyTo(destination).Should().Be(destination);

        File.Exists(destination).Should().BeTrue();
    }

    [Fact]
    public void 並列に初回の要求が来てもテンプレートは1回だけ作られること()
    {
        var scratch = CreateFromScratch();
        var expected = MigratedDatabaseTemplate.ReadShape(scratch);
        const int Parallelism = 8;
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, Parallelism).Select(_ => Task.Factory.StartNew(() =>
        {
            gate.Wait();
            return MigratedDatabaseTemplate.CopyTo(NewPath("parallel.db"));
        }, TaskCreationOptions.LongRunning)).ToArray();

        gate.Set();
        Task.WaitAll(tasks);

        foreach (var task in tasks)
        {
            AssertSameShape(MigratedDatabaseTemplate.ReadShape(task.Result), expected);
        }
        MigratedDatabaseTemplate.BuildCount.Should().Be(1, "テンプレートはプロセスで 1 回だけ作るため（別のテストクラスが先に作っていてもよいが 2 回以上は不可）");
    }

    [Fact]
    public void テンプレートの作成後に付随ファイルが残っていないこと()
    {
        var path = MigratedDatabaseTemplate.TemplatePathForTests;

        File.Exists(path).Should().BeTrue();
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            File.Exists(path + suffix).Should().BeFalse($"{suffix} が残るとコピーが未確定の書き込みを含み得るため");
        }
    }

    [Fact]
    public void テンプレートの作成に失敗したら例外を投げ作りかけを残さないこと()
    {
        var blocker = NewPath("blocker.txt");
        File.WriteAllText(blocker, "x");
        var unusable = Path.Combine(blocker, "template");

        Action act = () => MigratedDatabaseTemplate.BuildTemplate(unusable);

        act.Should().Throw<Exception>("ファイルの下にはフォルダーを作れないため");
        File.ReadAllText(blocker).Should().Be("x", "既存のファイルを壊さないため");
        Directory.GetFileSystemEntries(_dir).Should().OnlyContain(e => e == blocker || !e.EndsWith("template.db"),
            "作りかけのテンプレートや付随ファイルを残さないため");
    }

    [Fact]
    public void 作成途中で失敗したら作りかけのファイルを消すこと()
    {
        // DB ファイルの置き場所に同名のフォルダーがあると、SQLite が開けず作成が途中で失敗する
        var dir = Path.Combine(_dir, "partial");
        Directory.CreateDirectory(Path.Combine(dir, MigratedDatabaseTemplate.TemplateFileName));

        Action act = () => MigratedDatabaseTemplate.BuildTemplate(dir);

        act.Should().Throw<Exception>();
        File.Exists(Path.Combine(dir, MigratedDatabaseTemplate.TemplateFileName + "-journal")).Should().BeFalse();
        File.Exists(Path.Combine(dir, MigratedDatabaseTemplate.TemplateFileName + "-wal")).Should().BeFalse();
        File.Exists(Path.Combine(dir, MigratedDatabaseTemplate.TemplateFileName + "-shm")).Should().BeFalse();
    }
}
