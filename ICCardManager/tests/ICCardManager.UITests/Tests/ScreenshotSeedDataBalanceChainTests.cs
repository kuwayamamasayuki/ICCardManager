#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 撮影用のサンプルデータ（<see cref="ScreenshotSeedData"/>）の全経路で、カードごとの残高チェーン
    /// （前行の残額 ＋ 受入 − 払出 ＝ 当行の残額）が整合していることの検証（Issue #2425）。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 残高チェーンが切れたデータで撮ると、履歴画面の不整合警告が写り込むか、マニュアルの読者（庶務担当者）に
    /// 「払出があるのに残高が変わらない」帳簿を見せる。撮影の操作（仮想タッチの返却）が作る行は、撮影テストの側で
    /// <see cref="ScreenshotAssertions.RequireConsistentLedgerChain"/> が撮った後に確かめる。
    /// </para>
    /// <para>
    /// UITests は本体を参照しない（マイグレーションを実行できない）ため、シードが書く列だけを持つ最小のスキーマを
    /// インメモリの SQLite に作る。列や制約の食い違いは、実際のスキーマへ投入する撮影・UI テストの側で現れる。
    /// シードは <see cref="DateTime.Today"/> を読むので、日付の組み合わせは実行した日のものになる（日付の決め方は
    /// <see cref="ScreenshotSeedDataDateTests"/> が撮影日を走査して固定している）。
    /// </para>
    /// </remarks>
    public class ScreenshotSeedDataBalanceChainTests
    {
        /// <summary>ScreenshotSeedData の公開している投入の経路（撮影・UI テストが <c>AppFixture.LaunchWithSeed</c> へ渡すもの）。</summary>
        private static readonly IReadOnlyDictionary<string, Action<SQLiteConnection>> Seeds =
            new Dictionary<string, Action<SQLiteConnection>>(StringComparer.Ordinal)
            {
                [nameof(ScreenshotSeedData.Seed)] = ScreenshotSeedData.Seed,
                [nameof(ScreenshotSeedData.SeedForHistoryScreenshot)] = ScreenshotSeedData.SeedForHistoryScreenshot,
                [nameof(ScreenshotSeedData.SeedForVirtualTouch)] = ScreenshotSeedData.SeedForVirtualTouch,
                [nameof(ScreenshotSeedData.SeedForVirtualTouch) + "(バス停名入力あり)"] =
                    conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false),
                [nameof(ScreenshotSeedData.SeedForVirtualTouch) + "(同行者数入力あり)"] =
                    conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: true, skipCompanionCountInput: false),
                [nameof(ScreenshotSeedData.SeedForPhotographedStaffTouch)] = ScreenshotSeedData.SeedForPhotographedStaffTouch,
                [nameof(ScreenshotSeedData.SeedWithCompanionCountInputEnabled)] = ScreenshotSeedData.SeedWithCompanionCountInputEnabled,
                [nameof(ScreenshotSeedData.SeedWithoutWarnings)] = ScreenshotSeedData.SeedWithoutWarnings,
                [nameof(ScreenshotSeedData.SeedWithIncompleteBusStops)] = conn => _ = ScreenshotSeedData.SeedWithIncompleteBusStops(conn),
                [nameof(ScreenshotSeedData.SeedWithUnreturnedCard)] = ScreenshotSeedData.SeedWithUnreturnedCard,
                [nameof(ScreenshotSeedData.SeedWithTransferStationGroups)] = ScreenshotSeedData.SeedWithTransferStationGroups,
            };

        public static IEnumerable<object[]> SeedNames => Seeds.Keys.Select(name => new object[] { name });

        [Theory]
        [MemberData(nameof(SeedNames))]
        public void 各シードの台帳は_カードごとに残高チェーンが整合していること(string seedName)
        {
            using var conn = OpenSchema();
            Seeds[seedName](conn);

            LedgerBalanceChain.FindBreaks(conn).Should().BeEmpty(
                $"{seedName} で投入した台帳は、履歴画面の残高チェーンの検査（LedgerConsistencyChecker）に掛からないこと");
            CountLedgerRows(conn).Should().BeGreaterThan(0, "空の台帳では検査が何も確かめていない");
        }

        /// <summary>
        /// 撮影の返却で仮想タッチへ入れる「現在残高」の定数が、シードの残高と運賃から整合する値であること。
        /// </summary>
        /// <remarks>
        /// 返却は、仮想タッチの「現在残高」を利用の行の残額としてそのまま台帳へ書く（<c>LendingService.ResolveReturnBalanceAsync</c>）。
        /// シードの最新残高に、その値の行を運賃の払出で積んだときにチェーンが続くことを、検査器を通して確かめる。
        /// </remarks>
        [Fact]
        public void 仮想タッチの現在残高を定数どおりに入れた返却の行は_残高チェーンを保つこと()
        {
            using var conn = OpenSchema();
            ScreenshotSeedData.SeedForVirtualTouch(conn);

            InsertUsage(conn, ScreenshotSeedData.VirtualTouchCardIdm, ScreenshotSeedData.VirtualTouchFare, ScreenshotSeedData.VirtualTouchBalanceAfterOneFare);

            LedgerBalanceChain.FindBreaks(conn).Should().BeEmpty();
            ScreenshotSeedData.VirtualTouchBalanceAfterOneFare.Should().Be(19800, "新規購入 20,000 円から運賃 200 円を払った残額");
        }

        /// <summary>
        /// 対: 「現在残高」を既定値（DB の最新残高）のまま返却した形（マニュアルの画像が写していた形）は、検査が見つけること。
        /// </summary>
        [Fact]
        public void 仮想タッチの現在残高を既定値のまま返却した行は_残高チェーンの切れ目として報告されること()
        {
            using var conn = OpenSchema();
            ScreenshotSeedData.SeedForVirtualTouch(conn);

            InsertUsage(conn, ScreenshotSeedData.VirtualTouchCardIdm, ScreenshotSeedData.VirtualTouchFare, ScreenshotSeedData.VirtualTouchCardSeededBalance);

            var breaks = LedgerBalanceChain.FindBreaks(conn);
            breaks.Should().ContainSingle()
                .Which.Should().Be(
                    $"カード {ScreenshotSeedData.VirtualTouchCardIdm} の {DateTime.Today:yyyy-MM-dd}「鉄道（博多～天神）」（id {LastInsertedId(conn)}）: " +
                    "前行の残額 20,000 ＋ 受入 0 − 払出 200 ＝ 19,800 のはずが、残額は 20,000");
        }

        /// <summary>
        /// 並び: 同じ日の中では新規購入を id より優先して先頭に置くこと（本体の <c>LedgerOrderHelper</c> と同じ）。
        /// 月初（1 日）に撮ると新規購入と利用が同じ日になるため。id の順だけで並べると、ここで切れ目が報告される。
        /// </summary>
        [Fact]
        public void 同じ日の新規購入は_idが後でも先頭に並べて検査すること()
        {
            var day = new DateTime(2026, 11, 1);
            var rows = new[]
            {
                new LedgerBalanceChain.Row(1, "CARD", day, "鉄道（博多～天神）", 0, 200, 9800),
                new LedgerBalanceChain.Row(2, "CARD", day, "新規購入", 10000, 0, 10000),
            };

            LedgerBalanceChain.FindBreaks(rows).Should().BeEmpty();
        }

        /// <summary>
        /// 対: カードをまたいでチェーンをつながないこと。別のカードの行を前行として扱うと、整合している 2 枚が切れ目として報告される。
        /// </summary>
        [Fact]
        public void 残高チェーンはカードごとに検査し_別のカードの行を前行にしないこと()
        {
            var day = new DateTime(2026, 11, 2);
            var rows = new[]
            {
                new LedgerBalanceChain.Row(1, "A", day, "新規購入", 10000, 0, 10000),
                new LedgerBalanceChain.Row(2, "B", day, "新規購入", 3000, 0, 3000),
                new LedgerBalanceChain.Row(3, "A", day.AddDays(1), "鉄道（博多～天神）", 0, 260, 9740),
                new LedgerBalanceChain.Row(4, "B", day.AddDays(1), "役務費によりチャージ", 1000, 0, 4000),
            };

            LedgerBalanceChain.FindBreaks(rows).Should().BeEmpty();
        }

        private static void InsertUsage(SQLiteConnection conn, string cardIdm, int fare, int balance)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance, staff_name, note, is_lent_record) " +
                "VALUES (@card, @lender, @date, '鉄道（博多～天神）', 0, @fare, @balance, @staff, NULL, 0)";
            cmd.Parameters.AddWithValue("@card", cardIdm);
            cmd.Parameters.AddWithValue("@lender", AppFixture.SeededStaffIdm);
            cmd.Parameters.AddWithValue("@date", DateTime.Today.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@fare", fare);
            cmd.Parameters.AddWithValue("@balance", balance);
            cmd.Parameters.AddWithValue("@staff", ScreenshotSeedData.PrimaryStaffName);
            cmd.ExecuteNonQuery();
        }

        private static long LastInsertedId(SQLiteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static long CountLedgerRows(SQLiteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ledger";
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>シードが書く列だけを持つ最小のスキーマ（列名は本体のマイグレーションと同じ）。</summary>
        private static SQLiteConnection OpenSchema()
        {
            var conn = new SQLiteConnection("Data Source=:memory:;Version=3");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE staff (staff_idm TEXT PRIMARY KEY, name TEXT NOT NULL, number TEXT, note TEXT, is_deleted INTEGER DEFAULT 0);" +
                "CREATE TABLE ic_card (card_idm TEXT PRIMARY KEY, card_type TEXT NOT NULL, card_number TEXT NOT NULL, note TEXT, " +
                "  is_deleted INTEGER DEFAULT 0, is_lent INTEGER DEFAULT 0, last_lent_at TEXT, last_lent_staff TEXT);" +
                "CREATE TABLE ledger (id INTEGER PRIMARY KEY AUTOINCREMENT, card_idm TEXT NOT NULL, lender_idm TEXT, date TEXT NOT NULL, " +
                "  summary TEXT NOT NULL, income INTEGER DEFAULT 0, expense INTEGER DEFAULT 0, balance INTEGER NOT NULL, staff_name TEXT, " +
                "  note TEXT, lent_at TEXT, is_lent_record INTEGER DEFAULT 0, companion_count INTEGER NOT NULL DEFAULT 0);" +
                "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);";
            cmd.ExecuteNonQuery();
            return conn;
        }
    }
}
