using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// ダイアログを開いた直後のキーボード フォーカスの回帰テスト（Issue #2194。07_テスト設計書 UT-054 の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 静的検査（<c>DialogInitialFocusTests</c>）は XAML の <c>FocusManager.FocusedElement</c> やコードビハインドの <c>Focus()</c> の
    /// <b>宣言</b>を固定する。実際にその要素へフォーカスが来るか（要素が無効・非表示だとフォーカスは移らない）は実機でしか分からなかった。
    /// 期待する要素は静的検査と同じ（UT-054 の表）。
    /// </para>
    /// <para>
    /// フォーカスはシステム全体で 1 つなので、判定の前にダイアログを前面化する（前面化はウィンドウが最後に持っていたフォーカスを戻す）。
    /// 行の追加・修正とバス停名入力は職員証の仮想タッチを要するため Debug 起動のときだけ実行する。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class DialogInitialFocusUiTests
    {
        private const string DebugOnlyReason =
            "Issue #2194: このダイアログは職員証の仮想タッチ（DEBUG ビルド限定）を経て開くため、Debug 起動のときだけ実行する。";

        public DialogInitialFocusUiTests()
        {
            // 履歴を開く操作（OpenCardHistory）は行を実際にクリックするため、座標系をそろえる
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Theory]
        [InlineData(TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName, TestConstants.CardList)]
        [InlineData(TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName, TestConstants.SettingsToastPositionComboBox)]
        [InlineData(TestConstants.OpenDataExportImportButton, TestConstants.DataExportImportDialogName, TestConstants.ExportDataTypeComboBox)]
        public void ツールバーから開いたダイアログの初期フォーカスが既定の要素にあること(string openButton, string dialogName, string expectedFocus)
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = page.ClickToolbarButtonAndWaitForDialog(openButton, dialogName);

            AssertInitialFocus(dialog, expectedFocus, ControlType.Custom);
        }

        /// <summary>
        /// 既存の行を修正するときは、出納日付にフォーカスがある（入力エラーの無い行。UT-054 の既定）。
        /// </summary>
        [SkippableFact]
        public void 履歴行の修正ダイアログの初期フォーカスが出納日付にあること()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), DebugOnlyReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = OpenNormalCardHistory(fixture);
            page.InvokeHistoryRowButton(EditTargetSummary, TestConstants.HistoryRowEditButton);
            PassStaffAuthentication(fixture, fixture.MainWindow);

            var dialog = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName));
            AssertInitialFocus(dialog, TestConstants.LedgerRowEditDateInput, ControlType.Custom);
        }

        /// <summary>
        /// 行を追加するときは、最初の入力エラー（空の摘要）にフォーカスがある（Issue #1279）。
        /// </summary>
        /// <remarks>
        /// 行の追加・修正ダイアログは、表示した時点で入力エラーがあればその欄へフォーカスを移す（<c>FocusFirstErrorField</c>）。
        /// 追加モードは摘要が空で必ずエラーになるので、XAML の既定（出納日付）より優先して摘要へ移るのが仕様である
        /// （初版は追加モードで出納日付を期待して失敗し、この仕様を確かめた）。
        /// </remarks>
        [SkippableFact]
        public void 履歴行の追加ダイアログの初期フォーカスが最初の入力エラーの摘要にあること()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), DebugOnlyReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = OpenNormalCardHistory(fixture);
            page.ClickButton(TestConstants.AddLedgerRowButton);
            PassStaffAuthentication(fixture, fixture.MainWindow);

            var dialog = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName));
            AssertInitialFocus(dialog, TestConstants.LedgerRowEditSummaryInput, ControlType.Edit);
        }

        [SkippableFact]
        public void バス停名入力ダイアログの初期フォーカスが最初のバス停名の入力欄にあること()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), DebugOnlyReason);

            using var fixture = AppFixture.LaunchWithSeed(
                conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false),
                AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 駅名なし（＝バス利用）の履歴 1 件で返却すると、バス停名入力ダイアログが開く
            ExecuteVirtualTouchWithOneEntry(page);
            var dialog = new DialogPageBase(WaitForDialogLong(page, TestConstants.BusStopInputDialogName));

            AssertInitialFocus(dialog, TestConstants.BusStopNameInput, ControlType.Edit);
        }

        /// <summary>修正の対象にする、入力エラーの無い既存の行（<see cref="ScreenshotSeedData.Seed"/> の通常カード）。</summary>
        private const string EditTargetSummary = "鉄道（博多～天神）";

        private static MainWindowPage OpenNormalCardHistory(AppFixture fixture)
        {
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);
            return page;
        }

        /// <summary>
        /// フォーカスが名前で指定した要素の内側にあることを表明する。
        /// </summary>
        /// <param name="dialog">ダイアログ。</param>
        /// <param name="expectedName">フォーカスがあるべき要素の AutomationProperties.Name。</param>
        /// <param name="controlType">
        /// 同名の要素（ラベル・列見出し）と取り違えないよう絞る種類。<see cref="ControlType.Custom"/> なら絞らない
        /// （ComboBox・DataGrid・DatePicker は名前が一意なので絞る必要が無い）。
        /// </param>
        private static void AssertInitialFocus(DialogPageBase dialog, string expectedName, ControlType controlType)
        {
            var target = dialog.Window.FindFirstDescendant(cf => controlType == ControlType.Custom
                ? cf.ByName(expectedName)
                : cf.ByName(expectedName).And(cf.ByControlType(controlType)));
            target.Should().NotBeNull($"「{dialog.Name}」に「{expectedName}」があること");

            ScreenshotHelper.RequireForeground(dialog.Window);
            FocusInspection.WaitForFocusWithin(target!).Should().BeTrue(
                $"「{dialog.Name}」を開いた直後のフォーカスは「{expectedName}」にあること（実際は {FocusInspection.DescribeFocused(target!)}）");
        }
    }
}
