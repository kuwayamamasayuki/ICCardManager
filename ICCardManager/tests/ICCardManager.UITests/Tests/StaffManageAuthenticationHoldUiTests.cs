using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 職員管理画面の職員証の認証の保持（Issue #2494）の UI 回帰テスト。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 最初の「保存」でだけ職員証の認証（職員証認証ダイアログの仮想タッチ ＝ <see cref="AppFixture.SeededStaffIdm"/> の
    /// 「博多 花子」）を求め、一覧の上に「認証中: 博多 花子」を表示し、2 回目の保存は認証ダイアログを出さずに書き込むこと、
    /// 「認証を解除」で表示が戻り次の保存で再び認証を求めること、画面を閉じて開き直すと保持していないことを通しで確かめる。
    /// </para>
    /// <para>
    /// 2 回目の保存が認証を求めないことは、認証ダイアログ（モーダル）を開けば書き込みが進まないことから、
    /// 書き込みが DB に現れることで表明する。認証の仮想タッチは DEBUG ビルド限定のため、Debug 起動のときだけ実行する。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class StaffManageAuthenticationHoldUiTests
    {
        private const string SkipReason =
            "Issue #2494: 職員証の認証は認証ダイアログの仮想タッチ（DEBUG ビルド限定）で通すため、Debug 起動のときだけ実行する。";

        private static bool IsDebug => string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal);

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        [SkippableFact]
        public void 最初の保存でだけ認証し_認証中を表示して2回目は認証せず_解除と閉じると保持しないこと()
        {
            Skip.If(!IsDebug, SkipReason);
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = OpenStaffManage(page);

            ReadHeldText(dialog).Should().StartWith(TestConstants.StaffNoHeldAuthenticationPrefix, "開いた直後は認証を保持していない");
            dialog.FindByName(TestConstants.StaffReleaseHeldAuthenticationButton).Should().BeNull(
                "保持していない間は「認証を解除」を出さない（Collapsed の要素は UIA の木に現れない）");

            // 1 回目: 認証を求める
            EditNoteAndSave(dialog, "保持の確認1");
            PassStaffAuthentication(fixture, dialog.Window);
            WaitForNote("保持の確認1");

            var heldText = Retry.WhileFalse(
                () => ReadHeldText(dialog).StartsWith(TestConstants.StaffHeldAuthenticationPrefix + ScreenshotSeedData.PrimaryStaffName, StringComparison.Ordinal),
                Wait).Success;
            heldText.Should().BeTrue($"認証した職員を「認証中: 氏名」で表示すること（実際: {ReadHeldText(dialog)}）");
            dialog.FindByNameWithRetry(TestConstants.StaffReleaseHeldAuthenticationButton).Should().NotBeNull(
                "保持している間は「認証を解除」を出す");

            // 2 回目: 認証ダイアログを出さずに書き込む
            EditNoteAndSave(dialog, "保持の確認2");
            WaitForNote("保持の確認2");
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM operation_log WHERE target_table = 'staff' AND target_id = @idm AND action = 'UPDATE' AND operator_idm = @op",
                ("@idm", ScreenshotSeedData.SecondaryStaffIdm), ("@op", AppFixture.SeededStaffIdm))
                .Should().Be(2L, "2 回の変更とも、認証した職員（博多 花子）の操作として記録する");

            // 「認証を解除」→ 表示が戻り、次の保存で再び認証を求める
            dialog.ClickButton(TestConstants.StaffReleaseHeldAuthenticationButton);
            Retry.WhileFalse(
                () => ReadHeldText(dialog).StartsWith(TestConstants.StaffNoHeldAuthenticationPrefix, StringComparison.Ordinal),
                Wait).Success.Should().BeTrue("解除したら「職員証の認証: なし」の表示に戻ること");
            EditNoteAndSave(dialog, "保持の確認3");
            PassStaffAuthentication(fixture, dialog.Window);
            WaitForNote("保持の確認3");

            // 閉じて開き直すと保持していない
            dialog.Close();
            var reopened = OpenStaffManage(page);
            ReadHeldText(reopened).Should().StartWith(TestConstants.StaffNoHeldAuthenticationPrefix,
                "画面を閉じたら保持を破棄する");
            reopened.Close();
        }

        private static DialogPageBase OpenStaffManage(MainWindowPage page)
            => page.ClickToolbarButtonAndWaitForDialog(TestConstants.OpenStaffManageButton, TestConstants.StaffManageDialogName);

        private static string ReadHeldText(DialogPageBase dialog)
        {
            var element = Retry.WhileNull(
                () => dialog.Window.FindFirstDescendant(cf => cf.ByAutomationId(TestConstants.StaffHeldAuthenticationTextId)),
                Wait).Result;
            element.Should().NotBeNull("職員管理ダイアログに職員証の認証の保持の表示が常にあること");
            return element!.Name ?? string.Empty;
        }

        /// <summary>天神 太郎を選んで編集し、備考を書き換えて「保存」を押す。</summary>
        private static void EditNoteAndSave(DialogPageBase dialog, string note)
        {
            var grid = dialog.FindByNameWithRetry(TestConstants.StaffList)?.AsGrid();
            grid.Should().NotBeNull($"職員管理ダイアログに \"{TestConstants.StaffList}\" があること");
            var row = Retry.WhileNull(
                () => grid!.Rows.FirstOrDefault(r => r.Cells.Any(
                    c => string.Equals(c.Name, ScreenshotSeedData.SecondaryStaffName, StringComparison.Ordinal))),
                Wait).Result;
            row.Should().NotBeNull($"職員一覧に \"{ScreenshotSeedData.SecondaryStaffName}\" の行があること");
            row!.Select();

            // 「編集」は選択行で有効になるので、有効になるまで待ってから押す（Issue #2192）
            dialog.WaitUntilEnabled(TestConstants.StaffEditButton).Should().BeTrue("行を選んだら「編集」ボタンが有効になること");
            dialog.ClickButton(TestConstants.StaffEditButton);
            // 一覧の列見出しも「備考」なので、入力欄（Edit）に絞る。値は座標やフォーカスを介さない Value パターンで入れる
            var noteBox = Retry.WhileNull(
                () => dialog.Window.FindFirstDescendant(cf => cf.ByName(TestConstants.StaffNoteTextBox)
                    .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))),
                Wait).Result;
            noteBox.Should().NotBeNull("編集中は備考の入力欄があること");
            noteBox!.Patterns.Value.Pattern.SetValue(note);
            dialog.WaitUntilEnabled(TestConstants.StaffManageSaveButton).Should().BeTrue("編集中は「保存」が押せること");
            dialog.ClickButton(TestConstants.StaffManageSaveButton);
        }

        private static void WaitForNote(string note)
        {
            Retry.WhileFalse(
                () => DatabaseProbe.Count("SELECT COUNT(*) FROM staff WHERE staff_idm = @idm AND note = @note",
                    ("@idm", ScreenshotSeedData.SecondaryStaffIdm), ("@note", note)) == 1L,
                Wait).Success.Should().BeTrue($"天神 太郎の備考が「{note}」に書き換わること");
        }
    }
}
