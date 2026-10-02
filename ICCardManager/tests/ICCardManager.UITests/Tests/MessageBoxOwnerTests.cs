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
    /// 確認の MessageBox は、カード削除の「削除確認」を使う（職員証の仮想タッチで認証を通した直後に出る）。
    /// 「いいえ」で閉じるので副作用は無い（それも DB で表明する）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class MessageBoxOwnerTests
    {
        private const string SkipReason =
            "Issue #2190: 削除確認の前に職員証認証があり、仮想タッチ（DEBUG ビルド限定）でしか通せないため、Debug 起動のときだけ実行する。";

        /// <summary>カード削除の確認 MessageBox のタイトル（<c>CardManageViewModel</c> の <c>ShowWarningConfirmation</c>）。</summary>
        private const string DeleteConfirmationTitle = "削除確認";

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

            // 削除は職員証認証を要する（#429）。認証ダイアログの仮想タッチを押すと、続けて削除確認が出る
            var auth = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, cardManage.Window, TestConstants.StaffAuthDialogName));
            var dialogHandle = NativeWindows.HandleOf(cardManage.Window);

            // 故障の条件: 認証の完了を待つ間に、職員が別のアプリへ切り替えた（Issue #1794 のシナリオ）。
            // UIA の Invoke は呼ばれたアプリへ前面化の権利を渡し、認証ダイアログが前面を取る（実測）ので、
            // 前面を取ったのを見届けてから別のウィンドウを前面に戻す。認証成功から削除確認までは約 700ms ある
            using var decoy = ForegroundDecoy.Activate();
            auth.ClickButton(TestConstants.DebugVirtualTouchButtonName);
            _ = Retry.WhileFalse(
                () => NativeWindows.ProcessIdOf(NativeWindows.Foreground) == fixture.App.ProcessId,
                TimeSpan.FromSeconds(2));
            decoy.BringToFront();

            var messageBox = MessageBoxOperations.WaitFor(fixture, cardManage.Window, DeleteConfirmationTitle);
            var messageBoxHandle = NativeWindows.HandleOf(messageBox);

            decoy.IsForeground.Should().BeTrue(
                $"前提: MessageBox が出た時点でアプリは背面にあること（前面は {NativeWindows.Describe(NativeWindows.Foreground)}）。" +
                "前面だとオーナーを渡さない実装でも偶然オーナーが付き、このテストは何も検査しない");
            NativeWindows.OwnerOf(messageBoxHandle).Should().Be(dialogHandle,
                "確認の MessageBox のオーナーは、それを出した交通系ICカード管理ダイアログであること（ownerless なら 0）");
            NativeWindows.IsEnabled(dialogHandle).Should().BeFalse(
                "MessageBox を出している間、下のダイアログは Win32 レベルで無効になり、削除などのボタンを押せないこと");

            // 対: 「いいえ」で閉じるとダイアログは再び操作でき、カードは削除されていない
            MessageBoxOperations.Answer(messageBox, MessageBoxOperations.NoPrefix);

            var enabledAgain = Retry.WhileFalse(
                () => NativeWindows.IsEnabled(dialogHandle),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            enabledAgain.Should().BeTrue("MessageBox を閉じたら、ダイアログは再び操作できること");
            DatabaseProbe.Count(
                "SELECT is_deleted FROM ic_card WHERE card_idm = @card",
                ("@card", ScreenshotSeedData.NormalCardIdm)).Should().Be(0L, "「いいえ」を選んだのでカードは削除されないこと");
        }
    }
}
