#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using ICCardManager.Data;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// DB の形（スキーマ・適用済みの版数・設定のキー）。
/// </summary>
/// <remarks>
/// <c>settings</c> は<b>キーだけ</b>を比べる。値には作成時刻や環境に由来するもの（例: 最終実施日時）が入り得て、
/// 空から作った DB とコピーした DB で一致する理由が無いため。比べたいのは「どの行が初期投入されたか」である。
/// </remarks>
internal sealed class DatabaseShape
{
    public DatabaseShape(IReadOnlyList<string> schemaObjects, IReadOnlyList<long> migrationVersions, IReadOnlyList<string> settingKeys)
    {
        SchemaObjects = schemaObjects;
        MigrationVersions = migrationVersions;
        SettingKeys = settingKeys;
    }

    /// <summary><c>sqlite_master</c> の（type, name, tbl_name, sql）を連結した文字列の昇順リスト。</summary>
    public IReadOnlyList<string> SchemaObjects { get; }

    /// <summary><c>schema_migrations.version</c> の昇順リスト。</summary>
    public IReadOnlyList<long> MigrationVersions { get; }

    /// <summary><c>settings.key</c> の昇順リスト。</summary>
    public IReadOnlyList<string> SettingKeys { get; }
}

/// <summary>
/// 全マイグレーション適用済みの DB を、テストプロセスで 1 回だけ作ってコピーで配るヘルパー。
/// </summary>
/// <remarks>
/// ファイル DB の <c>InitializeDatabase()</c> はマイグレーションのコミットごとに fsync するため 1 回 1.7 秒ほどかかるが、
/// 適用済みの DB をコピーして <c>InitializeDatabase()</c> を呼ぶと数 ms で済む。テンプレートは
/// テストプロセス専用の一時フォルダーに作り、プロセス終了時に消す。
/// </remarks>
internal static class MigratedDatabaseTemplate
{
    internal const string TemplateFileName = "template.db";

    private static readonly string[] CompanionSuffixes = { "-journal", "-wal", "-shm" };

    private static int _buildCount;

    private static readonly Lazy<string> Template = CreateTemplateLazy(CreateProcessTemplate);

    /// <summary>テンプレートの遅延作成。作成関数は最初の要求で 1 回だけ実行される（テストが新しい Lazy で競合を再現できるよう分けている）。</summary>
    internal static Lazy<string> CreateTemplateLazy(Func<string> build)
        => new Lazy<string>(build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>テンプレートを作った回数（テスト用。プロセスで 1 を超えてはならない）。</summary>
    internal static int BuildCount => Volatile.Read(ref _buildCount);

    /// <summary>テンプレートのパス（テスト用。未作成なら作る）。</summary>
    internal static string TemplatePathForTests => Template.Value;

    /// <summary>
    /// テンプレートを <paramref name="destinationPath"/> へコピーして同じパスを返す。
    /// </summary>
    /// <exception cref="InvalidOperationException">コピー先または付随ファイルが既にある（前のテストの残骸を使わない）。</exception>
    public static string CopyTo(string destinationPath)
    {
        var template = Template.Value;
        foreach (var path in new[] { destinationPath }.Concat(CompanionSuffixes.Select(s => destinationPath + s)))
        {
            if (File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"コピー先 '{path}' が既に存在します。前のテストの残骸を使うと状態が混ざるため、" +
                    "テストごとに新しいパス（一時フォルダー配下の GUID 付きの名前など）を指定してください。");
            }
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.Copy(template, destinationPath, false);
        return destinationPath;
    }

    private static string CreateProcessTemplate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ICCardManagerTestTemplate_" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteDirectoryQuietly(directory);
        Interlocked.Increment(ref _buildCount);
        return BuildTemplate(directory);
    }

    /// <summary>
    /// <paramref name="directory"/> に全マイグレーション適用済みの DB を作ってパスを返す。
    /// 失敗したら作りかけのファイルを消して例外を投げる。
    /// </summary>
    internal static string BuildTemplate(string directory, Action<string>? afterInitialize = null)
    {
        var path = Path.Combine(directory, TemplateFileName);
        try
        {
            Directory.CreateDirectory(directory);
            using (var context = new DbContext(path))
            {
                context.InitializeDatabase();
            }
            SQLiteConnection.ClearAllPools();
            afterInitialize?.Invoke(path);

            foreach (var suffix in CompanionSuffixes)
            {
                if (File.Exists(path + suffix))
                {
                    throw new InvalidOperationException(
                        $"テンプレート DB の作成後に '{path + suffix}' が残っています。未確定の書き込みを含むコピーを配ることになるため、" +
                        "接続が閉じられているか確認してください。");
                }
            }
            return path;
        }
        catch
        {
            SQLiteConnection.ClearAllPools();
            DeleteFileQuietly(path);
            foreach (var suffix in CompanionSuffixes)
            {
                DeleteFileQuietly(path + suffix);
            }
            try
            {
                Directory.Delete(directory, false);
            }
            catch (Exception)
            {
                // 空でない・作れていない場合は残してよい（呼び出し元が一時フォルダーごと消す）
            }
            throw;
        }
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 後片付けの失敗は元の例外を隠さないよう無視する
        }
    }

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception)
        {
            // 後片付けの失敗は無視する
        }
    }

    internal static DatabaseShape ReadShape(string dbPath)
    {
        try
        {
            using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
            connection.Open();
            var objects = Query(connection,
                "SELECT type || '|' || name || '|' || tbl_name || '|' || IFNULL(sql, '') FROM sqlite_master",
                r => r.GetString(0)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var versions = Query(connection, "SELECT version FROM schema_migrations", r => r.GetInt64(0)).OrderBy(v => v).ToList();
            var keys = Query(connection, "SELECT key FROM settings", r => r.GetString(0)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            return new DatabaseShape(objects, versions, keys);
        }
        finally
        {
            SQLiteConnection.ClearAllPools();
        }
    }

    private static List<T> Query<T>(SQLiteConnection connection, string sql, Func<SQLiteDataReader, T> read)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(read(reader));
        }
        return list;
    }
}
