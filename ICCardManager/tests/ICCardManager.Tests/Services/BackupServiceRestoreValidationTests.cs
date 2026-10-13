using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Migrations;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2269: リストア元ファイルの検証（整合性・トリガー／ビュー・制約の衝突解決句・スキーマの版）と、
/// 検証に使う一時コピーの扱いのテスト
/// </summary>
/// <remarks>
/// <para>
/// 修正前のリストアは SQLite のヘッダしか検証せず、トリガーを仕込んだ DB をそのまま受け入れていた
/// （<c>operation_log</c> への INSERT を <c>RAISE(IGNORE)</c> で捨てるトリガーで、以後の監査ログが痕跡なく欠落する）。
/// </para>
/// <para>
/// 「欠陥を突く側」（拒否すべきファイル）と「正当なバックアップを塞いでいない側」
/// （現在のスキーマ・古い版・マイグレーション機構の導入前のバックアップの合格）を対で置く。
/// 後者が無いと、検証を常に不合格にする実装でも緑になる。
/// </para>
/// <para>
/// 一時コピーの置き場所はテストごとのフォルダーへ差し替え、後に残らないことをフォルダーの中身で表明する。
/// </para>
/// </remarks>
public class BackupServiceRestoreValidationTests : IDisposable
{
    private const string MarkerKey = "issue2269_restore_marker";

    private const string DropAuditTrigger =
        "CREATE TRIGGER drop_audit BEFORE INSERT ON operation_log BEGIN SELECT RAISE(IGNORE); END";

    /// <summary>トリガーを使わずに operation_log への 2 行目以降の INSERT を黙って捨てる表へ作り直す</summary>
    private const string SilentlyDroppingOperationLog =
        "DROP TABLE operation_log; " +
        "CREATE TABLE operation_log (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT, " +
        "x INTEGER DEFAULT 1 UNIQUE ON CONFLICT IGNORE)";

    /// <summary>
    /// トリガーの <c>type</c> 列を大文字へ書き換える（コードレビューで検出）。SQLite は種類を定義文から決めるのでトリガーは働き続けるが、
    /// <c>type = 'trigger'</c> で絞る検査は素通りする
    /// </summary>
    private const string UppercaseTypeTrigger =
        DropAuditTrigger + "; PRAGMA writable_schema=ON; UPDATE sqlite_master SET type='TRIGGER' WHERE name='drop_audit'";

    /// <summary>衝突解決句のある表の <c>type</c> 列を大文字・小文字混じりへ書き換える</summary>
    private const string MixedCaseTypeConflictTable =
        SilentlyDroppingOperationLog + "; PRAGMA writable_schema=ON; UPDATE sqlite_master SET type='Table' WHERE name='operation_log'";

    /// <summary>
    /// 台帳の行の削除に合わせて監査ログを黙って消す生成列を operation_log に持たせる（コードレビューで検出）
    /// </summary>
    private const string CascadingGeneratedColumnOperationLog =
        "DROP TABLE operation_log; " +
        "CREATE TABLE operation_log (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT DEFAULT CURRENT_TIMESTAMP, " +
        "operator_idm TEXT NOT NULL, operator_name TEXT NOT NULL, target_table TEXT, target_id TEXT, action TEXT, " +
        "before_data TEXT, after_data TEXT, " +
        "g INTEGER GENERATED ALWAYS AS (CASE WHEN target_table = 'ledger' THEN CAST(target_id AS INTEGER) END) VIRTUAL " +
        "REFERENCES ledger(id) ON DELETE CASCADE)";

    /// <summary>本システムが作らない参照動作（通常の列）を operation_log に足す</summary>
    private const string CascadingColumnOnOperationLog =
        "ALTER TABLE operation_log ADD COLUMN ledger_ref INTEGER REFERENCES ledger(id) ON DELETE CASCADE";

    /// <summary>参照動作の無い生成列（参照動作が無くても、本システムが作らないので拒否する）</summary>
    private const string PlainGeneratedColumnOnLedger =
        "ALTER TABLE ledger ADD COLUMN g2 INTEGER GENERATED ALWAYS AS (1) VIRTUAL";

    /// <summary>
    /// 表値関数の形の pragma に影を付ける普通の表（writable_schema も要らない。コードレビューで検出）。
    /// 表の一覧には staff だけを見せ、列情報・外部キーは空に見せる
    /// </summary>
    private const string ShadowPragmaTables =
        "CREATE TABLE pragma_table_list (schema TEXT, name TEXT, type TEXT, ncol INTEGER, wr INTEGER, strict INTEGER); " +
        "INSERT INTO pragma_table_list VALUES ('main', 'staff', 'table', 1, 0, 0); " +
        "CREATE TABLE pragma_table_xinfo (cid INTEGER, name TEXT, type TEXT, \"notnull\" INTEGER, dflt_value TEXT, pk INTEGER, hidden INTEGER, arg TEXT, schema TEXT); " +
        "CREATE TABLE pragma_foreign_key_list (id INTEGER, seq INTEGER, \"table\" TEXT, \"from\" TEXT, \"to\" TEXT, on_update TEXT, on_delete TEXT, \"match\" TEXT, arg TEXT, schema TEXT)";

