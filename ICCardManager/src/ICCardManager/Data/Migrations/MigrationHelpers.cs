using System;
using System.Data.SQLite;
using System.Text.RegularExpressions;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// マイグレーション実装で冪等な SQL 操作を提供するヘルパー（Issue #1285）。
    /// </summary>
    /// <remarks>
    /// SQLite の <c>ALTER TABLE ADD COLUMN</c> は二重実行時に "duplicate column" エラーを出すため、
    /// <c>PRAGMA table_info()</c> で事前に列の有無を確認する方式で冪等化する。
    ///
    /// 識別子・型句のパラメータ化は SQLite ではサポートされないため、文字列補間で構築する。
    /// 開発者が誤って外部入力相当の値を渡した場合の保険として、Issue #1466 で
    /// ホワイトリスト regex による検証層を追加した。
    /// </remarks>
    internal static class MigrationHelpers
    {
        private static readonly Regex IdentifierPattern =
            new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static readonly Regex TypeAndConstraintsPattern =
            new Regex(
                @"^(INTEGER|TEXT|REAL|BLOB|NUMERIC)" +
                @"(\s+(NOT\s+NULL" +
                @"|DEFAULT\s+(-?\d+(\.\d+)?|'[^';]*'|NULL)" +
                @"|REFERENCES\s+[A-Za-z_][A-Za-z0-9_]*\([A-Za-z_][A-Za-z0-9_]*\)" +
                @"))*\s*$",
                RegexOptions.Compiled);

        /// <summary>
        /// 指定テーブルに指定列が存在するかを返す。
        /// </summary>
        public static bool HasColumn(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string table,
            string column)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            EnsureValidIdentifier(table, nameof(table));
            EnsureValidIdentifier(column, nameof(column));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 列が存在しない場合のみ <c>ALTER TABLE ... ADD COLUMN</c> を実行する（冪等）。
        /// </summary>
        /// <param name="connection">対象データベースへの開いた接続</param>
        /// <param name="transaction">実行中のトランザクション</param>
        /// <param name="table">対象テーブル名</param>
        /// <param name="column">追加する列名</param>
        /// <param name="typeAndConstraints">例: <c>"INTEGER DEFAULT 0"</c>, <c>"TEXT"</c></param>
        public static void AddColumnIfNotExists(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string table,
            string column,
            string typeAndConstraints)
        {
            EnsureValidTypeAndConstraints(typeAndConstraints);

            if (HasColumn(connection, transaction, table, column))
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndConstraints}";
            command.ExecuteNonQuery();
        }

        private static void EnsureValidIdentifier(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"{paramName} は空にできません。" +
                    "`[A-Za-z_][A-Za-z0-9_]*` の識別子を渡してください。",
                    paramName);
            }

            if (!IdentifierPattern.IsMatch(value))
            {
                throw new ArgumentException(
                    $"{paramName} '{value}' は SQLite 識別子として不正です。" +
                    "英字または '_' で始まり、英数字または '_' のみで構成される必要があります " +
                    "（regex: `[A-Za-z_][A-Za-z0-9_]*`）。",
                    paramName);
            }
        }

        private static void EnsureValidTypeAndConstraints(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "typeAndConstraints は空にできません。" +
                    "`INTEGER` / `TEXT` / `REAL` / `BLOB` / `NUMERIC` のいずれかに " +
                    "`NOT NULL` / `DEFAULT <値>` / `REFERENCES <table>(<col>)` を組み合わせて指定してください。",
                    nameof(value));
            }

            if (!TypeAndConstraintsPattern.IsMatch(value))
            {
                throw new ArgumentException(
                    $"typeAndConstraints '{value}' は許可された構文に一致しません。" +
                    "型は `INTEGER` / `TEXT` / `REAL` / `BLOB` / `NUMERIC` のいずれか、" +
                    "制約は `NOT NULL` / `DEFAULT <整数|小数|'literal'|NULL>` / " +
                    "`REFERENCES <table>(<col>)` の組み合わせのみ受理されます。",
                    nameof(value));
            }
        }
    }
}
