using System.IO;
using Microsoft.Data.Sqlite;

namespace ICCardManager.Data;

/// <summary>
/// SQLiteデータベース接続管理クラス
/// </summary>
public class DbContext : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// データベースファイルのパス
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="databasePath">データベースファイルのパス（省略時はアプリフォルダ内）</param>
    public DbContext(string? databasePath = null)
    {
        DatabasePath = databasePath ?? GetDefaultDatabasePath();
        _connectionString = $"Data Source={DatabasePath}";
    }

    /// <summary>
    /// デフォルトのデータベースパスを取得
    /// </summary>
    private static string GetDefaultDatabasePath()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ICCardManager");

        Directory.CreateDirectory(appDataPath);
        return Path.Combine(appDataPath, "iccard.db");
    }

    /// <summary>
    /// データベース接続を取得
    /// </summary>
    public SqliteConnection GetConnection()
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            // 外部キー制約を有効化
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
        }

        return _connection;
    }

    /// <summary>
    /// データベースを初期化（テーブル作成）
    /// </summary>
    public void InitializeDatabase()
    {
        var connection = GetConnection();

        // schema.sqlの内容を埋め込みリソースまたは直接実行
        var schema = GetSchemaScript();

        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// スキーマスクリプトを取得
    /// </summary>
    private static string GetSchemaScript()
    {
        // 埋め込みリソースとして読み込む場合
        var assembly = typeof(DbContext).Assembly;
        var resourceName = "ICCardManager.Data.schema.sql";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // 埋め込みリソースがない場合は直接スキーマを返す
        return GetInlineSchema();
    }

    /// <summary>
    /// インラインスキーマ定義（フォールバック用）
    /// </summary>
    private static string GetInlineSchema()
    {
        return """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS staff (
                staff_idm  TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                number     TEXT,
                note       TEXT,
                is_deleted INTEGER DEFAULT 0,
                deleted_at TEXT
            );

            CREATE TABLE IF NOT EXISTS ic_card (
                card_idm        TEXT PRIMARY KEY,
                card_type       TEXT NOT NULL,
                card_number     TEXT NOT NULL,
                note            TEXT,
                is_deleted      INTEGER DEFAULT 0,
                deleted_at      TEXT,
                is_lent         INTEGER DEFAULT 0,
                last_lent_at    TEXT,
                last_lent_staff TEXT REFERENCES staff(staff_idm)
            );

            CREATE TABLE IF NOT EXISTS ledger (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                card_idm       TEXT    NOT NULL REFERENCES ic_card(card_idm),
                lender_idm     TEXT    REFERENCES staff(staff_idm),
                date           TEXT    NOT NULL,
                summary        TEXT    NOT NULL,
                income         INTEGER DEFAULT 0,
                expense        INTEGER DEFAULT 0,
                balance        INTEGER NOT NULL,
                staff_name     TEXT,
                note           TEXT,
                returner_idm   TEXT,
                lent_at        TEXT,
                returned_at    TEXT,
                is_lent_record INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS ledger_detail (
                ledger_id     INTEGER REFERENCES ledger(id) ON DELETE CASCADE,
                use_date      TEXT,
                entry_station TEXT,
                exit_station  TEXT,
                bus_stops     TEXT,
                amount        INTEGER,
                balance       INTEGER,
                is_charge     INTEGER DEFAULT 0,
                is_bus        INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS operation_log (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp     TEXT DEFAULT CURRENT_TIMESTAMP,
                operator_idm  TEXT NOT NULL,
                operator_name TEXT NOT NULL,
                target_table  TEXT,
                target_id     TEXT,
                action        TEXT,
                before_data   TEXT,
                after_data    TEXT
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_staff_deleted      ON staff(is_deleted);
            CREATE INDEX IF NOT EXISTS idx_card_deleted       ON ic_card(is_deleted);
            CREATE INDEX IF NOT EXISTS idx_ledger_date        ON ledger(date);
            CREATE INDEX IF NOT EXISTS idx_ledger_summary     ON ledger(summary);
            CREATE INDEX IF NOT EXISTS idx_ledger_card_date   ON ledger(card_idm, date);
            CREATE INDEX IF NOT EXISTS idx_ledger_lender      ON ledger(lender_idm);
            CREATE INDEX IF NOT EXISTS idx_detail_ledger      ON ledger_detail(ledger_id);
            CREATE INDEX IF NOT EXISTS idx_detail_bus         ON ledger_detail(is_bus);
            CREATE INDEX IF NOT EXISTS idx_log_timestamp      ON operation_log(timestamp);

            INSERT OR IGNORE INTO settings (key, value) VALUES ('warning_balance', '10000');
            INSERT OR IGNORE INTO settings (key, value) VALUES ('font_size', 'medium');
            """;
    }

    /// <summary>
    /// 6年経過したデータを削除
    /// </summary>
    public int CleanupOldData()
    {
        var connection = GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ledger WHERE date < date('now', '-6 years')";
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// VACUUMを実行してデータベースを最適化
    /// </summary>
    public void Vacuum()
    {
        var connection = GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// トランザクションを開始
    /// </summary>
    public SqliteTransaction BeginTransaction()
    {
        return GetConnection().BeginTransaction();
    }

    /// <summary>
    /// リソースを解放
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// リソースを解放（内部実装）
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _connection?.Dispose();
            }
            _disposed = true;
        }
    }
}
