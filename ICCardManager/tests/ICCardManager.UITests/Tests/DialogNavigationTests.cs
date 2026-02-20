using System;
using FluentAssertions;
using FlaUI.Core.Tools;
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

        [Fact]
        public void 終了ボタンでアプリケーションが終了する()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 終了ボタンをクリック
            page.ClickExitButton();

            // プロセスが終了するまで待機
            var exited = Retry.WhileTrue(
                () => !fixture.App.HasExited,
                TimeSpan.FromSeconds(10));

            fixture.App.HasExited.Should().BeTrue(
                "終了ボタンクリック後、アプリケーションプロセスが終了するべき");
        }
    }
}
