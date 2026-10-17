using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
    /// バス停名入力の自動入力（Issue #2251。PR #2252 の手動確認 1〜4 を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DEBUG ビルドの仮想タッチで返却し、開いたバス停名入力ダイアログを実際に操作する。仮想タッチの「履歴追加」は
    /// 今日・200 円・駅名なし（＝バス利用）の行を作るので、2 回押せば「同じ金額・同じ利用日のバスが続く」形になる。
    /// </para>
    /// <para>
    /// クリックの確認（手動確認 3）は実際のマウス操作が要るため、DPI 対応にしてからウィンドウを左上へ寄せる
    /// （<see cref="ScreenshotHelper.EnsureProcessDpiAware"/>）。読み上げ（手動確認 4）は、ナレーターが補足として読む
    /// UIA の HelpText を表明する。実際に読み上げられるか（聞き取り）は表明できない。
    /// </para>
    /// <para>注記の文言は本体の <c>BusStopInputItem.SuggestedAutoFillNote</c> / <c>RoundTripAutoFillNote</c> と同じもの（UITests は本体を参照しないので、一致は <c>BusStopAutoFillUiTestConstantsTests</c> が固定する。ずれると「注記が出ないこと」の表明が素通りする）。</para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class BusStopAutoFillUiTests
    {
        private const string SkipReason =
            "Issue #2251: 仮想タッチは DEBUG ビルドにしか無いため、Debug 起動のときだけ実行する。";

        private const string SuggestedNote = TestConstants.BusStopSuggestedAutoFillNote;

        private const string RoundTripNote = TestConstants.BusStopRoundTripAutoFillNote;

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        public BusStopAutoFillUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        /// <summary>手動確認 1: 上の行の「A～B」から次の行へ「B～A」が入り、上の行を直すと入れ直し、消すと取り除く。</summary>
        [SkippableFact]
        public void 確認1_往復の帰りが次の行へ入り上の行に追随すること()
        {
            SkipUnlessDebug();
            using var fixture = LaunchWithBusStopInput(ScreenshotSeedDataForInput);
            var dialog = ReturnWithBusEntries(fixture, entryCount: 2);

            var (first, second) = TwoInputs(dialog);
            ValueOf(second).Should().BeEmpty("前提: 過去の入力が無いので 2 行目は空で開くこと");
            NoteVisible(dialog, RoundTripNote).Should().BeFalse("前提: 何も入力していなければ往復の注記は出ないこと");

            first.Patterns.Value.Pattern.SetValue("天神～博多");
            WaitForValue(second, "博多～天神").Should().BeTrue("1 行目の「天神～博多」から 2 行目へ「博多～天神」が入ること");
            NoteVisible(dialog, RoundTripNote).Should().BeTrue("2 行目に往復の注記が出ること");
            HelpTextOf(second).Should().Be(RoundTripNote, "2 行目の読み上げの補足が注記に切り替わること（手動確認 4）");

            first.Patterns.Value.Pattern.SetValue("天神～薬院");
            WaitForValue(second, "薬院～天神").Should().BeTrue("1 行目を直すと 2 行目も入れ直されること");

            first.Patterns.Value.Pattern.SetValue(string.Empty);
            WaitForValue(second, string.Empty).Should().BeTrue("1 行目を消すと 2 行目の自動の値も取り除かれること");
            NoteVisible(dialog, RoundTripNote).Should().BeFalse("取り除いたら注記も消えること");
            HelpTextOf(second).Should().NotBe(RoundTripNote, "取り除いたら読み上げの補足も元に戻ること");
        }

        /// <summary>手動確認 2: 同じ職員・同じ金額で 2 回入力した区間が既定値として入り、Enter でそのまま保存できる。</summary>
        [SkippableFact]
        public void 確認2_同じ職員と金額の実績が既定値として入りEnterで保存できること()
        {
            SkipUnlessDebug();
            using var fixture = LaunchWithBusStopInput(RegressionSeedData.SeedWithPastBusStopsForVirtualTouch);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = ReturnWithBusEntries(fixture, entryCount: 1);

            var input = Inputs(dialog).Single();
            WaitForValue(input, RegressionSeedData.PastBusStops).Should().BeTrue(
                $"同じ職員・同じ金額で 2 回入力した「{RegressionSeedData.PastBusStops}」が既定値として入ること");
            NoteVisible(dialog, SuggestedNote).Should().BeTrue("既定値の注記が出ること");
            HelpTextOf(input).Should().Be(SuggestedNote, "読み上げの補足が既定値の注記であること（手動確認 4）");

            // 欄へ入ると候補が開く → Esc で閉じてから Enter（候補が開いたままの Enter は候補の確定になる。#2072）
            input.Focus();
            if (Retry.WhileFalse(() => SuggestionListOpen(dialog), Wait).Result)
            {
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                Retry.WhileTrue(() => SuggestionListOpen(dialog), Wait).Success.Should().BeTrue("Esc で候補が閉じること");
                DialogStillOpen(page).Should().BeTrue("候補が開いている間の Esc はダイアログを閉じない（スキップしない）こと");
            }

            Keyboard.Type(VirtualKeyShort.ENTER);
            Retry.WhileTrue(() => DialogStillOpen(page), TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds))
                .Success.Should().BeTrue("Enter で保存されダイアログが閉じること（確認ダイアログが挟まらないこと）");

            DatabaseProbe.Scalar(
                "SELECT d.bus_stops FROM ledger_detail d JOIN ledger l ON l.id = d.ledger_id " +
                "WHERE l.card_idm = @card AND d.is_bus = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(RegressionSeedData.PastBusStops,
                "自動で入った既定値がそのまま明細に保存されること");
        }

        /// <summary>手動確認 3（前半）: 自動の欄をマウスでクリックすると全選択になり、打ち始めた文字で置き換わる。</summary>
        [SkippableFact]
        public void 確認3a_自動の欄をクリックすると全選択になり打った文字で置き換わること()
        {
            SkipUnlessDebug();
            using var fixture = LaunchWithBusStopInput(ScreenshotSeedDataForInput);
            var dialog = ReturnWithBusEntries(fixture, entryCount: 2);
            var (first, second) = TwoInputs(dialog);

            first.Patterns.Value.Pattern.SetValue("天神～博多");
            WaitForValue(second, "博多～天神").Should().BeTrue("前提: 2 行目に往復の帰りが自動で入ること");
            FocusInspection.HasFocusWithin(second).Should().BeFalse("前提: クリック前の 2 行目はフォーカスを持たないこと");

            PrepareForMouse(dialog);
            second.Click();
            Retry.WhileFalse(() => SelectedText(second) == "博多～天神", Wait).Result
                .Should().BeTrue($"クリックで値の全体が選択されること（実際の選択: 「{SelectedText(second)}」）");

            Keyboard.Type("薬院");
            WaitForValue(second, "薬院").Should().BeTrue($"打ち始めた文字で置き換わること（実際: 「{ValueOf(second)}」）");
            NoteVisible(dialog, RoundTripNote).Should().BeFalse("職員が書き換えたら注記は消えること");
        }

        /// <summary>
        /// 手動確認 3（後半）: 全選択の後に値の途中をもう一度クリックすると、候補を閉じるだけで終わらずキャレットが置かれるか。
        /// </summary>
        [SkippableFact]
        public void 確認3b_全選択の後に値の途中をクリックするとキャレットが置かれること()
        {
            SkipUnlessDebug();
            using var fixture = LaunchWithBusStopInput(ScreenshotSeedDataForInput);
            var dialog = ReturnWithBusEntries(fixture, entryCount: 2);
            var (first, second) = TwoInputs(dialog);

            first.Patterns.Value.Pattern.SetValue("天神～博多");
            WaitForValue(second, "博多～天神").Should().BeTrue("前提: 2 行目に往復の帰りが自動で入ること");

            PrepareForMouse(dialog);
            second.Click();
            Retry.WhileFalse(() => SelectedText(second) == "博多～天神", Wait).Result
                .Should().BeTrue("前提: 1 回目のクリックで全選択になること");
            var popupAfterFirstClick = SuggestionListOpen(dialog);

            // 値の途中（左端から 2 文字分ほど右）をもう一度クリックする
            var rect = second.BoundingRectangle;
            var charWidth = Math.Max(8, rect.Height / 3);
            Mouse.Click(new System.Drawing.Point(rect.Left + 8 + (charWidth * 2), rect.Top + (rect.Height / 2)));
            var selectionCleared = Retry.WhileFalse(() => SelectedText(second) == string.Empty, Wait).Result;
            var popupAfterSecondClick = SuggestionListOpen(dialog);

            Keyboard.Type("X");
            Retry.WhileFalse(() => ValueOf(second).Contains("X"), Wait);
            var value = ValueOf(second);

            // 観測した事実を because に並べて、どの段で食い違ったかを結果から読めるようにする
            var observed = $"1 回目後の候補={popupAfterFirstClick}, 2 回目後の候補={popupAfterSecondClick}, 選択解除={selectionCleared}, 入力後=「{value}」";
            selectionCleared.Should().BeTrue($"2 回目のクリックで全選択が外れキャレットになること（{observed}）");
            value.Should().HaveLength("博多～天神".Length + 1, $"値は置き換わらず 1 文字挿入されること（{observed}）");
            value.Should().NotStartWith("X", $"キャレットは先頭ではなくクリックした位置にあること（{observed}）")
                .And.NotEndWith("X", $"キャレットは末尾ではなくクリックした位置にあること（{observed}）");
        }

        // ── ヘルパー ─────────────────────────────────

        private static void ScreenshotSeedDataForInput(System.Data.SQLite.SQLiteConnection conn) =>
            ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false);

        private static void SkipUnlessDebug() =>
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);

        private static AppFixture LaunchWithBusStopInput(Action<System.Data.SQLite.SQLiteConnection> seed) =>
            AppFixture.LaunchWithSeed(seed, AppFixture.SuppressDebugTestData);

        /// <summary>仮想タッチで履歴を <paramref name="entryCount"/> 件（今日・200 円・駅名なし）追加して返却し、バス停名入力を待つ。</summary>
        private static DialogPageBase ReturnWithBusEntries(AppFixture fixture, int entryCount)
        {
            // モーダルのバス停名入力が開くとメイン画面は無効になり動かせない（Transform の Move が拒否される）ので、先に寄せる
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            InvokeDebugPanelButton(page, TestConstants.DebugPanelVirtualTouchButton);
            var virtualDialog = new DialogPageBase(page.WaitForDialog(TestConstants.VirtualCardDialogName));
            for (var i = 0; i < entryCount; i++)
            {
                virtualDialog.ClickButton(TestConstants.VirtualCardAddEntryButton);
            }

            virtualDialog.ClickButton(TestConstants.VirtualCardExecuteButton);
            WaitForToastWithTitle(fixture, TestConstants.ReturnToastTitle);
            var dialog = new DialogPageBase(WaitForDialogLong(page, TestConstants.BusStopInputDialogName));
            Retry.WhileFalse(() => Inputs(dialog).Count == entryCount, Wait).Result
                .Should().BeTrue($"バス停名の入力欄が {entryCount} 行あること");
            return dialog;
        }

        private static IReadOnlyList<AutomationElement> Inputs(DialogPageBase dialog) =>
            dialog.Window.FindAllDescendants(cf => cf.ByName(TestConstants.BusStopNameInput).And(cf.ByControlType(ControlType.Edit)));

        private static (AutomationElement First, AutomationElement Second) TwoInputs(DialogPageBase dialog)
        {
            var inputs = Inputs(dialog);
            inputs.Should().HaveCount(2);
            return (inputs[0], inputs[1]);
        }

        private static string ValueOf(AutomationElement edit) =>
            edit.Patterns.Value.Pattern.Value.ValueOrDefault ?? string.Empty;

        private static bool WaitForValue(AutomationElement edit, string expected) =>
            Retry.WhileFalse(() => ValueOf(edit) == expected, Wait).Result;

        private static string HelpTextOf(AutomationElement edit) =>
            edit.Properties.HelpText.ValueOrDefault ?? string.Empty;

        private static string SelectedText(AutomationElement edit)
        {
            var ranges = edit.Patterns.Text.Pattern.GetSelection();
            return ranges.Length == 0 ? string.Empty : ranges[0].GetText(-1);
        }

        /// <summary>注記は入力例の位置の TextBlock に出る（Name は表示中の文言）。</summary>
        private static bool NoteVisible(DialogPageBase dialog, string note) =>
            dialog.Window.FindFirstDescendant(cf => cf.ByName(note).And(cf.ByControlType(ControlType.Text))) != null;

        /// <summary>候補の Popup は UIA に現れないので、中の ListBox で開閉を判定する。</summary>
        private static bool SuggestionListOpen(DialogPageBase dialog)
        {
            try
            {
                return dialog.Window.Automation.GetDesktop()
                    .FindFirstDescendant(cf => cf.ByName(TestConstants.BusStopSuggestionList).And(cf.ByControlType(ControlType.List))) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool DialogStillOpen(MainWindowPage page)
        {
            try
            {
                return page.Window.ModalWindows.Any(w => w.Name == TestConstants.BusStopInputDialogName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void PrepareForMouse(DialogPageBase dialog)
        {
            ScreenshotHelper.MoveToTopLeft(dialog.Window);
            ScreenshotHelper.RequireForeground(dialog.Window);
        }
    }
}
