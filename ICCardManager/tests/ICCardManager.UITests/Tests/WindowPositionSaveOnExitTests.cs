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

        /// <summary>
        /// Issue #2305: 起動中に他の PC が設定を変えても、終了時の保存がその設定を巻き戻さないこと。
        /// </summary>
        /// <remarks>
        /// 以前の終了時の保存は、起動時に読んでキャッシュした設定（TTL 5 分・共有モード 3 分）のウィンドウ値だけを差し替えて
        /// 一括保存へ丸ごと渡していた。起動後に DB の部署種別・残額警告を書き換えて（他の PC の変更に相当）終了し、
        /// 書き換えた値が終了後も残っていることを表明する。DB は <see cref="AppFixture"/> の Dispose で復元される。
        /// </remarks>
        [Fact]
        public void 起動中に他のPCが変えた設定を終了時の保存が巻き戻さないこと()
        {
            using var fixture = AppFixture.Launch();
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 起動時に読んだ値と異なる値へ書き換える（他の PC が設定画面で変えたことに相当）
            var changedDepartment = ReadSetting(DepartmentTypeKey) == EnterpriseAccount ? MayorOffice : EnterpriseAccount;
            var changedWarningBalance = ReadSetting(WarningBalanceKey) == "4321" ? "4322" : "4321";
            WriteSetting(DepartmentTypeKey, changedDepartment);
            WriteSetting(WarningBalanceKey, changedWarningBalance);
            WriteSetting(WindowLeftKey, Marker);
            ReadSetting(DepartmentTypeKey).Should().Be(changedDepartment, "前提: 他の PC の変更に相当する値を書き込めていること");

            page.ClickExitButton();
            var confirmation = MessageBoxOperations.WaitFor(fixture, fixture.MainWindow, TestConstants.ExitConfirmationTitle);
            MessageBoxOperations.Answer(confirmation, MessageBoxOperations.YesPrefix);

            var exited = Retry.WhileTrue(() => !fixture.App.HasExited, TimeSpan.FromSeconds(30)).Success;
            exited.Should().BeTrue("前提: 終了の確認に「はい」と答えたら、アプリケーションが終了すること");

            ReadSetting(WindowLeftKey).Should().NotBe(Marker, "前提: 終了時の保存が走っていること（走らなければ巻き戻りも起きず空振りする）");
            ReadSetting(DepartmentTypeKey).Should().Be(
                changedDepartment,
                "終了時の保存が部署種別を起動時の値へ戻すと、以後のチャージの摘要が誤った方で台帳へ入る（Issue #2305）");
            ReadSetting(WarningBalanceKey).Should().Be(changedWarningBalance, "残額警告のしきい値も巻き戻さないこと");
        }

        /// <summary>部署種別の設定キー（本体の <c>SettingsRepository.KeyDepartmentType</c>）。</summary>
        private const string DepartmentTypeKey = "department_type";

        /// <summary>残額警告の設定キー（本体の <c>SettingsRepository.KeyWarningBalance</c>）。</summary>
        private const string WarningBalanceKey = "warning_balance";

        private const string MayorOffice = "mayor_office";

        private const string EnterpriseAccount = "enterprise_account";

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
