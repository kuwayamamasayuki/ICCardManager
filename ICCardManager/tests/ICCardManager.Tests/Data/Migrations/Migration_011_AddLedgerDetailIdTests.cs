using System;
using System.Collections.Generic;
using System.Data.SQLite;
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
