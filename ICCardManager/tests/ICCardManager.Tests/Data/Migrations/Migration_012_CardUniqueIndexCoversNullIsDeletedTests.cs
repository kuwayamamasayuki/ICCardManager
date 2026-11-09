#nullable enable
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
    /// Issue #2419: カード種別＋管理番号の部分ユニークインデックスが、<c>is_deleted</c> が NULL の
    /// カード（Issue #2381 以降アプリは有効なカードとして扱う）も対象にすること。
    /// </summary>
    /// <remarks>
    /// Migration_008 のインデックスは <c>WHERE is_deleted = 0</c> だったため、外部ツールで
    /// <c>is_deleted</c> を空（NULL）にしたカードと同じ種別・管理番号のカードを重複して登録・編集できた。
    /// 移行は NULL を 0 へ正規化し、NULL を含めた重複を 008 と同じ方法（番号をずらす）で解消してから、
    /// 条件を <c>WHERE is_deleted = 0 OR is_deleted IS NULL</c> にしてインデックスを作り直す。
    /// </remarks>
    public class Migration_012_CardUniqueIndexCoversNullIsDeletedTests : IDisposable
    {
        private const string IndexName = "idx_card_type_number_active";

        private readonly SQLiteConnection _connection;

        public Migration_012_CardUniqueIndexCoversNullIsDeletedTests()
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
        }

        public void Dispose()
        {
            _connection.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Up_NormalizesNullIsDeletedToZero()
        {
            InsertCard("IDM001", "はやかけん", "1", null);
            InsertCard("IDM002", "nimoca", "1", 0);
            InsertCard("IDM003", "SUGOCA", "1", 1);

            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());

            SelectCards().Should().Equal(
                ("IDM001", "はやかけん", "1", (long?)0),
                ("IDM002", "nimoca", "1", (long?)0),
                ("IDM003", "SUGOCA", "1", (long?)1));
        }

        /// <summary>
        /// 旧インデックスの対象外だった NULL のカードを含む重複があっても、移行は失敗せず番号をずらすこと。
        /// </summary>
        /// <remarks>
        /// 重複が残ったまま <c>CREATE UNIQUE INDEX</c> を実行すると制約違反で移行が失敗し、
        /// アプリが起動できなくなる。ずらし方は Migration_008 と同じ（IDm の昇順で最初の 1 枚が番号を保ち、
        /// 残りはその種別の最大番号＋1）。論理削除済みのカードは重複に数えず、書き換えもしない。
        /// </remarks>
        [Fact]
        public void Up_RenumbersDuplicatesIncludingNullIsDeleted()
        {
            InsertCard("IDM001", "はやかけん", "3", 0);
            InsertCard("IDM002", "はやかけん", "3", null);
            InsertCard("IDM003", "はやかけん", "3", 1);
            InsertCard("IDM004", "はやかけん", "5", 0);

            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());

            SelectCards().Should().Equal(
                ("IDM001", "はやかけん", "3", (long?)0),
                ("IDM002", "はやかけん", "6", (long?)0),
                ("IDM003", "はやかけん", "3", (long?)1),
                ("IDM004", "はやかけん", "5", (long?)0));
        }

        /// <summary>
        /// 作り直したインデックスが NULL のカードも対象にする定義になっていること（作り直しの表明）。
        /// </summary>
        /// <remarks>
        /// 冪等性テスト（<see cref="MigrationIdempotencyTests"/>）は 1 回目と 2 回目を比べるため、
        /// 1 回目で定義が変わったことは検出できない。ic_card のほかのインデックスが移行の前後で
        /// 変わらないことも対で表明する（作り直しの巻き添えで別のインデックスを消していないこと）。
        /// </remarks>
        [Fact]
        public void Up_RecreatesUniqueIndexCoveringNullIsDeleted()
        {
            var before = SelectCardIndexes();
            before.Should().Contain(i => i.Name == IndexName && i.Sql ==
                "CREATE UNIQUE INDEX idx_card_type_number_active ON ic_card(card_type, card_number) WHERE is_deleted = 0",
                "前提: 移行前は Migration_008 の定義であるべき");

            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());

            var after = SelectCardIndexes();
            after.Should().ContainSingle(i => i.Name == IndexName).Which.Sql.Should().Be(
                "CREATE UNIQUE INDEX idx_card_type_number_active ON ic_card(card_type, card_number) WHERE is_deleted = 0 OR is_deleted IS NULL");
            after.Where(i => i.Name != IndexName).Should().Equal(before.Where(i => i.Name != IndexName),
                "作り直すのは種別＋管理番号のインデックスだけであるべき");
        }

        /// <summary>
        /// 移行の後に外部ツールで再び NULL にされたカードも、インデックスが重複を拒否すること。
        /// </summary>
        [Fact]
        public void AfterUp_DuplicateOfNullIsDeletedCardViolatesUniqueIndex()
        {
            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());
            InsertCard("IDM001", "はやかけん", "3", null);

            var act = () => InsertCard("IDM002", "はやかけん", "3", 0);

            act.Should().Throw<SQLiteException>().Which.ResultCode.Should().Be(SQLiteErrorCode.Constraint);
        }

        /// <summary>
        /// 対の表明: 論理削除済みのカードとは、移行後も同じ種別・管理番号を使えること。
        /// </summary>
        [Fact]
        public void AfterUp_DuplicateOfDeletedCardIsAllowed()
        {
            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());
            InsertCard("IDM001", "はやかけん", "3", 1);

            var act = () => InsertCard("IDM002", "はやかけん", "3", 0);

            act.Should().NotThrow();
        }

        /// <summary>
        /// 2 回目の Up() が番号をさらにずらしたりしないこと（共有モードの同時起動、Issue #1285）。
        /// </summary>
        [Fact]
        public void Up_SecondRunKeepsCardsAndIndexUnchanged()
        {
            InsertCard("IDM001", "はやかけん", "3", 0);
            InsertCard("IDM002", "はやかけん", "3", null);
            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());
            var cards = SelectCards();
            var indexes = SelectCardIndexes();

            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());

            SelectCards().Should().Equal(cards);
            SelectCardIndexes().Should().Equal(indexes);
        }

        [Fact]
        public void Down_RestoresMigration008Definition()
        {
            var before = SelectCardIndexes();
            RunOnce(new Migration_012_CardUniqueIndexCoversNullIsDeleted());

            RunDown(new Migration_012_CardUniqueIndexCoversNullIsDeleted());

            SelectCardIndexes().Should().Equal(before, "Down は Migration_008 の定義へ戻すべき");
        }

        private void InsertCard(string idm, string cardType, string cardNumber, int? isDeleted)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"INSERT INTO ic_card (card_idm, card_type, card_number, is_deleted)
VALUES (@idm, @cardType, @cardNumber, @isDeleted)";
            command.Parameters.AddWithValue("@idm", idm);
            command.Parameters.AddWithValue("@cardType", cardType);
            command.Parameters.AddWithValue("@cardNumber", cardNumber);
            command.Parameters.AddWithValue("@isDeleted", isDeleted.HasValue ? (object)isDeleted.Value : DBNull.Value);
            command.ExecuteNonQuery();
        }

        private List<(string Idm, string CardType, string CardNumber, long? IsDeleted)> SelectCards()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT card_idm, card_type, card_number, is_deleted FROM ic_card ORDER BY card_idm";
            using var reader = command.ExecuteReader();
            var rows = new List<(string, string, string, long?)>();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }
            return rows;
        }

        /// <summary>ic_card に付いたインデックスを名前順に返す（定義文の空白は正規化する）。</summary>
        private List<(string Name, string? Sql)> SelectCardIndexes()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'ic_card' ORDER BY name";
            using var reader = command.ExecuteReader();
            var rows = new List<(string, string?)>();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : Regex.Replace(reader.GetString(1), @"\s+", " ")));
            }
            return rows;
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
    }
}
