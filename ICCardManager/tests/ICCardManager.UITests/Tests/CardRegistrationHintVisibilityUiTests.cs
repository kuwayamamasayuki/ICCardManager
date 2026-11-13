#nullable enable
using System;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
            using var fixture = LaunchWithFontSize(fontSize);
            var cardCountBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card");
            var (manage, mode) = OpenRegistrationModeDialog(fixture);
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

            CancelAndAssertNothingRegistered(fixture, manage, mode, cardCountBefore);
        }

        /// <summary>
        /// 最大化して元に戻しても、ダイアログの位置が変わらないこと（Issue #2465 レビュー 4 回目）。
        /// </summary>
        /// <remarks>
        /// 大きさが変わると位置を作業領域の中へ寄せるが、最大化しているときに寄せると、最大化した大きさ（作業領域と同じかそれ以上）で
        /// 寄せるので位置が作業領域の端（左上）に書き換わり、元に戻したときにダイアログがそこへ動いてしまう。最大化中は上限だけを合わせる。
        /// </remarks>
        [SkippableFact]
        public void 最大化して元に戻しても位置が変わらないこと()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);
            using var fixture = LaunchWithFontSize("medium");
            var cardCountBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card");
            var (manage, mode) = OpenRegistrationModeDialog(fixture);

            var window = mode.Window.Patterns.Window.Pattern;
            window.CanMaximize.Value.Should().BeTrue("前提: ダイアログは最大化できる（ResizeMode=CanResize）");
            // 開いた直後は Loaded・SizeChanged で位置を寄せている途中のことがあるので、待ってから 2 回続けて同じ値になるまで読み直す
            Thread.Sleep(SettleDelay);
            var before = ReadStableBounds(mode.Window);

            window.SetWindowVisualState(WindowVisualState.Maximized);
            Retry.WhileFalse(() => window.WindowVisualState.Value == WindowVisualState.Maximized, Wait).Success
                .Should().BeTrue("前提: 最大化できたこと");
            Thread.Sleep(SettleDelay);
            window.SetWindowVisualState(WindowVisualState.Normal);
            Retry.WhileFalse(() => window.WindowVisualState.Value == WindowVisualState.Normal, Wait).Success
                .Should().BeTrue("前提: 元に戻せたこと");
            Thread.Sleep(SettleDelay);

            var after = mode.Window.BoundingRectangle;
            after.Location.Should().Be(before.Location,
                $"最大化して元に戻したら、元の位置に戻ること（最大化中に位置を作業領域へ寄せると端へ動く。前 {before}、後 {after}）");

            CancelAndAssertNothingRegistered(fixture, manage, mode, cardCountBefore);
        }

        /// <summary>ウィンドウの矩形を、2 回続けて同じ値になるまで読み直す（落ち着く前の位置を基準にしない）。</summary>
        private static System.Drawing.Rectangle ReadStableBounds(FlaUI.Core.AutomationElements.Window window)
        {
            var previous = window.BoundingRectangle;
            var stable = Retry.WhileFalse(
                () =>
                {
                    Thread.Sleep(200);
                    var current = window.BoundingRectangle;
                    var same = current == previous;
                    previous = current;
                    return same;
                },
                Wait).Success;
            stable.Should().BeTrue($"前提: ダイアログの位置が落ち着くこと（最後の矩形 {previous}）");
            return previous;
        }

        /// <summary>レイアウトが落ち着くまでの待ち時間（最大化・元に戻すのアニメーションと SizeChanged の処理）</summary>
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(800);

        private static AppFixture LaunchWithFontSize(string fontSize)
        {
            var fixture = AppFixture.LaunchWithSeed(
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
            return fixture;
        }

        /// <summary>未登録のカードを読み取り、「保存」でカード登録方法の選択ダイアログを開く。</summary>
        private static (DialogPageBase Manage, DialogPageBase Mode) OpenRegistrationModeDialog(AppFixture fixture)
        {
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var manage = page.ClickToolbarButtonAndWaitForDialog(TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            manage.ClickButton(TestConstants.CardNewRegistrationButton);
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            Retry.WhileNull(() => manage.FindByNameStartsWith(TestConstants.CardManageCardReadStatusPrefix), Wait).Result
                .Should().NotBeNull("未登録のカードを読み取ったら、カード種別の確認を促す案内が出ること");
            manage.ClickButton(TestConstants.CardManageSaveButton);

            var mode = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName));
            return (manage, mode);
        }

        private static void CancelAndAssertNothingRegistered(AppFixture fixture, DialogPageBase manage, DialogPageBase mode, long cardCountBefore)
        {
            mode.ClickButton(TestConstants.CardRegistrationCancelButton);
            Retry.WhileTrue(() => DialogLocator.IsOpen(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName), Wait)
                .Success.Should().BeTrue("閉じると登録がキャンセルされること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card").Should().Be(cardCountBefore, "キャンセルしたので登録されないこと");
        }
    }
}
