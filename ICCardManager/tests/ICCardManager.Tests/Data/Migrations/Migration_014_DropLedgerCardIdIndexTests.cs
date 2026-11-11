#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #2387: 当初の用途が無くなった <c>idx_ledger_card_id</c> を削除し、ほかのインデックスは変えないこと。
    /// </summary>
    /// <remarks>
    /// <c>card_idm</c> の等値条件の問い合わせが削除後も全件走査にならないこと（<c>idx_ledger_card_date</c> を使うこと）は
    /// <c>LedgerCardIdmQueryPlanTests</c> が対で押さえる。
    /// </remarks>
    public class Migration_014_DropLedgerCardIdIndexTests : IDisposable
    {
        private const string IndexName = "idx_ledger_card_id";

        private readonly SQLiteConnection _connection;

        public Migration_014_DropLedgerCardIdIndexTests()
        {
            _connection = new SQLiteConnection("Data Source=:memory:");
            _connection.Open();
            RunOnce(new Migration_001_Initial());
            RunOnce(new Migration_002_AddPointRedemption());
            RunOnce(new Migration_003_AddTripGroupId());
            RunOnce(new Migration_004_AddPerformanceIndexes());
            RunOnce(new Migration_005_AddStartingPageNumber());
            RunOnce(new Migration_006_AddRefundedStatus());
            RunOnce(new Migration_007_AddMergeHistory());
            RunOnce(new Migration_008_AddCardTypeNumberUniqueIndex());
            RunOnce(new Migration_009_AddCarryoverTotals());
            RunOnce(new Migration_010_AddCompanionCount());
            RunOnce(new Migration_011_AddLedgerDetailId());
            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());
            RunOnce(new Migration_013_AddInitialRecordKind());
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Up_idx_ledger_card_idを削除しほかのインデックスは変えないこと()
        {
            var before = SelectIndexes();
            before.Should().ContainKey(IndexName, "前提: Migration_004 が作ったインデックスがある");

            RunOnce(new Migration_014_DropLedgerCardIdIndex());

            var after = SelectIndexes();
            after.Should().NotContainKey(IndexName);
            before.Remove(IndexName);
            after.Should().Equal(before, "削除するのは idx_ledger_card_id だけ");
            after.Should().ContainKey("idx_ledger_card_date", "card_idm の等値条件はこのインデックスで引く");
        }

        [Fact]
        public void Up_2回実行しても失敗しないこと()
        {
            RunOnce(new Migration_014_DropLedgerCardIdIndex());

            Action second = () => RunOnce(new Migration_014_DropLedgerCardIdIndex());

            second.Should().NotThrow();
            SelectIndexes().Should().NotContainKey(IndexName);
        }

        [Fact]
        public void Down_Migration_004と同じ定義で作り直すこと()
        {
            var definition = SelectIndexes()[IndexName];
            RunOnce(new Migration_014_DropLedgerCardIdIndex());

            using (var tx = _connection.BeginTransaction())
            {
                new Migration_014_DropLedgerCardIdIndex().Down(_connection, tx);
                tx.Commit();
            }

            SelectIndexes().Should().ContainKey(IndexName)
                .WhoseValue.Should().Be(definition);
        }

        private Dictionary<string, string> SelectIndexes()
        {
            var result = new Dictionary<string, string>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0), reader.GetString(1));
            }

            return result;
        }

        private void RunOnce(IMigration migration)
        {
            using var tx = _connection.BeginTransaction();
            migration.Up(_connection, tx);
            tx.Commit();
        }
    }
}
