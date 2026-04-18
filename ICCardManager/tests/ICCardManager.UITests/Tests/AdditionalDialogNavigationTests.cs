using System;
using FluentAssertions;
using FlaUI.Core.Tools;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #1263: 既存の <see cref="DialogNavigationTests"/> で未カバーのダイアログ
    /// および連続操作のテストを拡充する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 既存の <see cref="DialogNavigationTests"/> は「設定」「職員管理」「終了」のみを検証。
    /// 本クラスでは以下の残りのツールバーダイアログを補完し、さらに連続オープン等の
    /// リソースリーク観点も追加する。
    /// </para>
    /// <para>
    /// ICカードリーダー必須のフロー（貸出/返却/バス停名入力）は自動化不能のため、
    /// PR 本文で手動テスト手順として提示する。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class AdditionalDialogNavigationTests
    {
        [Fact]
        public void 帳票ダイアログが開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenReportButton,
                TestConstants.ReportDialogName);

            dialog.Name.Should().Be(TestConstants.ReportDialogName);

            dialog.Close();

            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "帳票ダイアログが閉じられたはず");
        }

        [Fact]
        public void 交通系ICカード管理ダイアログが開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenCardManageButton,
                TestConstants.CardManageDialogName);

            dialog.Name.Should().Be(TestConstants.CardManageDialogName);

            dialog.Close();

            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "交通系ICカード管理ダイアログが閉じられたはず");
        }

        [Fact]
        public void データエクスポートインポートダイアログが開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenDataExportImportButton,
                TestConstants.DataExportImportDialogName);

            dialog.Name.Should().Be(TestConstants.DataExportImportDialogName);

            dialog.Close();

            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "データエクスポート/インポートダイアログが閉じられたはず");
        }

        [Fact]
        public void システム管理ダイアログが開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton,
                TestConstants.SystemManageDialogName);

            dialog.Name.Should().Be(TestConstants.SystemManageDialogName);

            dialog.Close();

            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "システム管理ダイアログが閉じられたはず");
        }

        /// <summary>
        /// Issue #1263: 複数ダイアログの連続オープン・クローズでリソースリークや
        /// UIA ツリーの不整合が発生しないこと（長時間運用の安定性）。
        /// </summary>
        [Fact]
        public void 複数ダイアログを連続で開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 設定 → 閉じる
            var settings = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSettingsButton,
                TestConstants.SettingsDialogName);
            settings.Close();
            Retry.WhileTrue(() => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            // 職員管理 → 閉じる
            var staff = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenStaffManageButton,
                TestConstants.StaffManageDialogName);
            staff.Close();
            Retry.WhileTrue(() => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            // カード管理 → 閉じる
            var cards = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenCardManageButton,
                TestConstants.CardManageDialogName);
            cards.Close();
            Retry.WhileTrue(() => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(5));

            // 連続操作後もメインウィンドウが応答可能であること
            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "連続操作後にモーダル残存がないこと");
            page.SettingsButton.Should().NotBeNull(
                "連続操作後もツールバーボタンがUIAツリーに存在する");
        }

        /// <summary>
        /// Issue #1263: 同一ダイアログを連続で開閉しても問題なく動作する。
        /// 2回目以降の開閉でハンドルリークや例外が発生しないこと。
        /// </summary>
        [Fact]
        public void 同一ダイアログを連続で開閉できる()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            for (int i = 1; i <= 3; i++)
            {
                var dialog = page.ClickToolbarButtonAndWaitForDialog(
                    TestConstants.OpenSettingsButton,
                    TestConstants.SettingsDialogName);
                dialog.Name.Should().Be(TestConstants.SettingsDialogName,
                    $"{i}回目の開閉でダイアログ名が正しいこと");
                dialog.Close();
                Retry.WhileTrue(() => fixture.MainWindow.ModalWindows.Length > 0,
                    TimeSpan.FromSeconds(5));
            }

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "3回開閉後もモーダル残存がないこと");
        }
    }
}
