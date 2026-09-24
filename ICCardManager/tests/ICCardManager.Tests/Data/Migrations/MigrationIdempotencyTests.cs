using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #1285: 全マイグレーションの Up() が二重実行に対して安全（冪等）であることを検証。
    /// </summary>
    /// <remarks>
    /// <para>
    /// MigrationRunner は schema_migrations でバージョン管理するが、共有モードでの競合や
    /// 部分適用状態を想定し、各マイグレーション自体の Up() が二重適用でも例外を投げないことを担保する。
    /// テストは MigrationRunner を介さず、Migration のインスタンスを作って直接 Up() を 2 回呼ぶ。
    /// </para>
    /// <para>
    /// Issue #2107: 以前は対象を手で列挙し、検証も「例外が出ない」だけで、しかも空のテーブルに対して
    /// 流していた。そのため ①新しいマイグレーションを列挙し忘れても検出できない、②2 回目の Up() が
    /// 行を書き換える・重複させる・インデックスを落とすといった「例外にならない非冪等」を検出できなかった。
    /// 対象は本番と同じ <see cref="MigrationRunner"/> の自動検出から導出し、行を入れた DB で
    /// 「2 回目の Up() の前後でスキーマとデータが一致すること」を表明する。
    /// </para>
    /// </remarks>
    public class MigrationIdempotencyTests
    {
        /// <summary>本番の <see cref="MigrationRunner"/> が自動検出する全マイグレーションのバージョン。</summary>
        public static IEnumerable<object[]> AllMigrationVersions()
            => DiscoverMigrations().Select(m => new object[] { m.Version });

        /// <summary>
        /// 対象の導出が空振りしていないこと（既知のバージョンが実際に拾えること）
        /// </summary>
        /// <remarks>
        /// Theory のデータが 0 件になると、テストは 1 件も実行されず緑になる（testing.md #1786）。
        /// 版が 1 から欠番なく並ぶことも併せて表明し、自動検出と Theory のデータが同じ集合であることを固定する。
        /// </remarks>
        [Fact]
        public void AllMigrationVersions_ContainsEveryDiscoveredMigrationFromVersion1()
        {
            var versions = AllMigrationVersions().Select(row => (int)row[0]).ToList();

            versions.Should().Contain(new[] { 1, 11 }, "既知の最初と #2000 の版が自動検出に含まれているべき");
            versions.Should().Equal(Enumerable.Range(1, versions.Count),
                "版は 1 から欠番なく並び、Theory はそのすべてを対象にするべき");
        }

        [Theory]
        [MemberData(nameof(AllMigrationVersions))]
        public void Up_SecondRunDoesNotChangeSchemaOrData(int version)
        {
            using var connection = new SQLiteConnection("Data Source=:memory:");
            connection.Open();
            var migrations = DiscoverMigrations();
            var target = migrations.Single(m => m.Version == version);

            foreach (var predecessor in migrations.Where(m => m.Version < version))
            {
                RunOnce(connection, predecessor);
            }
            RunOnce(connection, target);
            SeedRows(connection);
            var before = Snapshot(connection);
            EmptyTables(connection).Should().BeEmpty(
                "前提: すべてのテーブルに行を入れておくこと（空のテーブルでは行の重複・書き換えを検出できない。新しいテーブルを足したら SeedRows にも足す）");

            Action secondRun = () => RunOnce(connection, target);

            secondRun.Should().NotThrow($"Migration {version:000} の Up() は二重実行しても失敗しないべき");
            Snapshot(connection).Should().Equal(before,
                $"Migration {version:000} の 2 回目の Up() はスキーマ（テーブル・列・インデックス）も既存の行も変えないべき");
        }

        /// <summary>
        /// 対の表明（検出力の担保）: スナップショットはインデックスの消失と行の変化を実際に区別できること
        /// </summary>
        /// <remarks>
        /// スナップショットが何も拾っていなければ、上の Theory はどんな非冪等でも緑になる。
        /// </remarks>
        [Fact]
        public void Snapshot_DetectsDroppedIndexAndChangedRow()
        {
            using var connection = new SQLiteConnection("Data Source=:memory:");
            connection.Open();
            foreach (var migration in DiscoverMigrations())
            {
                RunOnce(connection, migration);
            }
            SeedRows(connection);
            var original = Snapshot(connection);

            Execute(connection, "DROP INDEX idx_detail_ledger");
            var withoutIndex = Snapshot(connection);
            Execute(connection, "CREATE INDEX idx_detail_ledger ON ledger_detail(ledger_id)");
            Execute(connection, "UPDATE ledger_detail SET amount = amount + 1");
            var withChangedRow = Snapshot(connection);

            withoutIndex.Should().NotEqual(original, "インデックスの消失を検出できるべき");
            withChangedRow.Should().NotEqual(original, "行の変化を検出できるべき");
        }

        private static List<IMigration> DiscoverMigrations()
        {
            using var connection = new SQLiteConnection("Data Source=:memory:");
            connection.Open();
            return new MigrationRunner(connection).GetPendingMigrations().ToList();
        }

        private static void RunOnce(SQLiteConnection connection, IMigration migration)
        {
            using var tx = connection.BeginTransaction();
            migration.Up(connection, tx);
            tx.Commit();
        }

        /// <summary>
        /// 初期スキーマ（001）の列だけを使って主要テーブルへ行を入れる。
        /// </summary>
        /// <remarks>
        /// 001 の列は以降の版でも残るため、どの版を適用した後でも同じ SQL で入れられる。
        /// 後の版で増えるテーブル（007 の ledger_merge_history）は、そのテーブルがあるときだけ入れる。
        /// </remarks>
        private static void SeedRows(SQLiteConnection connection)
        {
            Execute(connection, "INSERT INTO staff (staff_idm, name, number) VALUES ('1111111111111111', '博多 花子', '001')");
            Execute(connection, @"INSERT INTO ic_card (card_idm, card_type, card_number, is_lent)
VALUES ('0102030405060708', 'はやかけん', '001', 0)");
            Execute(connection, @"INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance, staff_name)
VALUES ('0102030405060708', '1111111111111111', '2026-04-01 00:00:00', '鉄道（天神～博多）', 0, 210, 790, '博多 花子')");
            Execute(connection, @"INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station, amount, balance, is_charge, is_bus)
VALUES ((SELECT MAX(id) FROM ledger), '2026-04-01 00:00:00', '天神', '博多', 210, 790, 0, 0)");
            Execute(connection, "INSERT INTO settings (key, value) VALUES ('idempotency_probe', 'seeded')");
            Execute(connection, @"INSERT INTO operation_log (timestamp, operator_idm, operator_name, target_table, target_id, action)
VALUES ('2026-04-01 09:00:00', '1111111111111111', '博多 花子', 'ledger', '1', 'INSERT')");
            if (TableExists(connection, "ledger_merge_history"))
            {
                Execute(connection, @"INSERT INTO ledger_merge_history (merged_at, target_ledger_id, description, undo_data)
VALUES ('2026-04-02 09:00:00', (SELECT MAX(id) FROM ledger), '統合', '{}')");
            }
        }

        private static bool TableExists(SQLiteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
            command.Parameters.AddWithValue("@name", table);
            return (long)command.ExecuteScalar() > 0;
        }

        /// <summary>行が 1 件も無いテーブルの名前（SQLite の内部テーブルは除く）。</summary>
        private static List<string> EmptyTables(SQLiteConnection connection)
        {
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            return tables.Where(table =>
            {
                using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                return (long)count.ExecuteScalar() == 0;
            }).ToList();
        }

        /// <summary>
        /// スキーマ（sqlite_master の全定義）と全テーブルの全行を、比較できる文字列の列へ写す。
        /// </summary>
        private static List<string> Snapshot(SQLiteConnection connection)
        {
            var snapshot = new List<string>();
            var tables = new List<string>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var type = reader.GetString(0);
                    var name = reader.GetString(1);
                    snapshot.Add($"schema|{type}|{name}|{reader.GetString(2)}|{(reader.IsDBNull(3) ? "" : reader.GetString(3))}");
                    if (type == "table")
                    {
                        tables.Add(name);
                    }
                }
            }

            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    snapshot.Add($"row|{table}|{string.Join("|", values.Select(v => v is DBNull ? "NULL" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))}");
                }
            }

            return snapshot;
        }

        private static void Execute(SQLiteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
