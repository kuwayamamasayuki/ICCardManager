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
    /// 職員管理画面の「職員証を差し替え」（Issue #2461）の UI 回帰テスト。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 認証（職員証認証ダイアログの仮想タッチ ＝ <see cref="AppFixture.SeededStaffIdm"/> の「博多 花子」）→
    /// 新しい職員証のタッチ（メイン画面の DEBUG パネル。モーダルの間もイベントはダイアログへ届く）→ 確認 → 保存までを通す。
    /// DEBUG パネルの「職員証」は登録済みの職員（博多 花子）の IDm を、「交通系ICカード」は
    /// <see cref="ScreenshotSeedData.Seed"/> では未登録の IDm（<see cref="ScreenshotSeedData.VirtualTouchCardIdm"/>）を読ませる。
    /// 後者を「再発行された新しい職員証」として使う。
    /// </para>
    /// <para>
    /// タッチ待ちの間はメイン画面のカード読み取りを抑制するので、未登録の IDm を読ませてもメイン画面の
    /// 「カード種別の選択」は開かない（開くとこのテストは確認の MessageBox を見つけられずに失敗する）。
    /// DEBUG パネルを使うため Debug 起動のときだけ実行する。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class StaffCardReplacementUiTests
    {
        private const string SkipReason =
            "Issue #2461: 新しい職員証のタッチは DEBUG パネル（DEBUG ビルド限定）で起こすため、Debug 起動のときだけ実行する。";

        private static bool IsDebug => string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal);

        [SkippableFact]
        public void 編集中に職員証を差し替えると_登録済みの職員証は拒否して待ち続け_新しい職員証で差し替わること()
        {
            Skip.If(!IsDebug, SkipReason);
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var lenderRowsBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ledger WHERE lender_idm = @idm",
                ("@idm", ScreenshotSeedData.SecondaryStaffIdm));
            lenderRowsBefore.Should().BeGreaterThan(0L, "前提: 差し替える職員に付け替える台帳の行があること（無いと引き継ぎを確かめられない）");
            DatabaseProbe.Count("SELECT COUNT(*) FROM staff WHERE staff_idm = @idm",
                ("@idm", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(0L, "前提: 新しい職員証の IDm は未登録であること");

            var (page, dialog) = StartReplacementAndWaitForTouch(fixture);

            // 登録済みの職員（博多 花子）の職員証をタッチ → 理由を案内して待ち続ける
            InvokeDebugPanelButton(page, TestConstants.DebugPanelStaffButton);
            var rejection = Retry.WhileNull(
                () => dialog.FindByNameStartsWith("タッチした職員証は、職員「" + ScreenshotSeedData.PrimaryStaffName),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            rejection.Should().NotBeNull("登録済みの職員証は、誰の職員証かを名指しして拒否すること");
            dialog.FindByName(TestConstants.StaffCancelReplaceCardButton).Should().NotBeNull("拒否の後もタッチを待ち続けること");

            // 未登録の IDm（再発行された新しい職員証）をタッチ → 確認 → はい
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            var confirmation = MessageBoxOperations.WaitFor(fixture, dialog.Window, TestConstants.StaffCardReplacementConfirmTitle);
            MessageBoxOperations.Answer(confirmation, MessageBoxOperations.YesPrefix);

            var replaced = Retry.WhileFalse(
                () => DatabaseProbe.Count("SELECT COUNT(*) FROM staff WHERE staff_idm = @idm AND name = @name",
                    ("@idm", ScreenshotSeedData.VirtualTouchCardIdm), ("@name", ScreenshotSeedData.SecondaryStaffName)) == 1L,
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            replaced.Should().BeTrue("新しい職員証の IDm で同じ職員（天神 太郎）が登録されていること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM staff WHERE staff_idm = @idm",
                ("@idm", ScreenshotSeedData.SecondaryStaffIdm)).Should().Be(0L, "古い職員証の登録は残らないこと");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ledger WHERE lender_idm = @idm",
                ("@idm", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(lenderRowsBefore, "過去の利用履歴が新しい職員証へ引き継がれること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card WHERE card_idm = @idm",
                ("@idm", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(0L,
                "タッチ待ちの間の読み取りがメイン画面で交通系ICカードとして登録されていないこと");

        }

        [SkippableFact]
        public void 差し替えをやめると何も変わらないこと()
        {
            Skip.If(!IsDebug, SkipReason);
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);

            var (_, dialog) = StartReplacementAndWaitForTouch(fixture);

            dialog.ClickButton(TestConstants.StaffCancelReplaceCardButton);

            var message = Retry.WhileNull(
                () => dialog.FindByNameStartsWith("職員証の差し替えをやめました"),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            message.Should().NotBeNull("やめたことをステータス欄で知らせること");
            dialog.FindByName(TestConstants.StaffReplaceCardButton).Should().NotBeNull("もう一度差し替えを始められること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM staff WHERE staff_idm = @idm",
                ("@idm", ScreenshotSeedData.SecondaryStaffIdm)).Should().Be(1L, "職員証は変わらないこと");
        }

        /// <summary>
        /// 職員管理画面で天神 太郎を編集し、「職員証を差し替え」→ 認証（仮想タッチ）を通してタッチ待ちにする。
        /// </summary>
        private static (MainWindowPage Page, DialogPageBase Dialog) StartReplacementAndWaitForTouch(AppFixture fixture)
        {
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenStaffManageButton, TestConstants.StaffManageDialogName);

            var grid = dialog.FindByNameWithRetry(TestConstants.StaffList)?.AsGrid();
            grid.Should().NotBeNull($"職員管理ダイアログに \"{TestConstants.StaffList}\" があること");
            var row = Retry.WhileNull(
                () => grid!.Rows.FirstOrDefault(r => r.Cells.Any(
                    c => string.Equals(c.Name, ScreenshotSeedData.SecondaryStaffName, StringComparison.Ordinal))),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            row.Should().NotBeNull($"職員一覧に \"{ScreenshotSeedData.SecondaryStaffName}\" の行があること");
            row!.Select();

            // 「編集」は選択行で有効になるので、有効になるまで待ってから押す（Issue #2192）
            dialog.WaitUntilEnabled(TestConstants.StaffEditButton).Should().BeTrue("行を選んだら「編集」ボタンが有効になること");
            dialog.ClickButton(TestConstants.StaffEditButton);
            dialog.WaitUntilEnabled(TestConstants.StaffReplaceCardButton).Should().BeTrue(
                "編集中は「職員証を差し替え」ボタンが押せること");
            dialog.ClickButton(TestConstants.StaffReplaceCardButton);

            PassStaffAuthentication(fixture, dialog.Window);

            var cancel = dialog.FindByNameWithRetry(TestConstants.StaffCancelReplaceCardButton);
            cancel.Should().NotBeNull("認証の後は新しい職員証のタッチを待ち、「差し替えをやめる」を出すこと");
            dialog.FindByName(TestConstants.StaffReplaceCardButton).Should().BeNull(
                "待っている間は「職員証を差し替え」ボタンを出さないこと（Collapsed の要素は UIA の木に現れない）");
            return (page, dialog);
        }
    }
}
