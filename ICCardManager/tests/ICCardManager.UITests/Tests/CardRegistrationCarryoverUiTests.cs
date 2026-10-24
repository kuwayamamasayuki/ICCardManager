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
    /// カード登録方法の選択ダイアログで、繰越月を 3 月にすると累計受入・累計払出の入力欄が無効になることの回帰テスト（Issue #2326）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 3 月から繰越は新年度の始まり（導入行「前年度より繰越」）で、紙の出納簿の 3 月末時点の累計は旧年度のもの。
    /// 入力を受け付けると新年度の 5 月以降の累計行に上乗せされるため、欄を無効にする。保存しないことは単体テスト
    /// （<c>CardManageViewModelTests.SaveAsync_CarryoverMode_March_ShouldNotSavePaperCumulativeTotals</c>）が固定し、
    /// ここでは画面の有効・無効の切り替え（コードビハインド）を確かめる。
    /// </para>
    /// <para>
    /// 未登録のカードの読み取りはメイン画面の DEBUG パネル（<c>HybridCardReader.SimulateCardRead</c>）で起こすため、
    /// Debug 起動のときだけ実行する。ダイアログは閉じて登録をキャンセルし、DB に何も書かないことも確かめる。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class CardRegistrationCarryoverUiTests
    {
        private const string SkipReason =
            "Issue #2326: 未登録カードの読み取りは DEBUG パネル（DEBUG ビルド限定）で起こすため、Debug 起動のときだけ実行する。";

        // 繰越月のコンボは年度順（4 月〜3 月）。項目の UIA Name は表示名ではないため位置で選ぶ。
        private const int MayIndex = 1;
        private const int MarchIndex = 11;

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        [SkippableFact]
        public void 繰越月に3月を選ぶと累計の入力欄が無効になり3月以外では有効なこと()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);
            // 仮想タッチのカード（DEBUG パネルの「交通系ICカード」が模擬する IDm）を登録していない投入データ
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var cardCountBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card");

            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var manage = page.ClickToolbarButtonAndWaitForDialog(TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            manage.ClickButton(TestConstants.CardNewRegistrationButton);
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            // 保存ボタンは読み取り前から有効なので、読み取りの完了はステータス欄の案内で待つ
            Retry.WhileNull(() => manage.FindByNameStartsWith(TestConstants.CardManageCardReadStatusPrefix), Wait).Result
                .Should().NotBeNull("未登録のカードを読み取ったら、カード種別の確認を促す案内が出ること");
            manage.ClickButton(TestConstants.CardManageSaveButton);

            var mode = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName));
            var carryover = mode.FindByNameWithRetry(TestConstants.CardRegistrationCarryoverRadio);
            carryover.Should().NotBeNull("「紙の出納簿からの繰越」の選択肢があること");
            carryover!.AsRadioButton().IsChecked = true;

            var month = mode.FindByNameWithRetry(TestConstants.CardRegistrationCarryoverMonthCombo)!.AsComboBox();
            var income = mode.FindByNameWithRetry(TestConstants.CardRegistrationIncomeTotalTextBox)!;
            var expense = mode.FindByNameWithRetry(TestConstants.CardRegistrationExpenseTotalTextBox)!;

            month.Select(MarchIndex);
            Retry.WhileTrue(() => income.IsEnabled, Wait).Success
                .Should().BeTrue("3 月から繰越は新年度の始まりなので、累計受入は入力できないこと");
            expense.IsEnabled.Should().BeFalse("累計払出も入力できないこと");

            month.Select(MayIndex);
            Retry.WhileFalse(() => income.IsEnabled, Wait).Success
                .Should().BeTrue("3 月以外では紙の出納簿の累計を入力できること（Issue #1215）");
            expense.IsEnabled.Should().BeTrue();

            mode.ClickButton(TestConstants.CardRegistrationCancelButton);
            // WaitUntilClosed はメイン画面が有効に戻るまで待つが、ここでは交通系ICカード管理ダイアログが開いたままなので使わない
            Retry.WhileTrue(() => DialogLocator.IsOpen(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName), Wait)
                .Success.Should().BeTrue("閉じると登録がキャンセルされること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card").Should().Be(cardCountBefore, "キャンセルしたので登録されないこと");
        }
    }
}
