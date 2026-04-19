using System;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// マイグレーション実装で冪等な SQL 操作を提供するヘルパー（Issue #1285）。
    /// </summary>
    /// <remarks>
    /// SQLite の <c>ALTER TABLE ADD COLUMN</c> は二重実行時に "duplicate column" エラーを出すため、
    /// <c>PRAGMA table_info()</c> で事前に列の有無を確認する方式で冪等化する。
    /// </remarks>
    internal static class MigrationHelpers
    {
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
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("table must be non-empty", nameof(table));
            if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column must be non-empty", nameof(column));

            // PRAGMA は識別子のパラメータ化をサポートしないため、表名は文字列リテラル前提。
            // 外部入力でないことを前提とするが、念のため不正文字を拒否する。
            if (table.IndexOfAny(new[] { '\'', '"', ';', ' ' }) >= 0)
            {
                throw new ArgumentException($"invalid table name: {table}", nameof(table));
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info の 2 列目（index 1）が column name
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
        /// <param name="typeAndConstraints">例: <c>"INTEGER DEFAULT 0"</c>, <c>"TEXT"</c></param>
        public static void AddColumnIfNotExists(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string table,
            string column,
            string typeAndConstraints)
        {
            if (HasColumn(connection, transaction, table, column))
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndConstraints}";
            command.ExecuteNonQuery();
        }
    }
}
