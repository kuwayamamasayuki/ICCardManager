using System;
using System.Data.SQLite;
using System.Globalization;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2202: 終了ボタンで終了したとき、ウィンドウの位置・サイズが設定に保存されること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 終了時の保存は <c>MainWindow.Closing</c> で行う。#2202 で UI スレッドから呼んだ DB の完了を UI スレッドへ Post してから
    /// 伝えるようにした結果、<c>async void</c> のハンドラーで保存を <c>await</c> する形では、最初の <c>await</c> で戻った後に
    /// アプリの終了処理が先に進み、保存が走らないまま終わった（独立レビューで検出）。終了ボタンの経路
    /// （<c>Application.Shutdown</c>）は <c>Closing</c> を取り消せないので、保存はスレッドプールで始めて上限付きで待つ形にした。
    /// </para>
    /// <para>
    /// 位置の値そのもの（DPI の換算・見えない枠の幅で変わる）ではなく、終了の直前に目印の値へ書き換えておいた設定の行が、
    /// 終了後に数値で上書きされていることを表明する（DB には以前の起動で保存した位置が残っていることがあるため）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class WindowPositionSaveOnExitTests
    {
        /// <summary>
        /// ウィンドウ位置の設定キー（本体の <c>SettingsRepository.KeyWindowLeft</c> / <c>KeyWindowTop</c>。UI テストは本体を参照しない）。
        /// </summary>
        private const string WindowLeftKey = "window_left";

        private const string WindowTopKey = "window_top";

        /// <summary>終了の直前に書き込んでおく目印の値（数値として読めない値）。</summary>
        private const string Marker = "issue-2202-not-saved";

        public WindowPositionSaveOnExitTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void 終了ボタンで終了するとウィンドウの位置が設定に保存されること()
        {
            using var fixture = AppFixture.Launch();
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 以前の起動で保存した位置が残っていても判定できるよう、終了の直前に目印の値へ書き換える
            WriteSetting(WindowLeftKey, Marker);
            WriteSetting(WindowTopKey, Marker);
            ReadSetting(WindowLeftKey).Should().Be(Marker, "前提: 目印の値を書き込めていること（空振り防止）");

            page.ClickExitButton();
            var confirmation = MessageBoxOperations.WaitFor(fixture, fixture.MainWindow, TestConstants.ExitConfirmationTitle);
            MessageBoxOperations.Answer(confirmation, MessageBoxOperations.YesPrefix);

            var exited = Retry.WhileTrue(() => !fixture.App.HasExited, TimeSpan.FromSeconds(30)).Success;
            exited.Should().BeTrue("前提: 終了の確認に「はい」と答えたら、アプリケーションが終了すること");

            foreach (var key in new[] { WindowLeftKey, WindowTopKey })
            {
                var value = ReadSetting(key);
                value.Should().NotBe(Marker, $"終了時にウィンドウの位置（{key}）が保存されること（目印の値のままなら保存が走っていない）");
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    .Should().BeTrue($"保存された {key} が数値であること（実際の値: {value}）");
            }
        }

        private static void WriteSetting(string key, string value)
        {
            using var conn = new SQLiteConnection($"Data Source={AppFixture.DatabasePath};Version=3");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }

        private static string? ReadSetting(string key)
            => DatabaseProbe.Scalar("SELECT value FROM settings WHERE key = @key", ("@key", key)) as string;
    }
}
