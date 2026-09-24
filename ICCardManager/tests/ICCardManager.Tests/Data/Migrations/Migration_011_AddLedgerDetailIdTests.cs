using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #2000: <c>ledger_detail</c> へ明示的な主キー <c>id</c> を足す移行が、
    /// <b>既存の rowid 値をそのまま引き継ぐ</b>こと。
    /// </summary>
    /// <remarks>
    /// 値を採番し直すと、既に <c>ledger_merge_history.undo_data_json</c> へ保存済みの
    /// 統合取り消しデータが指す行がずれ、6 年保存の台帳明細が別の台帳へ移る。
    /// FeliCa 互換の「小さい値＝新しい」大小関係（Issue #548 / #880）も同時に壊れる。
    /// </remarks>
    public class Migration_011_AddLedgerDetailIdTests : IDisposable
    {
        private readonly SQLiteConnection _connection;

        public Migration_011_AddLedgerDetailIdTests()
        {
            _connection = new SQLiteConnection("Data Source=:memory:");
            _connection.Open();
            RunOnce(new Migration_001_Initial());
            RunOnce(new Migration_002_AddPointRedemption());
            RunOnce(new Migration_003_AddTripGroupId());
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Up_PreservesExistingRowidValuesAsId()
        {
            SeedLegacyDetailsWithGaps();
            var before = SelectRows("SELECT rowid, ledger_id, amount, balance, bus_stops FROM ledger_detail ORDER BY rowid");
            before.Should().HaveCount(3, "前提: 3 行残っているべき");
            before[0][0].Should().Be(1L);
            before[1][0].Should().Be(3L, "前提: 削除で rowid に穴が空いているべき");
            before[2][0].Should().Be(5L);

            RunOnce(new Migration_011_AddLedgerDetailId());

            var after = SelectRows("SELECT id, ledger_id, amount, balance, bus_stops FROM ledger_detail ORDER BY id");
            after.Should().BeEquivalentTo(before,
                "移行は現在の rowid を id へそのまま写し、他の列も変えないべき");
        }

        [Fact]
        public void Up_MakesIdSurviveVacuum()
        {
            SeedLegacyDetailsWithGaps();
            RunOnce(new Migration_011_AddLedgerDetailId());

            Execute("VACUUM");

            var ids = SelectRows("SELECT id FROM ledger_detail ORDER BY id");
            ids.Should().HaveCount(3);
            new[] { ids[0][0], ids[1][0], ids[2][0] }.Should().Equal(new object[] { 1L, 3L, 5L },
                "id は INTEGER PRIMARY KEY（rowid の別名）なので VACUUM で詰め直されないべき");
        }

        /// <summary>
        /// 対の表明（検出力の担保）: 移行前の形（暗黙 rowid）では、同じ VACUUM が実際に値を詰め直すこと。
        /// </summary>
        /// <remarks>
        /// <para>
        /// これが無いと、VACUUM が何もしない環境でも <see cref="Up_MakesIdSurviveVacuum"/> が緑になり、
        /// 移行を丸ごと止めた実装を検出できない。
        /// </para>
        /// <para>
        /// <b>インデックスを落としてから VACUUM する</b>のは、SQLite が「インデックスを 1 つでも持つテーブル」の
        /// 暗黙 rowid を実装上は保存するため（インデックス項目が rowid を参照する）。
        /// <see cref="WithoutMigration_VacuumPreservesRowids_OnlyBecauseIndexesExist"/> がその条件を固定している。
        /// </para>
        /// </remarks>
        [Fact]
        public void WithoutMigration_VacuumRenumbersRowids_WhenIndexesAreAbsent()
        {
            SeedLegacyDetailsWithGaps();
            Execute("DROP INDEX idx_detail_ledger");
            Execute("DROP INDEX idx_detail_bus");

            Execute("VACUUM");

            var rowids = SelectRows("SELECT rowid FROM ledger_detail ORDER BY rowid");
            new[] { rowids[0][0], rowids[1][0], rowids[2][0] }.Should().Equal(new object[] { 1L, 2L, 3L },
                "明示的な主キーを持たないテーブルの暗黙 rowid は VACUUM で詰め直される（本 Issue の前提）");
        }

        /// <summary>
        /// 移行前の <c>ledger_detail</c> が VACUUM を耐えていたのは、
        /// <b>どこにも宣言されていない「インデックスがある」という条件</b>のおかげでしかなかったこと。
        /// </summary>
        /// <remarks>
        /// SQLite のドキュメントは「INTEGER PRIMARY KEY を持たないテーブルの rowid は VACUUM で
        /// 変わり<b>得る</b>」としか約束しない。実装は現在（3.45 系）インデックスを持つテーブルの rowid を
        /// 保存するが、これは契約ではない。<see cref="WithoutMigration_VacuumRenumbersRowids_WhenIndexesAreAbsent"/>
        /// のとおり、インデックスを 1 つ落とせばその瞬間に振り直しが始まる。
        /// 本移行は、この偶然を SQLite が契約として保証する形（<c>INTEGER PRIMARY KEY</c>）へ置き換える。
        /// このテストは「欠陥が潜在にとどまっていた理由」を記録として固定するものであり、
        /// この挙動に依存してよいという表明ではない。
        /// </remarks>
        [Fact]
        public void WithoutMigration_VacuumPreservesRowids_OnlyBecauseIndexesExist()
        {
            SeedLegacyDetailsWithGaps();

            Execute("VACUUM");

            var rowids = SelectRows("SELECT rowid FROM ledger_detail ORDER BY rowid");
            new[] { rowids[0][0], rowids[1][0], rowids[2][0] }.Should().Equal(new object[] { 1L, 3L, 5L },
                "インデックスを持つテーブルの rowid は現行の SQLite では保存される（保証ではなく実装の都合）");
        }

        [Fact]
        public void Down_RestoresImplicitRowidTableKeepingValues()
        {
            SeedLegacyDetailsWithGaps();
            RunOnce(new Migration_011_AddLedgerDetailId());

            RunDown(new Migration_011_AddLedgerDetailId());

            MigrationHelpers.HasColumn(_connection, null, "ledger_detail", "id")
                .Should().BeFalse("Down は id 列を取り除くべき");
            var rowids = SelectRows("SELECT rowid, amount FROM ledger_detail ORDER BY rowid");
            new[] { rowids[0][0], rowids[1][0], rowids[2][0] }.Should().Equal(new object[] { 1L, 3L, 5L },
                "Down でも値は保つべき（再採番すると既存の取り消しデータが指す行がずれる）");
        }

        /// <summary>
        /// Issue #2107: 移行はテーブルの作り直しで消えたインデックスを、移行前と同じ定義で作り直すこと。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>DROP TABLE ledger_detail</c> はインデックスも一緒に消すため、作り直しを落とすと
        /// 明細の取得（<c>WHERE ledger_id = …</c>）が全件走査になるうえ、<see cref="WithoutMigration_VacuumPreservesRowids_OnlyBecauseIndexesExist"/>
        /// のとおり「インデックスがある」ことは移行前の DB で rowid を守っていた条件でもある。
        /// 作り直しの 2 行を消しても、ほかの全テストは緑のままだった。
        /// </para>
        /// <para>
        /// 比較の基準は<b>直前の版（010）まで適用した実際のスキーマ</b>から取る。名前を手で列挙すると、
        /// 004 以降が ledger_detail にインデックスを足したときに、そのインデックスの消失を見逃す。
        /// 基準が空でないことを前提として表明するのは、両側とも空なら一致して緑になるため。
        /// </para>
        /// </remarks>
        [Fact]
        public void Up_RecreatesLedgerDetailIndexesAsBeforeMigration()
        {
            ApplyMigrationsUpTo010();
            SeedLegacyDetailsWithGaps();
            var before = SelectLedgerDetailIndexes();
            before.Select(i => i.Name).Should().Contain(
                new[] { "idx_detail_bus", "idx_detail_ledger" },
                "前提: 移行前の ledger_detail は明細の検索用インデックスを持っているべき");

            RunOnce(new Migration_011_AddLedgerDetailId());

            SelectLedgerDetailIndexes().Should().Equal(before,
                "テーブルを作り直しても、インデックスは名前・定義とも移行前と同じであるべき");
        }

        /// <summary>
        /// Issue #2107: Down も同じくインデックスを作り直すこと（Up と対）。
        /// </summary>
        [Fact]
        public void Down_RecreatesLedgerDetailIndexesAsBeforeMigration()
        {
            ApplyMigrationsUpTo010();
            SeedLegacyDetailsWithGaps();
            var before = SelectLedgerDetailIndexes();
            RunOnce(new Migration_011_AddLedgerDetailId());

            RunDown(new Migration_011_AddLedgerDetailId());

            SelectLedgerDetailIndexes().Should().Equal(before,
                "Down で作り直したテーブルも、移行前と同じインデックスを持つべき");
        }

        /// <summary>コンストラクターが適用した 001〜003 に続けて、004〜010 を適用する。</summary>
        private void ApplyMigrationsUpTo010()
        {
            RunOnce(new Migration_004_AddPerformanceIndexes());
            RunOnce(new Migration_005_AddStartingPageNumber());
            RunOnce(new Migration_006_AddRefundedStatus());
            RunOnce(new Migration_007_AddMergeHistory());
            RunOnce(new Migration_008_AddCardTypeNumberUniqueIndex());
            RunOnce(new Migration_009_AddCarryoverTotals());
            RunOnce(new Migration_010_AddCompanionCount());
        }

        /// <summary>
        /// ledger_detail に付いたインデックスを名前順に返す（自動生成のものは sql が NULL）。
        /// </summary>
        /// <remarks>
        /// 定義文の空白は正規化する。001 は列をそろえるために空白を詰めて書いており、
        /// 作り直した定義と文字列としては一致しないが、定義としては同じである。
        /// </remarks>
        private List<(string Name, string? Sql)> SelectLedgerDetailIndexes()
        {
            return SelectRows(
                    "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'ledger_detail' ORDER BY name")
                .Select(row => ((string)row[0], row[1] is string sql ? Regex.Replace(sql, @"\s+", " ") : null))
                .ToList();
        }

        /// <summary>
        /// 5 行入れて 2 行消し、rowid が 1 / 3 / 5 に歯抜けした状態を作る
        /// （実運用で <c>ReplaceDetailsAsync</c> の DELETE + INSERT や台帳削除が作る形）。
        /// </summary>
        private void SeedLegacyDetailsWithGaps()
        {
            Execute(@"INSERT INTO ledger (id, card_idm, date, summary, income, expense, balance)
VALUES (1, '0102030405060708', '2026-04-01 09:00:00', '鉄道（A駅～B駅）', 0, 210, 1000)");

            for (var i = 1; i <= 5; i++)
            {
                Execute(@"INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station, bus_stops,
                                                    amount, balance, is_charge, is_point_redemption, is_bus, group_id)
VALUES (1, '2026-04-01 09:00:00', 'A駅', 'B駅', NULL, @amount, @balance, 0, 0, 0, NULL)",
                    ("@amount", 200 + i), ("@balance", 1000 - (i * 10)));
            }

            Execute("DELETE FROM ledger_detail WHERE rowid IN (2, 4)");
        }

        private void RunOnce(IMigration migration)
        {
            using var tx = _connection.BeginTransaction();
            migration.Up(_connection, tx);
            tx.Commit();
        }

        private void RunDown(IMigration migration)
        {
            using var tx = _connection.BeginTransaction();
            migration.Down(_connection, tx);
            tx.Commit();
        }

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            command.ExecuteNonQuery();
        }

        private List<object[]> SelectRows(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
            return rows;
        }
    }
}
