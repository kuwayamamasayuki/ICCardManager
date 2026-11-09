using System;
using System.IO;
using System.Linq;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2435: バックアップの保存先・DB のパスを表示する画面（システム管理・接続診断）のスクリーンショットを、
    /// 撮影用の置き場所で動かしたアプリで撮る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// UI テストのアプリは開発機の実 DB のバックアップを押し出さないよう、バックアップを UI テスト専用のフォルダーへ作る（#2423）。
    /// システム管理画面の「保存先」は使っている保存先をそのまま表示するので、従来の撮り方（<see cref="ManualScreenshotTests"/>。Release ビルド）では
    /// マニュアルの画像に <c>C:\ProgramData\ICCardManager.UITests\backup</c> が写る。ここではアプリの置き場所ごと撮影用のフォルダーへ移し
    /// （<see cref="AppFixture.LaunchWithSeedInScreenshotDataRoot"/>）、本体に本番の置き場所の配下として表示させる。
    /// </para>
    /// <para>
    /// 置き場所の差し替えは DEBUG ビルドのアプリにしか無いので、本クラスは Debug パス（<c>Screenshot=Debug</c>）で走る。
    /// 撮る画面はダイアログだけで、Debug ビルドにしか無い仮想タッチの操作パネル（メイン画面の下部）は写らない。
    /// 撮った後に、画面に UI テスト専用のフォルダーのパスが出ていないこと（<see cref="ScreenshotAssertions.RequireNoUiTestPathShown"/>）と、
    /// 「保存先」が既定のパスであること（<see cref="ScreenshotAssertions.RequireBackupFolderShownAsDefault"/>）を確かめる。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    [Trait("Category", "Screenshot")]
    [Trait("Screenshot", "Debug")]
    public class BackupLocationScreenshotTests
    {
        public BackupLocationScreenshotTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        private const string SkipReason =
            "Issue #2435: 保存先を表示する画面の撮影は ICCARDMANAGER_SCREENSHOT=1 かつ Debug 起動" +
            "（ICCARDMANAGER_UITEST_CONFIGURATION=Debug）のときだけ実行する。tools/take-screenshots-uitest.ps1 から起動してください。";

        [SkippableFact]
        public void system_システム管理()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeedInScreenshotDataRoot(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var system = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);
            try
            {
                // 保存先はバックアップの状況の読み込みの後に表示される（それまでは「保存先: -」）
                var shown = WaitForBackupFolderText(system);
                var path = ScreenshotHelper.Capture(system.Window, "system.png");
                File.Exists(path).Should().BeTrue();

                ScreenshotAssertions.RequireBackupFolderShownAsDefault(shown, path);
                ScreenshotAssertions.RequireNoUiTestPathShown(system.Window, path);
            }
            finally
            {
                system.Close();
            }
        }

        /// <summary>
        /// リストア用のバックアップ一覧（1 件選択した状態）。
        /// </summary>
        /// <remarks>
        /// 手動バックアップの作成は OS 標準の <c>SaveFileDialog</c> を経るため自動化の対象外。一覧は起動時の自動バックアップが作った世代で埋まる。
        /// 撮影用の置き場所は起動のたびに空にするので、一覧と件数は撮影機の撮影の履歴に左右されない。
        /// </remarks>
        [SkippableFact]
        public void restore_list_リストア用バックアップ一覧()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeedInScreenshotDataRoot(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var system = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);
            try
            {
                var shown = WaitForBackupFolderText(system);
                var item = ManualScreenshotTests.WaitForRow(system, TestConstants.BackupFileList,
                    "バックアップ一覧に 1 件以上あること（起動時の自動バックアップが作られていること）");
                item.Patterns.SelectionItem.PatternOrDefault?.Select();

                var path = ScreenshotHelper.Capture(system.Window, "restore_list.png");
                File.Exists(path).Should().BeTrue();

                ScreenshotAssertions.RequireBackupFolderShownAsDefault(shown, path);
                ScreenshotAssertions.RequireNoUiTestPathShown(system.Window, path);
            }
            finally
            {
                system.Close();
            }
        }

        /// <summary>
        /// 接続診断。表示が完了したことの目印は診断項目の一覧の行（<c>Window_Loaded</c> の非同期処理が終わって初めて並ぶ）。
        /// </summary>
        [SkippableFact]
        public void connection_diagnostics_接続診断()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeedInScreenshotDataRoot(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var system = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);
            system.ClickButton(TestConstants.OpenConnectionDiagnosticsButton);

            var child = DialogLocator.WaitForNestedDialog(fixture, system.Window, TestConstants.ConnectionDiagnosticsDialogName);
            ManualScreenshotTests.WaitForRow(new DialogPageBase(child), TestConstants.ConnectionDiagnosticsItemList,
                $"{TestConstants.ConnectionDiagnosticsDialogName} の一覧に行が現れること");

            // 画像の下部の詳細欄に DB のパスを写す。初期選択は問題のある最初の項目（撮影機のリーダーの状態に左右される）なので、
            // データベース到達性の項目を選び直してから撮る
            var detail = SelectDatabaseReachability(child);
            var path = ScreenshotHelper.Capture(child, "connection_diagnostics.png");
            File.Exists(path).Should().BeTrue();

            ScreenshotAssertions.RequireDatabaseShownAsDefault(detail, path);
            ScreenshotAssertions.RequireNoUiTestPathShown(child, path);
        }

        /// <summary>
        /// 接続診断の一覧で「データベース到達性」の行を選び、詳細欄にその詳細が出るまで待って、詳細欄の文字を返す（読めなければ null）。
        /// </summary>
        private static string? SelectDatabaseReachability(FlaUI.Core.AutomationElements.Window dialog)
        {
            var list = new DialogPageBase(dialog).FindByNameWithRetry(TestConstants.ConnectionDiagnosticsItemList);
            list.Should().NotBeNull();
            var row = list!.FindAllDescendants().FirstOrDefault(e =>
                e.Patterns.SelectionItem.IsSupported
                && e.FindFirstDescendant(cf => cf.ByName(TestConstants.DatabaseReachabilityItemName)) != null);
            row.Should().NotBeNull($"接続診断の一覧に「{TestConstants.DatabaseReachabilityItemName}」の行があること");
            row!.Patterns.SelectionItem.Pattern.Select();

            return Retry.WhileNull(
                () =>
                {
                    var name = dialog.FindFirstDescendant(cf => cf.ByHelpText(TestConstants.ConnectionDiagnosticsDetailHelpText))?.Name;
                    return name != null && name.StartsWith("データベース（", StringComparison.Ordinal) ? name : null;
                },
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result
                ?? dialog.FindFirstDescendant(cf => cf.ByHelpText(TestConstants.ConnectionDiagnosticsDetailHelpText))?.Name;
        }

        /// <summary>
        /// 「保存先: 」の表示が読み込みの後の値（「保存先: -」以外）になるまで待ち、その文字を返す。
        /// </summary>
        private static string WaitForBackupFolderText(DialogPageBase system)
        {
            var shown = Retry.WhileNull(
                () =>
                {
                    var name = system.FindByNameStartsWith(TestConstants.BackupFolderTextPrefix)?.Name;
                    return name == null || name == TestConstants.BackupFolderTextPrefix + "-" ? null : name;
                },
                TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds)).Result;
            shown.Should().NotBeNull("システム管理画面にバックアップの保存先が表示されること");
            return shown!;
        }

        private static void SkipUnlessDebugCapture()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);
        }
    }
}