    /// <summary>影の表で隠したうえで、operation_log に台帳の削除で消える参照動作を足す</summary>
    private const string ShadowedCascadeOnOperationLog =
        ShadowPragmaTables + "; " + CascadingColumnOnOperationLog;

    /// <summary>識別子の引用が要る名前の表に生成列を置く</summary>
    private const string GeneratedColumnOnQuotedTableName =
        "CREATE TABLE \"we\"\"ird\" (a INTEGER, g INTEGER GENERATED ALWAYS AS (a) VIRTUAL)";

    private readonly string _testDirectory;
    private readonly string _inspectionFolder;

    public BackupServiceRestoreValidationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"RestoreValidationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _inspectionFolder = Path.Combine(_testDirectory, "inspection");
        Directory.CreateDirectory(_inspectionFolder);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダーの後始末の失敗はテスト結果に影響させない
        }
        GC.SuppressFinalize(this);
    }

    #region 正当なバックアップは合格する

    [Fact]
    public void PrepareRestoreSource_現在のスキーマのバックアップは合格し一時コピーを持つこと()
    {
        var backupPath = CreateAppDatabaseFile("current.db");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.IsValid.Should().BeTrue();
        result.Failure.Should().Be(RestoreSourceValidationFailure.None);
        result.StagedCopyPath.Should().NotBeNull();
        Path.GetDirectoryName(result.StagedCopyPath).Should().Be(_inspectionFolder);
        File.ReadAllBytes(result.StagedCopyPath!).Should().Equal(File.ReadAllBytes(backupPath),
            "置き換えに使う一時コピーはリストア元と同じ内容");
    }

    [Fact]
    public void PrepareRestoreSource_古い版のバックアップは合格すること()
    {
        var backupPath = CreateAppDatabaseFile("old_version.db");
        Execute(backupPath, "DELETE FROM schema_migrations WHERE version > 1");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.IsValid.Should().BeTrue("古い版は起動時のマイグレーションで最新へ上がるので、リストアを塞がない");
    }

    [Fact]
    public void PrepareRestoreSource_マイグレーション機構の導入前のバックアップは合格すること()
    {
        var backupPath = CreateAppDatabaseFile("legacy.db");
        Execute(backupPath, "DROP TABLE schema_migrations");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.IsValid.Should().BeTrue("schema_migrations の無い DB は起動時に版 1 として扱われる（HandleLegacyDatabase）");
    }

    /// <summary>
    /// 検証の前後でリストア元の内容・更新日時が変わらず、隣にジャーナル等を作らないこと
    /// </summary>
    /// <remarks>
    /// 正常な DB では元のファイルを SQLite で開いても内容は変わらないため、このテストだけでは
    /// 「一時コピーを検査していること」は表明できない。それは
    /// <see cref="RestoreFromPreparedSource_検証した後にリストア元が書き換えられても検証した内容で置き換えること"/> と
    /// <see cref="PrepareRestoreSource_現在のスキーマのバックアップは合格し一時コピーを持つこと"/> が担う。
    /// </remarks>
    [Fact]
    public void PrepareRestoreSource_検証の前後で元のファイルの内容が変わらず隣にジャーナルを作らないこと()
    {
        var backupPath = CreateAppDatabaseFile("untouched.db");
        var before = ComputeHash(backupPath);
        var writeTimeBefore = File.GetLastWriteTimeUtc(backupPath);

        using (CreateService().PrepareRestoreSource(backupPath))
        {
        }

        ComputeHash(backupPath).Should().Equal(before);
        File.GetLastWriteTimeUtc(backupPath).Should().Be(writeTimeBefore);
        File.Exists(backupPath + "-journal").Should().BeFalse();
        File.Exists(backupPath + "-wal").Should().BeFalse();
        File.Exists(backupPath + "-shm").Should().BeFalse();
    }

    #endregion

    #region 改変・破損・新しい版のバックアップは拒否する

    [Fact]
    public void PrepareRestoreSource_operation_logへの書き込みを捨てるトリガーを含むと拒否すること()
    {
        var backupPath = CreateAppDatabaseFile("trigger.db");
        Execute(backupPath, DropAuditTrigger);

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(RestoreSourceValidationFailure.UnexpectedSchemaObject);
        result.UserMessage.Should().Contain("トリガー");
        result.StagedCopyPath.Should().BeNull();
    }

    [Fact]
    public void PrepareRestoreSource_ビューを含むと拒否すること()
    {
        var backupPath = CreateAppDatabaseFile("view.db");
        Execute(backupPath, "CREATE VIEW staff_names AS SELECT name FROM staff");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.UnexpectedSchemaObject);
    }

    /// <summary>
    /// トリガーを使わずに監査ログを黙って捨てる経路（コードレビューで検出）
    /// </summary>
    [Fact]
    public void PrepareRestoreSource_INSERTを黙って捨てる衝突解決句の列を持つ表があると拒否すること()
    {
        var backupPath = CreateAppDatabaseFile("conflict_ignore.db");
        Execute(backupPath, SilentlyDroppingOperationLog);
        CountTriggers(backupPath).Should().Be(0, "前提: トリガーは使っていない");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.UnexpectedSchemaObject);
    }

    /// <summary>
    /// 前提の確認: <c>type</c> 列を大文字へ書き換えても、トリガーは働き続けて operation_log への INSERT を黙って捨てる
    /// </summary>
    [Fact]
    public void 前提_type列を大文字へ書き換えたトリガーも働き続けること()
    {
        var backupPath = CreateAppDatabaseFile("uppercase_premise.db");
        Execute(backupPath, UppercaseTypeTrigger);
        var before = CountRows(backupPath, "operation_log");

        Execute(backupPath,
            "INSERT INTO operation_log (operator_idm, operator_name, action) VALUES ('0000000000000000', 'test', 'premise')");

        CountRows(backupPath, "operation_log").Should().Be(before, "トリガーが INSERT を黙って捨てている");
        CountTriggers(backupPath).Should().Be(0, "type = 'trigger' で数える検査は見落とす");
    }

    [Theory]
    [InlineData(UppercaseTypeTrigger)]
    [InlineData(MixedCaseTypeConflictTable)]
    public void PrepareRestoreSource_type列の大文字小文字を書き換えても拒否すること(string sql)
    {
        var backupPath = CreateAppDatabaseFile($"type_case_{sql.Length}.db");
        Execute(backupPath, sql);

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.UnexpectedSchemaObject);
    }

    [Theory]
    [InlineData(CascadingGeneratedColumnOperationLog, "generated:operation_log.g")]
    [InlineData(CascadingColumnOnOperationLog, "fk-action:operation_log.ledger_ref->ledger.id:on_delete CASCADE")]
    [InlineData(PlainGeneratedColumnOnLedger, "generated:ledger.g2")]
    [InlineData(ShadowedCascadeOnOperationLog, "fk-action:operation_log.ledger_ref->ledger.id:on_delete CASCADE")]
    [InlineData(GeneratedColumnOnQuotedTableName, "generated:we\"ird.g")]
    public void PrepareRestoreSource_生成列と本システムが作らない参照動作を拒否すること(string sql, string expectedDetail)
    {
        var backupPath = CreateAppDatabaseFile($"fk_{expectedDetail.Length}_{sql.Length}.db");
        Execute(backupPath, sql);
        var logger = new RecordingLogger<BackupService>();

        using var result = CreateService(logger).PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.UnexpectedSchemaObject);
        logger.Entries.Should().Contain(e => e.Message.Contains(expectedDetail), logger.FormatEntries());
    }

    /// <summary>
    /// 対: 本システムが作る参照動作（ledger_detail.ledger_id → ledger(id) ON DELETE CASCADE）は許可リストにあり、拒否しない
    /// </summary>
    [Fact]
    public void InspectSchema_本システムが作る参照動作は想定外として扱わないこと()
    {
        var dbPath = CreateAppDatabaseFile("legit_fk.db");
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT on_delete FROM pragma_foreign_key_list('ledger_detail') WHERE \"from\" = 'ledger_id'";
            (command.ExecuteScalar() as string).Should().Be("CASCADE", "前提: 正規のスキーマが参照動作を持つ");
        }

        SchemaObjectInspector.InspectSchema(connection).UnexpectedObjects.Should().BeEmpty();
    }

    /// <summary>
    /// 影の表で schema_migrations を一覧から隠しても、版 999 の DB を合格させないこと（コードレビューで検出）
    /// </summary>
    [Fact]
    public void PrepareRestoreSource_影の表で版の表を隠しても合格させないこと()
    {
        var backupPath = CreateAppDatabaseFile("shadow_version.db");
        Execute(backupPath, "INSERT INTO schema_migrations (version, description) VALUES (999, 'future'); " + ShadowPragmaTables);

        using (var connection = new SQLiteConnection(DbContext.BuildConnectionString(backupPath)))
        {
            connection.Open();
            SchemaObjectInspector.InspectSchema(connection).TableNames
                .Should().Contain("schema_migrations", "表の一覧は影の表ではなく PRAGMA 文から得る");
        }

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(RestoreSourceValidationFailure.UnexpectedSchemaObject, "pragma_ で始まる影の表そのものを拒否する");
    }

    /// <summary>
    /// 予約名の判定のサンプル（SQLite 内部の正規の名前は通し、pragma_ / その他の sqlite_ は拒否する）
    /// </summary>
    [Theory]
    [InlineData("sqlite_sequence", false)]
    [InlineData("sqlite_stat1", false)]
    [InlineData("SQLITE_STAT4", false)]
    [InlineData("sqlite_autoindex_staff_1", false)]
    [InlineData("staff", false)]
    [InlineData("my_pragma_table", false)]
    [InlineData("pragma_table_list", true)]
    [InlineData("Pragma_Foreign_Key_List", true)]
    [InlineData("sqlite_evil", true)]
    public void IsReservedName_サンプル入力を正しく判定すること(string name, bool expected)
    {
        SchemaObjectInspector.IsReservedName(name).Should().Be(expected);
    }

    /// <summary>
    /// 対: 本システムの DB は sqlite_sequence（AUTOINCREMENT の表）を持つが、予約名として拒否しない
    /// </summary>
    [Fact]
    public void InspectSchema_sqlite_sequenceを持つ正規のDBは予約名として扱わないこと()
    {
        var dbPath = CreateAppDatabaseFile("with_sequence.db");
        Execute(dbPath, "INSERT INTO operation_log (operator_idm, operator_name, action) VALUES ('0000000000000000', 't', 'x')");
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sqlite_sequence'";
            Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be(1, "前提: sqlite_sequence がある");
        }

        SchemaObjectInspector.InspectSchema(connection).UnexpectedObjects.Should().BeEmpty();
    }

    /// <summary>
    /// schema_migrations の type 列を書き換えても、版の存在確認が外れて新しい版が「導入前（版 0）」として合格しないこと（コードレビューで検出）
    /// </summary>
    [Fact]
    public void PrepareRestoreSource_版の表のtype列を書き換えても新しい版を拒否すること()
    {
        var backupPath = CreateAppDatabaseFile("newer_type_case.db");
        Execute(backupPath,
            "INSERT INTO schema_migrations (version, description) VALUES (999, 'future'); " +
            "PRAGMA writable_schema=ON; UPDATE sqlite_master SET type='TABLE' WHERE name='schema_migrations'");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.NewerSchemaVersion);
    }

    /// <summary>
    /// 本システムの DB の印になる表（staff / schema_migrations）が無い SQLite ファイルは拒否する（コードレビューで検出）。
    /// 対: staff だけを持つ導入前の DB は <see cref="PrepareRestoreSource_マイグレーション機構の導入前のバックアップは合格すること"/> が通す
    /// </summary>
    [Fact]
    public void PrepareRestoreSource_本システムのDBでないSQLiteファイルは拒否すること()
    {
        var path = Path.Combine(_testDirectory, "other_app.db");
        Execute(path, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT); INSERT INTO notes (body) VALUES ('x')");

        using var result = CreateService().PrepareRestoreSource(path);

        result.Failure.Should().Be(RestoreSourceValidationFailure.NotApplicationDatabase);
        result.UserMessage.Should().Contain("本システム（ピッすい）のデータベースではありません");
    }

    [Fact]
    public void PrepareRestoreSource_staffが無くてもschema_migrationsがあれば本システムのDBとして扱うこと()
    {
        var path = Path.Combine(_testDirectory, "migrations_only.db");
        Execute(path,
            "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, description TEXT NOT NULL, applied_at TEXT); " +
            "INSERT INTO schema_migrations (version, description) VALUES (1, 'init')");

        using var result = CreateService().PrepareRestoreSource(path);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PrepareRestoreSource_このアプリより新しい版のバックアップは拒否すること()
    {
        var backupPath = CreateAppDatabaseFile("newer.db");
        var latest = GetLatestKnownVersion(backupPath);
        Execute(backupPath,
            $"INSERT INTO schema_migrations (version, description) VALUES ({latest + 1}, 'future')");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.NewerSchemaVersion);
        result.UserMessage.Should().Contain((latest + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 版の値が int に収まらないと、起動時の <c>Convert.ToInt32</c> が例外になる。検証では例外を漏らさず不合格にする
    /// </summary>
    [Theory]
    [InlineData("9999999999")]
    [InlineData("-1")]
    public void PrepareRestoreSource_版の記録が本システムの書かない値なら例外を漏らさず拒否すること(string version)
    {
        var backupPath = CreateAppDatabaseFile($"bad_version_{version}.db");
        Execute(backupPath, "DELETE FROM schema_migrations");
        Execute(backupPath, $"INSERT INTO schema_migrations (version, description) VALUES ({version}, 'tampered')");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.UnrecognizedSchemaVersion);
    }

    [Fact]
    public void PrepareRestoreSource_ヘッダは正しいが中身が壊れていると拒否すること()
    {
        var backupPath = CreateAppDatabaseFile("corrupted.db");
        CorruptRootPageOf(backupPath, "ledger");
        BackupService.IsValidSqliteFile(backupPath).Should().BeTrue("前提: ヘッダの検査だけでは見抜けない壊れ方にする");

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.Corrupted);
    }

    [Fact]
    public void PrepareRestoreSource_SQLiteでないファイルは拒否すること()
    {
        var path = Path.Combine(_testDirectory, "not_sqlite.db");
        File.WriteAllText(path, "This is not a SQLite database file, just some text content.");

        using var result = CreateService().PrepareRestoreSource(path);

        result.Failure.Should().Be(RestoreSourceValidationFailure.NotSqliteDatabase);
    }

    [Fact]
    public void PrepareRestoreSource_存在しないファイルは拒否すること()
    {
        using var result = CreateService().PrepareRestoreSource(Path.Combine(_testDirectory, "missing.db"));

        result.Failure.Should().Be(RestoreSourceValidationFailure.FileNotFound);
    }

    [Fact]
    public void PrepareRestoreSource_リストア元を読み取れないときは読み取れないと案内すること()
    {
        var backupPath = CreateAppDatabaseFile("locked.db");
        using var lockStream = new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using var result = CreateService().PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.Unreadable);
    }

    /// <summary>
    /// 一時コピーを書けない（空き容量の不足等）ことを、リストア元を読み取れないことと取り違えない（コードレビューで検出）
    /// </summary>
    [Fact]
    public void PrepareRestoreSource_一時コピーを書けないときは読み取れないと取り違えず一時コピーの失敗と案内すること()
    {
        var backupPath = CreateAppDatabaseFile("staging_fail.db");
        // 一時フォルダーとしてファイルのパスを渡し、一時コピーの作成を必ず失敗させる
        var notAFolder = Path.Combine(_testDirectory, "not_a_folder.txt");
        File.WriteAllText(notAFolder, "x");

        using var result = CreateService(inspectionFolder: notAFolder).PrepareRestoreSource(backupPath);

        result.Failure.Should().Be(RestoreSourceValidationFailure.StagingFailed);
        result.UserMessage.Should().Contain("空き容量");
    }

    [Fact]
    public void PrepareRestoreSource_拒否した理由とファイルをWarningログへ残すこと()
    {
        var backupPath = CreateAppDatabaseFile("trigger_log.db");
        Execute(backupPath, DropAuditTrigger);
        var logger = new RecordingLogger<BackupService>();

        using (CreateService(logger).PrepareRestoreSource(backupPath))
        {
        }

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(backupPath) && e.Message.Contains("drop_audit"),
            logger.FormatEntries());
    }

    #endregion

    #region 一時コピーを後に残さない

    [Theory]
    [InlineData("trigger")]
    [InlineData("conflict")]
    [InlineData("corrupted")]
    [InlineData("newer")]
    public void PrepareRestoreSource_不合格なら一時コピーとジャーナルをその場で削除すること(string kind)
    {
        var backupPath = CreateAppDatabaseFile($"reject_{kind}.db");
        switch (kind)
        {
            case "trigger":
                Execute(backupPath, DropAuditTrigger);
                break;
            case "conflict":
                Execute(backupPath, SilentlyDroppingOperationLog);
                break;
            case "corrupted":
                CorruptRootPageOf(backupPath, "ledger");
                break;
            default:
                Execute(backupPath, $"INSERT INTO schema_migrations (version, description) VALUES ({GetLatestKnownVersion(backupPath) + 1}, 'future')");
                break;
        }

        var result = CreateService().PrepareRestoreSource(backupPath);

        result.IsValid.Should().BeFalse("前提: 不合格の経路");
        Directory.EnumerateFileSystemEntries(_inspectionFolder).Should().BeEmpty("Dispose を待たずに削除する");
    }

    [Fact]
    public void PrepareRestoreSource_合格した一時コピーはDisposeで削除され2回目のDisposeは何もしないこと()
    {
        var backupPath = CreateAppDatabaseFile("dispose.db");
        var result = CreateService().PrepareRestoreSource(backupPath);
        Directory.EnumerateFiles(_inspectionFolder).Should().ContainSingle("前提: 合格の間は一時コピーを持つ");

        result.Dispose();
        var act = () => result.Dispose();

        act.Should().NotThrow();
        Directory.EnumerateFileSystemEntries(_inspectionFolder).Should().BeEmpty();
    }

    [Fact]
    public void CleanupStaleRestoreInspectionFiles_十分古い一時コピーとジャーナルだけを回収すること()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0);
        var stale = Path.Combine(_inspectionFolder, BackupService.RestoreInspectionFilePrefix + "aaaa1111.db");
        var staleJournal = stale + "-journal";
        var recent = Path.Combine(_inspectionFolder, BackupService.RestoreInspectionFilePrefix + "bbbb2222.db");
        var unrelated = Path.Combine(_inspectionFolder, "other_app_file.db");
        foreach (var path in new[] { stale, staleJournal, recent, unrelated })
        {
            File.WriteAllText(path, "x");
        }

        File.SetLastWriteTime(stale, now.AddHours(-2));
        File.SetLastWriteTime(staleJournal, now.AddHours(-2));
        File.SetLastWriteTime(recent, now.AddMinutes(-10));
        File.SetLastWriteTime(unrelated, now.AddDays(-30));

        CreateService(clock: new FixedSystemClock(now)).CleanupStaleRestoreInspectionFiles();

        File.Exists(stale).Should().BeFalse("強制終了で取り残された一時コピーは回収する");
        File.Exists(staleJournal).Should().BeFalse("ジャーナルも同じ接頭辞で回収する");
        File.Exists(recent).Should().BeTrue("使用中かもしれない新しいものは消さない");
        File.Exists(unrelated).Should().BeTrue("本システムの一時コピー以外は消さない");
    }

    [Fact]
    public void CleanupStaleRestoreInspectionFiles_一時フォルダーが無くても例外を投げないこと()
    {
        var act = () => CreateService(inspectionFolder: Path.Combine(_testDirectory, "missing_folder"))
            .CleanupStaleRestoreInspectionFiles();

        act.Should().NotThrow();
    }

    #endregion

    #region RestoreFromBackup は検証に不合格なら何も書き換えない

    [Fact]
    public void RestoreFromBackup_トリガーを含むバックアップは拒否し現在のDBを書き換えないこと()
    {
        var dbPath = Path.Combine(_testDirectory, "restore_target.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        var backupPath = Path.Combine(_testDirectory, "backup_with_trigger.db");
        dbContext.CloseConnection();
        File.Copy(dbPath, backupPath);
        Execute(backupPath, DropAuditTrigger);
        WriteMarker(dbContext, "current");

        var service = CreateService(dbContext: dbContext);

        var result = service.RestoreFromBackup(backupPath);

        result.Should().BeFalse();
        dbContext.CloseConnection();
        File.Exists(dbPath + ".temp").Should().BeFalse("検証は退避より前に行い、不合格なら退避もしない");
        ReadMarker(dbPath).Should().Be("current", "不合格なら現在の DB をそのまま残す");
        CountTriggers(dbPath).Should().Be(0);
        Directory.EnumerateFileSystemEntries(_inspectionFolder).Should().BeEmpty();
    }

    [Fact]
    public void RestoreFromBackup_古い版のバックアップはリストアでき一時コピーを残さないこと()
    {
        var dbPath = Path.Combine(_testDirectory, "restore_target_ok.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        var backupPath = Path.Combine(_testDirectory, "backup_old.db");
        dbContext.CloseConnection();
        File.Copy(dbPath, backupPath);
        Execute(backupPath, "DELETE FROM schema_migrations WHERE version > 1");
        WriteMarker(dbContext, "current");

        var service = CreateService(dbContext: dbContext);

        var result = service.RestoreFromBackup(backupPath);

        result.Should().BeTrue();
        dbContext.CloseConnection();
        ReadMarker(dbPath).Should().BeNull("印を付ける前のバックアップでリストアされている");
        Directory.EnumerateFileSystemEntries(_inspectionFolder).Should().BeEmpty();
    }

    /// <summary>
    /// 検証した後にリストア元が差し替えられても、検証した内容（一時コピー）で置き換えること（TOCTOU。コードレビューで検出）
    /// </summary>
    [Fact]
    public void RestoreFromPreparedSource_検証した後にリストア元が書き換えられても検証した内容で置き換えること()
    {
        var dbPath = Path.Combine(_testDirectory, "restore_target_toctou.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        var backupPath = Path.Combine(_testDirectory, "backup_toctou.db");
        dbContext.CloseConnection();
        File.Copy(dbPath, backupPath);

        var service = CreateService(dbContext: dbContext);
        using var prepared = service.PrepareRestoreSource(backupPath);
        prepared.IsValid.Should().BeTrue("前提: 検証の時点では正当なバックアップ");

        // 検証の後で、リストア元にトリガーを仕込む
        Execute(backupPath, DropAuditTrigger);

        var result = service.RestoreFromPreparedSource(prepared);

        result.Should().BeTrue();
        dbContext.CloseConnection();
        CountTriggers(dbPath).Should().Be(0, "置き換えは検証した一時コピーから行い、リストア元を読み直さない");
    }

    #endregion

    #region 起動時の検知（DbContext.InitializeDatabase）

    [Theory]
    [InlineData(DropAuditTrigger, "trigger:drop_audit")]
    [InlineData(SilentlyDroppingOperationLog, "conflict:table:operation_log")]
    [InlineData(UppercaseTypeTrigger, "trigger:drop_audit")]
    [InlineData(MixedCaseTypeConflictTable, "conflict:table:operation_log")]
    [InlineData(CascadingGeneratedColumnOperationLog, "generated:operation_log.g")]
    [InlineData(ShadowedCascadeOnOperationLog, "fk-action:operation_log.ledger_ref->ledger.id:on_delete CASCADE")]
    public void InitializeDatabase_想定外の仕組みがあればWarningログへ残し起動は続けること(string sql, string expected)
    {
        var dbPath = CreateAppDatabaseFile($"startup_{expected.Length}_{sql.Length}.db");
        Execute(dbPath, sql);
        var logger = new RecordingLogger<DbContext>();

        using var dbContext = new DbContext(dbPath, logger);
        var act = () => dbContext.InitializeDatabase();

        act.Should().NotThrow("業務は止めず、痕跡だけを残す");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(expected),
            logger.FormatEntries());
    }

    [Fact]
    public void InitializeDatabase_本システムのスキーマだけなら警告を出さないこと()
    {
        var dbPath = CreateAppDatabaseFile("startup_clean.db");
        var logger = new RecordingLogger<DbContext>();

        using var dbContext = new DbContext(dbPath, logger);
        dbContext.InitializeDatabase();

        logger.Entries.Should().NotContain(e => e.Message.Contains("本システムが作成しない仕組み"), logger.FormatEntries());
    }

    /// <summary>
    /// 全マイグレーションを適用した DB に、許可リスト外のトリガー・ビュー・衝突解決句が無いこと
    /// </summary>
    /// <remarks>
    /// マイグレーションでこれらを追加して <see cref="SchemaObjectInspector.AllowedObjects"/> へ
    /// 載せ忘れると、そのマイグレーションを適用した DB のバックアップがすべてリストアで拒否される。その載せ忘れを検出する。
    /// </remarks>
    [Fact]
    public void FindUnexpectedObjects_全マイグレーションを適用したDBでは空であること()
    {
        var dbPath = CreateAppDatabaseFile("all_migrations.db");

        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();

        SchemaObjectInspector.FindUnexpectedObjects(connection).Should().BeEmpty();
    }

    /// <summary>
    /// 衝突解決句の判定のサンプル（検出する形と、検出しない形を対で固定する）
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE t (x INTEGER DEFAULT 1 UNIQUE ON CONFLICT IGNORE)", true)]
    [InlineData("CREATE TABLE t (x INTEGER PRIMARY KEY ON CONFLICT REPLACE)", true)]
    [InlineData("create table t (x integer unique  on\r\n  conflict\tignore)", true)]
    [InlineData("CREATE TABLE t (x INTEGER NOT NULL ON CONFLICT REPLACE DEFAULT 0)", true)]
    [InlineData("CREATE TABLE t (x TEXT, UNIQUE (x) ON CONFLICT IGNORE)", true)]
    [InlineData("CREATE TABLE t (x TEXT DEFAULT 'ON CONFLICT IGNORE')", false)]
    [InlineData("CREATE TABLE t (x TEXT DEFAULT 'it''s ON CONFLICT REPLACE')", false)]
    [InlineData("CREATE TABLE t (x TEXT -- ON CONFLICT IGNORE\r\n)", false)]
    [InlineData("CREATE TABLE t (x TEXT /* on conflict replace */)", false)]
    [InlineData("CREATE TABLE t (\"on conflict ignore\" TEXT)", false)]
    [InlineData("CREATE TABLE t ([on conflict ignore] TEXT)", false)]
    [InlineData("CREATE TABLE t (x INTEGER UNIQUE ON CONFLICT ROLLBACK)", false)]
    [InlineData("CREATE TABLE t (x INTEGER UNIQUE ON CONFLICT ABORT)", false)]
    [InlineData("CREATE TABLE t (x INTEGER UNIQUE)", false)]
    [InlineData(null, false)]
    public void HasSilentConflictClause_サンプル入力を正しく判定すること(string? sql, bool expected)
    {
        SchemaObjectInspector.HasSilentConflictClause(sql).Should().Be(expected);
    }

    /// <summary>
    /// 種類は定義文の先頭で判定し、type 列は定義文が無いとき（自動索引）の補助にだけ使う
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE t (x)", "table", "table")]
    [InlineData("CREATE TABLE IF NOT EXISTS t (x)", "TABLE", "table")]
    [InlineData("create temp table t (x)", "table", "table")]
    [InlineData("CREATE UNIQUE INDEX i ON t (x)", "index", "index")]
    [InlineData("CREATE TRIGGER g BEFORE INSERT ON t BEGIN SELECT 1; END", "table", "trigger")]
    [InlineData("  /* c */ -- d\n CREATE TEMPORARY TRIGGER g AFTER DELETE ON t BEGIN SELECT 1; END", "index", "trigger")]
    [InlineData("CREATE VIEW v AS SELECT 1", "table", "view")]
    [InlineData("CREATE VIRTUAL TABLE f USING fts5(x)", "table", "virtual-table")]
    [InlineData("SELECT 1", "table", null)]
    [InlineData(null, "INDEX", "index")]
    [InlineData(null, "table", null)]
    public void ClassifyDefinition_定義文の先頭で種類を判定すること(string? sql, string type, string? expected)
    {
        SchemaObjectInspector.ClassifyDefinition(sql, type).Should().Be(expected);
    }

    #endregion

    #region 案内文言の品質

    /// <summary>
    /// 不合格の理由ごとの案内が 3 要素（何が・なぜ・どうすれば）を満たし、理由ごとに異なること
    /// </summary>
    /// <remarks>
    /// ファクトリはリフレクションで列挙する。理由を追加したときに文言の検証が自動で追随するようにするため
    /// （<c>ConcurrencyConflictMessageTests</c> と同じ形）。
    /// </remarks>
    [Fact]
    public void 不合格の案内文言が3要素を満たし理由ごとに異なること()
    {
        var results = typeof(RestoreSourceValidationResult)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.ReturnType == typeof(RestoreSourceValidationResult) && !m.IsSpecialName)
            .Select(m => (RestoreSourceValidationResult)m.Invoke(
                null,
                m.GetParameters().Select(p => ArgumentFor(p.ParameterType)).ToArray())!)
            .Where(r => !r.IsValid)
            .ToList();

        var failureKinds = Enum.GetValues(typeof(RestoreSourceValidationFailure))
            .Cast<RestoreSourceValidationFailure>()
            .Where(f => f != RestoreSourceValidationFailure.None)
            .ToList();
        results.Select(r => r.Failure).Should().BeEquivalentTo(failureKinds, "不合格の理由すべてに文言がある");

        foreach (var result in results)
        {
            result.UserMessage.Length.Should().BeGreaterThanOrEqualTo(20, result.Failure.ToString());
            result.UserMessage.Should().StartWith("リストアを中止しました。", "何が起きたか（中止した）を先に述べる");
            result.UserMessage.Should().MatchRegex("(してください|選択してください|リストアしてください)。$", result.Failure.ToString());
            result.UserMessage.Should().NotContain("エラーが発生しました");
            result.UserMessage.Should().NotContain("ICカード");
        }

        results.Select(r => r.UserMessage).Should().OnlyHaveUniqueItems("理由ごとに「なぜ」が異なる");
    }

    /// <summary>
    /// 書き換えの形跡を理由に拒否したときは、管理者マニュアルと同じく IT 担当者への相談を案内すること
    /// </summary>
    [Fact]
    public void 書き換えの形跡で拒否したときはIT担当者への相談を案内すること()
    {
        RestoreSourceValidationResult.UnexpectedSchemaObject().UserMessage.Should().Contain("IT 担当者に相談");
        RestoreSourceValidationResult.UnrecognizedSchemaVersion().UserMessage.Should().Contain("IT 担当者に相談");
    }

    private static object ArgumentFor(Type type)
    {
        if (type == typeof(int))
        {
            return 99;
        }

        if (type == typeof(string))
        {
            return "unused";
        }

        if (type == typeof(Action))
        {
            return new Action(() => { });
        }

        throw new NotSupportedException(type.FullName);
    }

    #endregion

    #region ヘルパー

    private BackupService CreateService(
        ILogger<BackupService>? logger = null,
        string? inspectionFolder = null,
        FixedSystemClock? clock = null,
        DbContext? dbContext = null)
    {
        return new BackupService(
            dbContext ?? new DbContext(":memory:"),
            CreateSettingsRepositoryMock().Object,
            logger ?? NullLogger<BackupService>.Instance,
            clock ?? new FixedSystemClock(DateTime.Now),
            inspectionFolder ?? _inspectionFolder);
    }

    private Mock<ISettingsRepository> CreateSettingsRepositoryMock()
    {
        var mock = new Mock<ISettingsRepository>();
        mock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _testDirectory });
        return mock;
    }

    /// <summary>
    /// 全マイグレーションを適用した本システムの DB ファイルを作る（接続は閉じて返す）
    /// </summary>
    private string CreateAppDatabaseFile(string fileName)
    {
        var path = Path.Combine(_testDirectory, fileName);
        using (var dbContext = new DbContext(path))
        {
            dbContext.InitializeDatabase();
            dbContext.CloseConnection();
        }

        return path;
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int GetLatestKnownVersion(string dbPath)
    {
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        return new MigrationRunner(connection).GetLatestKnownVersion();
    }

    private static long CountRows(string dbPath, string table)
    {
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountTriggers(string dbPath)
    {
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger'";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteMarker(DbContext dbContext, string value)
    {
        using var lease = dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
        command.Parameters.AddWithValue("@key", MarkerKey);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    private static string? ReadMarker(string dbPath)
    {
        using var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key";
        command.Parameters.AddWithValue("@key", MarkerKey);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 指定したテーブルのルートページだけを壊す
    /// </summary>
    /// <remarks>
    /// 1 ページ目（ヘッダと sqlite_master）と <c>schema_migrations</c> は壊さない。トリガー・版の検査は
    /// 通ってしまう壊れ方にして、整合性検査（<c>PRAGMA quick_check</c>）だけが見抜けることを表明する
    /// （全ページを壊すと、版を読む段の例外でも不合格になり、整合性検査を外しても緑になる）。
    /// </remarks>
    private static void CorruptRootPageOf(string dbPath, string tableName)
    {
        long rootPage;
        using (var connection = new SQLiteConnection(DbContext.BuildConnectionString(dbPath)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rootpage FROM sqlite_master WHERE type = 'table' AND name = @name";
            command.Parameters.AddWithValue("@name", tableName);
            rootPage = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        rootPage.Should().BeGreaterThan(1, "前提: 1 ページ目以外のページを壊す");

        using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[100];
        stream.Read(header, 0, header.Length).Should().Be(100);
        var pageSize = (header[16] << 8) | header[17];
        if (pageSize == 1)
        {
            pageSize = 65536;
        }

        stream.Position = (rootPage - 1) * pageSize;
        var garbage = Enumerable.Repeat((byte)0xAB, pageSize).ToArray();
        stream.Write(garbage, 0, garbage.Length);
    }

    private static byte[] ComputeHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return sha.ComputeHash(stream);
    }

    #endregion
}
