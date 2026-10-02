using System;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// ツールバーボタンからダイアログを開閉するナビゲーションテスト。
    /// </summary>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class DialogNavigationTests
    {
        [Fact]
        public void 設定ボタンで設定ダイアログが開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // ダイアログを開く
            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSettingsButton,
                TestConstants.SettingsDialogName);

            dialog.Name.Should().Be(TestConstants.SettingsDialogName);

            // ダイアログを閉じる
            dialog.Close();

            // ダイアログが閉じたことを確認（モーダルウィンドウが無くなる）
            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "設定ダイアログが閉じられたはず");
        }

        [Fact]
        public void 職員管理ボタンで職員管理ダイアログが開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // ダイアログを開く
            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenStaffManageButton,
                TestConstants.StaffManageDialogName);

            dialog.Name.Should().Be(TestConstants.StaffManageDialogName);

            // ダイアログを閉じる
            dialog.Close();

            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "職員管理ダイアログが閉じられたはず");
        }

        /// <summary>
        /// 終了ボタンは確認（Issue #2143）を経て終了する。確認に「はい」で答えるとプロセスが終了すること。
        /// </summary>
        /// <remarks>
        /// #2143 で確認が入った後も、このテストは「押せばすぐ終了する」前提のまま残り、確認の MessageBox が開いたまま
        /// 「プロセスが終了しない」で失敗していた（UI テストは CI で走らないため気付かれなかった。Issue #2190 で是正）。
        /// </remarks>
        [Fact]
        public void 終了ボタンで確認にはいと答えるとアプリケーションが終了する()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.ClickExitButton();
            var confirmation = MessageBoxOperations.WaitFor(fixture, fixture.MainWindow, TestConstants.ExitConfirmationTitle);
            MessageBoxOperations.Answer(confirmation, MessageBoxOperations.YesPrefix);

            var exited = Retry.WhileTrue(
                () => !fixture.App.HasExited,
                TimeSpan.FromSeconds(10)).Success;
            exited.Should().BeTrue("終了の確認に「はい」と答えたら、アプリケーションプロセスが終了すること");
        }

        /// <summary>
        /// 対: 確認に「いいえ」で答えると終了せず、メイン画面を引き続き操作できること（Issue #2190。UT-132 の手動確認を置き換える）。
        /// </summary>
        /// <remarks>
        /// 「はい」の側は、確認が出ない実装（#2143 の是正前）なら確認の待ちで赤になるが、確認を出したうえで答えを無視して
        /// 終了する実装は検出できない。共有 PC で誤って押した「終了」を取り消せることが #2143 の目的なので、残る側を対で表明する。
        /// </remarks>
        [Fact]
        public void 終了ボタンで確認にいいえと答えるとアプリケーションは終了しない()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.ClickExitButton();
            AnswerNoAndAssertStillRunning(fixture);
        }

        /// <summary>
        /// Alt+F4 でも同じ確認が出て、「いいえ」で残ること（Issue #2190。UT-132 の手動確認を置き換える）。
        /// </summary>
        /// <remarks>
        /// ✕・Alt+F4 は <c>MainWindow</c> の <c>WM_SYSCOMMAND(SC_CLOSE)</c> フックが終了ボタンと同じ確認を通す（#2143）。
        /// UIA の <c>WindowPattern.Close</c> は <c>WM_CLOSE</c> を送り、このフックを通らないので使わない。
        /// キー入力は前面のウィンドウへ届くため、送る前に前面化を確かめる。
        /// </remarks>
        [Fact]
        public void AltF4でも終了の確認が出ていいえと答えるとアプリケーションは終了しない()
        {
            using var fixture = AppFixture.Launch();
            _ = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            ScreenshotHelper.RequireForeground(fixture.MainWindow);
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(
                FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.F4);
            AnswerNoAndAssertStillRunning(fixture);
        }

        /// <summary>終了の確認に「いいえ」で答え、プロセスが残り、メイン画面が再び操作できることを表明する。</summary>
        private static void AnswerNoAndAssertStillRunning(AppFixture fixture)
        {
            var confirmation = MessageBoxOperations.WaitFor(fixture, fixture.MainWindow, TestConstants.ExitConfirmationTitle);
            var mainHandle = NativeWindows.HandleOf(fixture.MainWindow);
            MessageBoxOperations.Answer(confirmation, MessageBoxOperations.NoPrefix);

            // 「終了しないこと」は時間内に起きないことの表明。答えを無視して終了する実装なら 1 秒もかからず終わる
            // （「はい」の側の実測）ので、5 秒待って終了していなければ残ったと判断する
            var exited = Retry.WhileFalse(
                () => fixture.App.HasExited,
                TimeSpan.FromSeconds(5)).Success;
            exited.Should().BeFalse("終了の確認に「いいえ」と答えたら、アプリケーションは終了しないこと");
            NativeWindows.IsEnabled(mainHandle).Should().BeTrue("確認を閉じた後、メイン画面は再び操作できること");
        }
    }
}
