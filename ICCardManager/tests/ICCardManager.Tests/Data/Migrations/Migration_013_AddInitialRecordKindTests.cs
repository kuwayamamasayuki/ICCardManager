#nullable enable
using System;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Data.Migrations
{
    /// <summary>
    /// Issue #2371: 台帳に導入行の種別（<c>initial_record_kind</c>）を足し、既存の導入行を摘要から埋めること。
    /// </summary>
    /// <remarks>
    /// 照合は現在の組織設定の文言と既定の文言の両方（文言を変える前に登録した行は既定の文言、変えた後は現在の文言を持つ）。
    /// 通常の行・貸出中レコードは埋めない。「欠陥を突く側」（文言を変えた組織の旧文言の行も埋まる）と
    /// 「正当な動作を塞いでいない側」（通常の行は埋まらない）を対で置く。
    /// </remarks>
    public class Migration_013_AddInitialRecordKindTests : IDisposable
    {
        private readonly SQLiteConnection _connection;

        public Migration_013_AddInitialRecordKindTests()
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
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Up_既定の文言の導入行を3種とも埋めること()
        {
            var purchase = InsertLedger("新規購入");
            var midYear = InsertLedger("5月から繰越");
            var fiscalYearStart = InsertLedger("前年度より繰越");

            RunOnce(new Migration_013_AddInitialRecordKind());

            SelectKind(purchase).Should().Be((long)InitialRecordKind.NewPurchase);
            SelectKind(midYear).Should().Be((long)InitialRecordKind.MidYearCarryover);
            SelectKind(fiscalYearStart).Should().Be((long)InitialRecordKind.FiscalYearStartCarryover);
        }

        /// <summary>
        /// 組織設定で繰越の文言を変えた組織では、変更前に登録した行（既定の文言）と変更後に登録した行（現在の文言）が混在する。
        /// 両方とも導入行として埋めること。現在の文言だけと照合すると、変更前の行が導入行でなくなる（#2371 の故障の形）。
        /// </summary>
        [Fact]
        public void Up_文言を変えた組織では現在の文言と既定の文言の両方の導入行を埋めること()
        {
            var current = new SummaryTextOptions
            {
                CarryoverFromPreviousYear = "前年より持越",
                MidYearCarryoverPattern = @"^(1[0-2]|[1-9])月分より持越$",
            };
            var beforeChange = InsertLedger("前年度より繰越");
            var afterChange = InsertLedger("前年より持越");
            var midYearBeforeChange = InsertLedger("7月から繰越");
            var midYearAfterChange = InsertLedger("7月分より持越");

            RunOnce(new Migration_013_AddInitialRecordKind { CurrentSummaryText = current });

            SelectKind(beforeChange).Should().Be((long)InitialRecordKind.FiscalYearStartCarryover);
            SelectKind(afterChange).Should().Be((long)InitialRecordKind.FiscalYearStartCarryover);
            SelectKind(midYearBeforeChange).Should().Be((long)InitialRecordKind.MidYearCarryover);
            SelectKind(midYearAfterChange).Should().Be((long)InitialRecordKind.MidYearCarryover);
        }

        [Fact]
        public void Up_通常の行と貸出中レコードは埋めないこと()
        {
            var usage = InsertLedger("鉄道（博多～天神）");
            var charge = InsertLedger("役務費によりチャージ");
            var nextYear = InsertLedger("次年度へ繰越");
            var monthCarryover = InsertLedger("4月より繰越");
            var invalidMonth = InsertLedger("13月から繰越");
            var lent = InsertLedger("新規購入", isLentRecord: true);

            RunOnce(new Migration_013_AddInitialRecordKind());

            SelectKind(usage).Should().BeNull();
            SelectKind(charge).Should().BeNull();
            SelectKind(nextYear).Should().BeNull();
            SelectKind(monthCarryover).Should().BeNull();
            SelectKind(invalidMonth).Should().BeNull("既定のパターンは 1〜12 月だけ");
            SelectKind(lent).Should().BeNull("貸出中レコードは導入行ではない");
        }

        /// <summary>
        /// MigrationRunner は DbContext から渡された現在の文言を 013 へ渡すこと（渡し損ねると既定の文言だけで埋まる）。
        /// </summary>
        [Fact]
        public void MigrationRunner_現在の文言を013へ渡すこと()
        {
            var current = new SummaryTextOptions { CarryoverFromPreviousYear = "前年より持越" };
            var afterChange = InsertLedger("前年より持越");

            var runner = new MigrationRunner(_connection, current);
            runner.MigrateTo(13);

            SelectKind(afterChange).Should().Be((long)InitialRecordKind.FiscalYearStartCarryover);
        }

        private long InsertLedger(string summary, bool isLentRecord = false)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"INSERT INTO ledger (card_idm, date, summary, income, expense, balance, is_lent_record)
VALUES ('IDM001', '2026-04-01 00:00:00', @summary, 0, 0, 1000, @isLentRecord);
SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("@summary", summary);
            command.Parameters.AddWithValue("@isLentRecord", isLentRecord ? 1 : 0);
            return (long)command.ExecuteScalar();
        }

        private long? SelectKind(long id)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT initial_record_kind FROM ledger WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            var value = command.ExecuteScalar();
            return value is DBNull or null ? null : (long?)Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private void RunOnce(IMigration migration)
        {
            using var tx = _connection.BeginTransaction();
            migration.Up(_connection, tx);
            tx.Commit();
        }
    }
}
