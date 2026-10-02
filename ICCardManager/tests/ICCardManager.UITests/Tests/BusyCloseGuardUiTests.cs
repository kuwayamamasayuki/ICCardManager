using System;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 処理中のダイアログを利用者の操作で閉じられないことの回帰テスト
    /// （Issue #2196。07_テスト設計書 UT-130 の手動確認「✕・Alt+F4・Esc が処理中に効かないこと」を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判断（<c>BusyCloseGuard.ShouldBlockUserClose</c> 等）は単体テストで、結線は静的検査で固定されているが、
    /// 実際に処理中のダイアログへ Esc・Alt+F4 を送って閉じないことは実機でしか確かめられなかった。
    /// </para>
    /// <para>
    /// 処理中の状態は、接続診断を DB の排他ロック（<see cref="DatabaseExclusiveLock"/>）で待たせて作る。接続診断は診断を
    /// 別スレッド（<c>Task.Run</c>）で行うので、待たされている間も UI スレッドは応答し、閉じる操作が実際に処理される
    /// （UI スレッドの上で DB を待つ処理を使うと UI スレッドごと止まり、ガードが無くても閉じない＝何も検査しない。
    /// 初版は設定の保存で作ろうとして、これを実測した）。
    /// </para>
    /// <para>
    /// 対: ロックを外して診断が終わると、同じ Esc でダイアログは閉じる（ガードは処理中にだけ効く）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class BusyCloseGuardUiTests
    {
        [Fact]
        public void 処理中のダイアログはEscとAltF4で閉じず処理が終わればEscで閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var systemManage = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);

            using var dbLock = DatabaseExclusiveLock.Acquire();
            systemManage.ClickButton(TestConstants.OpenConnectionDiagnosticsButton);
            var diagnostics = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, systemManage.Window, TestConstants.ConnectionDiagnosticsDialogName));

            // 前提: 診断が DB の排他ロックで待たされ、ダイアログは処理中。UIA が応答する＝UI スレッドは止まっていない
            diagnostics.FindByNameWithRetry(TestConstants.ConnectionDiagnosticsBusyMessage).Should().NotBeNull(
                $"前提: 診断中は処理中オーバーレイに「{TestConstants.ConnectionDiagnosticsBusyMessage}」が出ること");

            // 1. Esc は処理中に効かない
            PressOn(diagnostics, VirtualKeyShort.ESCAPE);
            AssertStillOpenAndBusy(fixture, systemManage, diagnostics, "処理中に Esc を押しても、ダイアログは閉じないこと");

            // 2. Alt+F4（✕ と同じ SC_CLOSE）も処理中に効かない
            ScreenshotHelper.RequireForeground(diagnostics.Window);
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4);
            AssertStillOpenAndBusy(fixture, systemManage, diagnostics, "処理中に Alt+F4 を押しても、ダイアログは閉じないこと");

            // 3. 対: ロックを外して診断が終わると、同じ Esc で閉じる
            dbLock.Release();
            var done = Retry.WhileFalse(
                () => diagnostics.FindByName(TestConstants.ConnectionDiagnosticsBusyMessage) == null,
                TimeSpan.FromSeconds(30)).Success;
            done.Should().BeTrue("ロックを外したら、診断が終わって処理中を抜けること");
            PressOn(diagnostics, VirtualKeyShort.ESCAPE);
            var closed = Retry.WhileFalse(
                () => !DialogLocator.IsOpen(fixture, systemManage.Window, TestConstants.ConnectionDiagnosticsDialogName),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            closed.Should().BeTrue("処理が終わった後の Esc では、ダイアログが閉じること（ガードは処理中にだけ効く）");
        }

        private static void PressOn(DialogPageBase dialog, VirtualKeyShort key)
        {
            ScreenshotHelper.RequireForeground(dialog.Window);
            Keyboard.Type(key);
        }

        /// <summary>
        /// ダイアログが開いたままで、まだ処理中であることを表明する。
        /// </summary>
        /// <remarks>
        /// 「処理中のまま」も合わせて見る。診断が DB の待ちを使い切って処理中を抜けると、その後の閉じる操作は正当に効くので、
        /// ガードを検査したことにならない。
        /// </remarks>
        private static void AssertStillOpenAndBusy(AppFixture fixture, DialogPageBase opener, DialogPageBase dialog, string because)
        {
            var closed = Retry.WhileFalse(
                () => !DialogLocator.IsOpen(fixture, opener.Window, TestConstants.ConnectionDiagnosticsDialogName),
                TimeSpan.FromSeconds(2)).Success;
            closed.Should().BeFalse(because);
            dialog.FindByName(TestConstants.ConnectionDiagnosticsBusyMessage).Should().NotBeNull(
                "前提: 確かめている間もダイアログは処理中であること（処理中を抜けた後の閉じる操作は正当に効く）");
        }
    }
}
