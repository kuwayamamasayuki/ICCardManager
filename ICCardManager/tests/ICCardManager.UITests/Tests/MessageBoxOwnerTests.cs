using System;
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
    /// MessageBox にオーナーが付き、下位のダイアログが押せなくなることの回帰テスト
    /// （Issue #2190。07_テスト設計書 UT-089 / UT-096 の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 故障（Issue #1794）: オーナーを渡さない <c>MessageBox.Show</c> は <c>GetActiveWindow()</c> でオーナーを決める。
    /// <b>アプリが前面にないとき</b>これは NULL になり、MessageBox は ownerless で開く。メイン画面は先に開いた
    /// ダイアログの <c>ShowDialog()</c> で無効になっているが、<b>そのダイアログ自身は有効なまま</b>残り、
    /// 確認の MessageBox を出したまま削除・払い戻しなどのボタンを押せてしまう。
    /// </para>
    /// <para>
    /// 単体テスト（<c>DialogOwnerResolverTests</c> / <c>DialogServiceOwnerTests</c>）が固定できるのは
    /// 「オーナーを選ぶ規則」と「選んだオーナーを MessageBox へ渡すこと」までで、実際に押せなくなることは
    /// 実機でしか確かめられなかった。本テストはアプリを背面に回して（<see cref="ForegroundDecoy"/>）故障の条件を作り、
    /// Win32 に問い合わせて表明する。アプリが前面にあると、オーナーを渡さない実装でも偶然オーナーが付いて緑になる。
    /// </para>
    /// <para>
    /// MessageBox は、カード削除で職員証の仮想タッチで認証を通した直後に出る「削除できません」を使う。
    /// Issue #2474 で削除は「削除確認 → 認証」の順になり、「削除確認」はボタンの Invoke の直後に出るようになった
    /// （Invoke の直後はアプリが前面を取るため、背面で出させる条件が作れない。<see cref="ForegroundDecoy"/>）。
    /// そこで、削除確認で「はい」を押した後、認証を待つ間に他の PC がそのカードを削除した状況を DB に作り、
    /// 認証の後の読み直しで出る「削除できません」を、認証成功から約 700ms 後に背面で出させる。
    /// 削除はアプリからは行われない（操作ログに削除が残らないことを DB で表明する）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class MessageBoxOwnerTests
    {
        private const string SkipReason =
            "Issue #2190: 「削除できません」は職員証認証の後に出し、認証は仮想タッチ（DEBUG ビルド限定）でしか通せないため、Debug 起動のときだけ実行する。";

        /// <summary>カード削除の確認 MessageBox のタイトル（<c>CardManageViewModel</c> の <c>ShowWarningConfirmation</c>）。</summary>
        private const string DeleteConfirmationTitle = "削除確認";

        /// <summary>削除を断る MessageBox のタイトル（<c>CardManageViewModel.NotifyWriteRefusedAsync</c> の <c>ShowError</c>）。</summary>
        private const string DeleteRefusedTitle = "削除できません";

        [SkippableFact]
        public void アプリが背面のときもダイアログ上のMessageBoxはそのダイアログをオーナーにして押せなくすること()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var cardManage = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            SelectCardRow(cardManage, ScreenshotSeedData.NormalCardNumber);
            cardManage.WaitUntilEnabled(TestConstants.CardDeleteButton).Should().BeTrue("行を選んだら「削除」ボタンが有効になること");
            cardManage.ClickButton(TestConstants.CardDeleteButton);

            // Issue #2474: 削除確認（はい）の後に職員証認証（#429）を求める
            var confirmation = MessageBoxOperations.WaitFor(fixture, cardManage.Window, DeleteConfirmationTitle);
            MessageBoxOperations.Answer(confirmation, MessageBoxOperations.YesPrefix);

            var auth = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, cardManage.Window, TestConstants.StaffAuthDialogName));
            var dialogHandle = NativeWindows.HandleOf(cardManage.Window);

            // 認証を待つ間に、他の PC がそのカードを削除した（共有フォルダーモード）。認証の後の読み直しで
            // 削除を断り、カード一覧を読み直してから「削除できません」を出す
            DatabaseProbe.ExecuteAsAnotherPc(
                "UPDATE ic_card SET is_deleted = 1 WHERE card_idm = @card",
                ("@card", ScreenshotSeedData.NormalCardIdm)).Should().Be(1, "前提: 他の PC の削除を DB に作れたこと");

            // 故障の条件: 認証の完了を待つ間に、職員が別のアプリへ切り替えた（Issue #1794 のシナリオ）。
            // UIA の Invoke は呼ばれたアプリへ前面化の権利を渡し、認証ダイアログが前面を取る（実測）ので、
            // 前面を取ったのを見届けてから別のウィンドウを前面に戻す。認証成功から「削除できません」までは約 700ms ある
            using var decoy = ForegroundDecoy.Activate();
            auth.ClickButton(TestConstants.DebugVirtualTouchButtonName);
            _ = Retry.WhileFalse(
                () => NativeWindows.ProcessIdOf(NativeWindows.Foreground) == fixture.App.ProcessId,
                TimeSpan.FromSeconds(2));
            decoy.BringToFront();

            var messageBox = MessageBoxOperations.WaitFor(fixture, cardManage.Window, DeleteRefusedTitle);
            var messageBoxHandle = NativeWindows.HandleOf(messageBox);

            decoy.IsForeground.Should().BeTrue(
                $"前提: MessageBox が出た時点でアプリは背面にあること（前面は {NativeWindows.Describe(NativeWindows.Foreground)}）。" +
                "前面だとオーナーを渡さない実装でも偶然オーナーが付き、このテストは何も検査しない");
            NativeWindows.OwnerOf(messageBoxHandle).Should().Be(dialogHandle,
                "「削除できません」の MessageBox のオーナーは、それを出した交通系ICカード管理ダイアログであること（ownerless なら 0）");
            NativeWindows.IsEnabled(dialogHandle).Should().BeFalse(
                "MessageBox を出している間、下のダイアログは Win32 レベルで無効になり、削除などのボタンを押せないこと");

            // 対: 閉じるとダイアログは再び操作でき、アプリは削除を記録していない
            MessageBoxOperations.Answer(messageBox, "OK");

            var enabledAgain = Retry.WhileFalse(
                () => NativeWindows.IsEnabled(dialogHandle),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            enabledAgain.Should().BeTrue("MessageBox を閉じたら、ダイアログは再び操作できること");
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM operation_log WHERE target_table = 'ic_card' AND action = 'DELETE' AND target_id = @card",
                ("@card", ScreenshotSeedData.NormalCardIdm)).Should().Be(0L,
                    "他の PC が先に削除していたので、このアプリは削除を記録しないこと");
        }
    }
}
