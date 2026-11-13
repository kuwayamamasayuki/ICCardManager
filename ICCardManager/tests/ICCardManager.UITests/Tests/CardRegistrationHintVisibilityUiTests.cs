#nullable enable
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
    /// カード登録方法の選択ダイアログで「紙の出納簿からの繰越」を選んだとき、ヒント（管理者マニュアル「1.6」への案内）が
    /// スクロールせずに見えることの回帰テスト（Issue #2465）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 是正前はヒントが内容の最下端にあり、繰越の入力欄（繰越額・累計受入・累計払出）の下に押し出されて、ウィンドウの高さの
    /// 上限（MaxHeight 680）の中では文字サイズ「中」（標準）でもヒントの一部がスクロールの外に隠れていた。ヒントは
    /// フォーカスを受けないので、Tab で入力欄を移っても自動ではスクロールされず、スクロールしないと読めない。
    /// </para>
    /// <para>
    /// ここでは実際の画面で、ヒントの矩形がスクロール領域の表示範囲の中に収まることを文字サイズ「中」「大」「特大」で確かめる。
    /// 未登録のカードの読み取りは DEBUG パネル（<c>HybridCardReader.SimulateCardRead</c>）で起こすため、Debug 起動のときだけ実行する
    /// （<see cref="CardRegistrationCarryoverUiTests"/> と同じ）。ダイアログはキャンセルで閉じ、DB に何も書かない。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class CardRegistrationHintVisibilityUiTests
    {
        private const string SkipReason =
            "Issue #2465: 未登録カードの読み取りは DEBUG パネル（DEBUG ビルド限定）で起こすため、Debug 起動のときだけ実行する。";

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        public CardRegistrationHintVisibilityUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [SkippableTheory]
        [InlineData("medium")]
        [InlineData("large")]
        [InlineData("xlarge")]
        public void 繰越を選んでもヒントがスクロールせずに見えること(string fontSize)
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);
            using var fixture = AppFixture.LaunchWithSeed(
                conn =>
                {
                    ScreenshotSeedData.Seed(conn);
                    using var font = conn.CreateCommand();
                    font.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('font_size', @font)";
                    font.Parameters.AddWithValue("@font", fontSize);
                    font.ExecuteNonQuery();
                },
                AppFixture.SuppressDebugTestData);
            // メイン画面を左上へ寄せる（ダイアログは所有者の中央に開くので、画面外へはみ出させない。FlaUI キャプチャの DPI 対策）
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var cardCountBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card");

            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var manage = page.ClickToolbarButtonAndWaitForDialog(TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            manage.ClickButton(TestConstants.CardNewRegistrationButton);
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            Retry.WhileNull(() => manage.FindByNameStartsWith(TestConstants.CardManageCardReadStatusPrefix), Wait).Result
                .Should().NotBeNull("未登録のカードを読み取ったら、カード種別の確認を促す案内が出ること");
            manage.ClickButton(TestConstants.CardManageSaveButton);

            var mode = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName));
            var carryover = mode.FindByNameWithRetry(TestConstants.CardRegistrationCarryoverRadio);
            carryover.Should().NotBeNull("「紙の出納簿からの繰越」の選択肢があること");
            carryover!.AsRadioButton().IsChecked = true;
            Retry.WhileFalse(() => mode.FindByNameWithRetry(TestConstants.CardRegistrationIncomeTotalTextBox)!.IsEnabled, Wait).Success
                .Should().BeTrue("前提: 繰越の入力欄が有効になったこと");

            var hint = Retry.WhileNull(
                () => mode.Window.FindFirstDescendant(cf => cf.ByAutomationId(TestConstants.CardRegistrationHintTextId)), Wait).Result;
            var viewport = Retry.WhileNull(
                () => mode.Window.FindFirstDescendant(cf => cf.ByAutomationId(TestConstants.CardRegistrationContentScrollViewerId)), Wait).Result;
            hint.Should().NotBeNull("前提: ヒントが見つかること");
            viewport.Should().NotBeNull("前提: 内容のスクロール領域が見つかること");

            var hintRect = hint!.BoundingRectangle;
            var viewportRect = viewport!.BoundingRectangle;
            hintRect.Height.Should().BeGreaterThan(0, "前提: ヒントの矩形が読めていること（空の矩形では包含の検査が空振りする）");
            viewportRect.Height.Should().BeGreaterThan(0, "前提: スクロール領域の矩形が読めていること");

            var detail = $"文字サイズ {fontSize}: ヒント {hintRect}、スクロール領域 {viewportRect}";
            hintRect.Top.Should().BeGreaterOrEqualTo(viewportRect.Top,
                $"ヒントの上端がスクロール領域の上に隠れていないこと（{detail}）");
            hintRect.Bottom.Should().BeLessOrEqualTo(viewportRect.Bottom,
                $"ヒント（管理者マニュアル「1.6」への案内）の下端がスクロールの外に隠れていないこと。フォーカスを受けないので、" +
                $"Tab で入力欄を移ってもスクロールされず、隠れると読まれない（{detail}）");

            // 高さの上限を作業領域の高さまで広げたので、下端（OK）が画面の外へ出ていないことも確かめる（位置を作業領域へ寄せる処理）
            var ok = Retry.WhileNull(
                () => mode.Window.FindFirstDescendant(cf => cf.ByAutomationId(TestConstants.CardRegistrationOkButtonId)), Wait).Result;
            ok.Should().NotBeNull("前提: OK ボタンが見つかること");
            var okRect = ok!.BoundingRectangle;
            var workArea = NativeWindows.WorkAreaOf(NativeWindows.HandleOf(mode.Window));
            okRect.Height.Should().BeGreaterThan(0, "前提: OK ボタンの矩形が読めていること");
            workArea.Contains(okRect).Should().BeTrue(
                $"OK ボタンがダイアログの載っているモニターの作業領域の中にあること（文字サイズ {fontSize}: OK {okRect}、作業領域 {workArea}）");

            mode.ClickButton(TestConstants.CardRegistrationCancelButton);
            Retry.WhileTrue(() => DialogLocator.IsOpen(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName), Wait)
                .Success.Should().BeTrue("閉じると登録がキャンセルされること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card").Should().Be(cardCountBefore, "キャンセルしたので登録されないこと");
        }
    }
}
