using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// ダイアログを Esc で閉じられることの回帰テスト（Issue #2192。07_テスト設計書 UT-058e の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 静的検査（<c>DialogEscapeCloseConventionTests</c>）は「Esc で閉じる手段が宣言されていること」までで、
    /// <b>入力欄や一覧にフォーカスがあるときにも Esc がダイアログへ届くか</b>は実機でしか確かめられなかった
    /// （フォーカスのあるコントロールが Esc を消費すると、宣言があっても閉じない）。
    /// </para>
    /// <para>
    /// 「一覧＋編集フォーム」型（交通系ICカード管理・職員管理）では、編集フォームを開いているときの Esc は
    /// <b>編集の取り消し</b>で、もう一度押すとダイアログが閉じる（#2080 の <c>EditFormKeyPolicy</c>）。1 回目で閉じないことも対で表明する。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class DialogEscapeUiTests
    {
        [Fact]
        public void カード一覧にフォーカスがあるときEscで交通系ICカード管理ダイアログが閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var cardManage = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            SelectCardRow(cardManage, ScreenshotSeedData.NormalCardNumber);

            var list = cardManage.FindByNameWithRetry(TestConstants.CardList);
            list.Should().NotBeNull();
            PressEscapeWithFocusOn(cardManage, list!);

            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.CardManageDialogName).Should().BeTrue("一覧にフォーカスがあっても、Esc でダイアログが閉じること");
        }

        [Fact]
        public void 編集フォームの入力欄では1回目のEscで編集を取り消し2回目でダイアログが閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var cardManage = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            SelectCardRow(cardManage, ScreenshotSeedData.NormalCardNumber);
            cardManage.WaitUntilEnabled(TestConstants.CardEditButton).Should().BeTrue("行を選んだら「編集」ボタンが有効になること");
            cardManage.ClickButton(TestConstants.CardEditButton);

            var input = Retry.WhileNull(
                () => VisibleElement(cardManage, TestConstants.CardNumberInput),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            input.Should().NotBeNull($"編集を始めたら、編集フォームの「{TestConstants.CardNumberInput}」が表示されること");

            // 1 回目: 編集フォームだけが閉じ、ダイアログは残る（入力内容ごとダイアログを閉じない。#2080）
            PressEscapeWithFocusOn(cardManage, input!);
            var formClosed = Retry.WhileFalse(
                () => VisibleElement(cardManage, TestConstants.CardNumberInput) == null,
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            formClosed.Should().BeTrue("入力欄で Esc を押したら、編集フォームが閉じること（編集の取り消し）");
            DialogLocator.IsOpen(fixture, fixture.MainWindow, TestConstants.CardManageDialogName)
                .Should().BeTrue("1 回目の Esc ではダイアログは閉じないこと");

            // 2 回目: ダイアログが閉じる
            var list = cardManage.FindByNameWithRetry(TestConstants.CardList);
            list.Should().NotBeNull();
            PressEscapeWithFocusOn(cardManage, list!);
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.CardManageDialogName).Should().BeTrue("編集を取り消した後の Esc で、ダイアログが閉じること");
        }

        [Fact]
        public void 設定ダイアログは入力欄にフォーカスがあってもEscで閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var settings = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName);

            var input = settings.FindByNameWithRetry(TestConstants.SettingsWarningBalanceInput);
            input.Should().NotBeNull($"設定ダイアログに「{TestConstants.SettingsWarningBalanceInput}」の入力欄があること");
            PressEscapeWithFocusOn(settings, input!);

            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.SettingsDialogName).Should().BeTrue("入力欄にフォーカスがあっても、Esc で設定ダイアログが閉じること");
        }

        // ── ヘルパー ─────────────────────────────────

        /// <summary>要素へフォーカスを移してから Esc を押す。キー入力は前面のウィンドウへ届くので、先に前面化を確かめる。</summary>
        private static void PressEscapeWithFocusOn(DialogPageBase dialog, AutomationElement element)
        {
            ScreenshotHelper.RequireForeground(dialog.Window);
            element.Focus();
            var focused = Retry.WhileFalse(() => HasFocusWithin(element), TimeSpan.FromSeconds(5)).Success;
            focused.Should().BeTrue($"前提: 「{element.Name}」（またはその中の要素）にフォーカスがあること");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
        }

        /// <summary>
        /// フォーカスのある要素が <paramref name="element"/> 自身かその子孫か。DataGrid は行やセルがフォーカスを持つため、
        /// 要素そのものの HasKeyboardFocus だけでは判定できない。
        /// </summary>
        private static bool HasFocusWithin(AutomationElement element)
        {
            try
            {
                var current = element.Automation.FocusedElement();
                for (var depth = 0; current != null && depth < 32; depth++)
                {
                    if (current.Equals(element))
                    {
                        return true;
                    }

                    current = current.Parent;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 表示されている（画面外でない）入力欄を名前で探す。Collapsed の要素は UIA ツリーから消える。
        /// </summary>
        /// <remarks>
        /// 名前だけで探すと、同じ名前を持つ一覧の列見出しやラベルが先に見つかり、フォーカスを移せない
        /// （実測: 「管理番号」で <c>SetFocus</c> が <c>InvalidOperationException</c> になった）。入力欄（Edit）に絞る。
        /// </remarks>
        private static AutomationElement? VisibleElement(DialogPageBase dialog, string name)
        {
            try
            {
                var element = dialog.Window.FindFirstDescendant(
                    cf => cf.ByName(name).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)));
                return element != null && !element.IsOffscreen ? element : null;
            }
            catch
            {
                return null;
            }
        }


    }
}
