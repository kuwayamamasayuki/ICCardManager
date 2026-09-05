using System;
using System.Collections.Generic;
using System.Linq;
using DebugDataViewer;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// DebugDataViewer のテーブル一覧（<see cref="DebugTableCatalog"/>）の回帰（Issue #2012）。
    /// </summary>
    /// <remarks>
    /// 期待値をリテラルで並べると、テーブルが増えたときに追随できない（#1764）。
    /// 実際に <c>InitializeDatabase()</c> した DB の <c>sqlite_master</c> から導出して突き合わせる。
    /// </remarks>
    [Trait("Category", "Unit")]
    public class DebugTableCatalogTests
    {
        /// <summary>
        /// 実際にマイグレーションを適用した DB に存在するテーブル名を取得する。
        /// </summary>
        private static IReadOnlyList<string> GetActualTableNames()
        {
            using var dbContext = new DbContext(":memory:");
            dbContext.InitializeDatabase();

            using var lease = dbContext.LeaseConnection();
            using var command = lease.Connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

            var names = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        [Fact]
        public void カタログは初期化直後のDBに存在する全テーブルと一致すること()
        {
            // Arrange
            var actual = GetActualTableNames();

            // 前提: 導出が空振りしていないこと（空集合なら一致テストは常に緑になる）
            actual.Should().NotBeEmpty("マイグレーション適用後の DB からテーブル名を導出できること");

            // Assert
            DebugTableCatalog.TableNames.Should().BeEquivalentTo(
                actual,
                "テーブルが増減したら DebugDataViewer の一覧も追随する必要がある（Issue #2012）");
        }

        [Fact]
        public void 統合履歴とマイグレーション管理のテーブルを閲覧できること()
        {
            // Issue #2012 で欠落が判明した 2 テーブル。
            // 上の一致テストが導出側の不具合で空振りしても、この 2 件は落ちる
            DebugTableCatalog.IsKnownTable("ledger_merge_history").Should().BeTrue();
            DebugTableCatalog.IsKnownTable("schema_migrations").Should().BeTrue();
        }

        [Theory]
        [InlineData("ledger; DROP TABLE staff")]
        [InlineData("sqlite_master")]
        [InlineData("Ledger")] // 大文字小文字を区別する（SQL へ渡す値なので緩めない）
        [InlineData("")]
        [InlineData(null)]
        public void 一覧に無いテーブル名は受け付けないこと(string tableName)
        {
            DebugTableCatalog.IsKnownTable(tableName).Should().BeFalse();
        }

        [Fact]
        public void 一覧に重複が無いこと()
        {
            DebugTableCatalog.TableNames.Should().OnlyHaveUniqueItems();
        }
    }
}
