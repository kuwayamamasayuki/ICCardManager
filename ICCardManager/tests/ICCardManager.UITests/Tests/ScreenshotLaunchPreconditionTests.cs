#nullable enable
using System;
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2485: マニュアル用の撮影テストが、起動の前提（起動構成・撮影モード）を満たさないまま撮らないことの検証。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 撮影テストは実行プロセスの環境変数（<c>ICCARDMANAGER_UITEST_CONFIGURATION</c>・<c>ICCARDMANAGER_SCREENSHOT_MODE</c>）に依存していた。
    /// 届かないと Debug 版が撮影モードなしで起動し、起動時のテストデータ（本体の <c>DebugDataService</c>）が撮影用のシードより先に DB に入る。
    /// シードは同じ IDm の職員・カードを <c>INSERT OR IGNORE</c> で入れるため黙って無視され、テストは
    /// 「カード一覧に行が現れない」「残高チェーンが切れている」といった原因と無関係な症状で失敗していた。
    /// </para>
    /// <para>
    /// ここでは①Release パスの撮影が起動構成を確かめる判定（<see cref="AppFixture.ThrowUnlessReleaseCapture"/>）と、
    /// ②シードが既存の行とぶつかったときに原因を名指しして止まること（<see cref="ScreenshotSeedData"/>）を固定する。
    /// 撮影テストがこれらを実際に通っていること（配線）は、単体テストの <c>ScreenshotLaunchPreconditionConventionTests</c> がソースを読んで固定する。
    /// </para>
    /// </remarks>
    public class ScreenshotLaunchPreconditionTests
    {
        [Theory]
        [InlineData("Release")]
        [InlineData("release")]
        public void Release起動なら_Releaseパスの撮影はそのまま進むこと(string value)
        {
            var act = () => AppFixture.ThrowUnlessReleaseCapture(value);

            act.Should().NotThrow();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void 起動構成の環境変数が届いていなければ_届いていないことと渡し方を示して止まること(string? value)
        {
            var act = () => AppFixture.ThrowUnlessReleaseCapture(value);

            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain("ICCARDMANAGER_UITEST_CONFIGURATION", "どの環境変数の問題かを名指しすること");
            message.Should().Contain("届いていません", "未設定（届いていない）のか、別の値なのかを区別して示すこと");
            message.Should().Contain("ICCARDMANAGER_UITEST_CONFIGURATION=Release", "どう渡せばよいかを示すこと");
            message.Should().Contain("WSLENV", "WSL から dotnet.exe を直接呼ぶと WSLENV を設定しない限り環境変数が届かないこと");
            message.Should().Contain("tools/take-screenshots-uitest.ps1", "通常の撮影の入口を示すこと");
            message.Should().Contain("DebugDataService", "このまま撮ると何が起きるか（テストデータが先に入る）を示すこと");
        }

        [Theory]
        [InlineData("Debug")]
        [InlineData("Relase")]
        public void 起動構成がRelease以外なら_受け取った値を示して止まること(string value)
        {
            var act = () => AppFixture.ThrowUnlessReleaseCapture(value);

            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain($"「{value}」", "受け取った値を示すこと（綴りの誤りに気付けるように）");
            message.Should().NotContain("届いていません");
            message.Should().Contain("ICCARDMANAGER_UITEST_CONFIGURATION=Release");
        }

        /// <summary>
        /// Debug 版のアプリが撮影モードなしで起動すると、起動時のテストデータ（撮影用の職員と同じ IDm の職員を含む）がシードより先に入る。
        /// </summary>
        [Fact]
        public void 投入の前に職員が入っていれば_シードは原因を名指しして止まること()
        {
            using var conn = OpenSchema();
            Execute(conn, $"INSERT INTO staff (staff_idm, name, number, note, is_deleted) VALUES ('{AppFixture.SeededStaffIdm}', '山田太郎', '001', NULL, 0)");

            Action act = () => ScreenshotSeedData.Seed(conn);

            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain("既存のデータがあります");
            message.Should().Contain("職員 1 行", "どの表に何行あったかを示すこと");
            message.Should().Contain("DebugDataService", "先に入った行の出どころ（起動時のテストデータ）を名指しすること");
            message.Should().Contain("撮影モード", "撮影モードで起動しているかを確かめるよう案内すること");
            Count(conn, "ic_card").Should().Be(0, "止めたときは何も投入しないこと");
        }

        /// <summary>
        /// IDm がシードと食い違うテストデータでも止めること。衝突の検知（影響行数 0）だけでは、IDm が一致するときしか働かない（レビュー 1 回目で強化）。
        /// </summary>
        [Theory]
        [InlineData("INSERT INTO staff (staff_idm, name, number, note, is_deleted) VALUES ('FFFF000000000099', '佐藤一郎', '003', NULL, 0)", "職員 1 行")]
        [InlineData("INSERT INTO ic_card (card_idm, card_type, card_number, note, is_deleted, is_lent) VALUES ('06FE112233445568', 'SUGOCA', 'S-001', NULL, 0, 0)", "交通系ICカード 1 行")]
        [InlineData("INSERT INTO ledger (card_idm, date, summary, balance) VALUES ('06FE112233445568', '2026-11-01 00:00:00', '新規購入', 1000)", "台帳 1 行")]
        public void シードとIDmの重ならないデータが入っていても_シードは投入の前に止まること(string sql, string expectedCount)
        {
            using var conn = OpenSchema();
            Execute(conn, sql);

            Action act = () => ScreenshotSeedData.SeedForVirtualTouch(conn);

            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain(expectedCount);
            message.Should().Contain("DebugDataService");
        }

        /// <summary>
        /// 投入の途中で <c>INSERT OR IGNORE</c> が無視されたとき（影響行数 0）は、シードが同じ IDm を重複して投入したことを名指しして止めること。
        /// 投入の前に DB が空であることは確かめているので、起動時のテストデータとの衝突ではない。環境変数の調査へ誘導しない（レビュー 2 回目で検出）。
        /// </summary>
        [Fact]
        public void 影響行数が0なら_シードの中での重複投入として名指しして止まること()
        {
            Action act = () => ScreenshotSeedData.RequireInserted(0, $"交通系ICカード（IDm {ScreenshotSeedData.VirtualTouchCardIdm}「はやかけん 004」）");

            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain(ScreenshotSeedData.VirtualTouchCardIdm, "どの行が重複したかを示すこと");
            message.Should().Contain("シードの中で既に投入しています");
            message.Should().Contain("派生シード", "どこを確かめればよいかを示すこと");
            message.Should().NotContain("撮影モード", "起動の前提の問題ではないので、環境変数の調査へ誘導しないこと");
            message.Should().NotContain("WSLENV");
        }

        /// <summary>対: 影響行数が 1（投入できた）なら止めないこと。</summary>
        [Fact]
        public void 影響行数が1なら_止めないこと()
        {
            Action act = () => ScreenshotSeedData.RequireInserted(1, "職員（IDm FFFF000000000001「博多 花子」）");

            act.Should().NotThrow();
        }

        /// <summary>
        /// 対: 空の DB（撮影モードで起動した形）にはシードがそのまま入ること。止める判定が常に例外を投げる実装を緑にしない。
        /// </summary>
        [Fact]
        public void 空のDBには_仮想タッチ用のシードがそのまま入ること()
        {
            using var conn = OpenSchema();

            Action act = () => ScreenshotSeedData.SeedForVirtualTouch(conn);

            act.Should().NotThrow();
            Count(conn, "staff").Should().Be(2);
            Count(conn, "ic_card").Should().Be(4);
        }

        /// <summary>シードが書く列だけを持つ最小のスキーマ（<see cref="ScreenshotSeedDataBalanceChainTests"/> と同じ）。</summary>
        private static SQLiteConnection OpenSchema()
        {
            var conn = new SQLiteConnection("Data Source=:memory:;Version=3");
            conn.Open();
            Execute(conn,
                "CREATE TABLE staff (staff_idm TEXT PRIMARY KEY, name TEXT NOT NULL, number TEXT, note TEXT, is_deleted INTEGER DEFAULT 0);" +
                "CREATE TABLE ic_card (card_idm TEXT PRIMARY KEY, card_type TEXT NOT NULL, card_number TEXT NOT NULL, note TEXT, " +
                "  is_deleted INTEGER DEFAULT 0, is_lent INTEGER DEFAULT 0, last_lent_at TEXT, last_lent_staff TEXT);" +
                "CREATE TABLE ledger (id INTEGER PRIMARY KEY AUTOINCREMENT, card_idm TEXT NOT NULL, lender_idm TEXT, date TEXT NOT NULL, " +
                "  summary TEXT NOT NULL, income INTEGER DEFAULT 0, expense INTEGER DEFAULT 0, balance INTEGER NOT NULL, staff_name TEXT, " +
                "  note TEXT, lent_at TEXT, is_lent_record INTEGER DEFAULT 0, companion_count INTEGER NOT NULL DEFAULT 0, initial_record_kind INTEGER DEFAULT NULL);" +
                "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);");
            return conn;
        }

        private static void Execute(SQLiteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static long Count(SQLiteConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
