using System;
using System.Linq;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #1509: StaffAuthDialog の StatusText が UIA tree から発見可能であり、
    /// 認証失敗・タイムアウト・成功の各シナリオで Text 更新が UIA から観察できることを検証する。
    /// </summary>
    /// <remarks>
    /// PR #1500 で StatusText に LiveSetting="Assertive" を付与したが、
    /// StatusBorder の Visibility="Collapsed" 起点では AutomationTree から除外され
    /// スクリーンリーダーが沈黙していた。本テストは Visibility Collapsed の再混入による
    /// 構造的回帰を確実に検出する。
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class StaffAuthDialogLiveRegionTests
    {
        [Fact]
        public void 認証ダイアログ表示直後_StatusTextがUIAtreeから発見可能でLiveSettingがAssertiveであること()
        {
            using var fixture = AppFixture.LaunchWithSeededStaff();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = TriggerStaffAuthDialog(page, fixture);

            // 仮想タッチボタンが DEBUG ビルドで表示されることを確認（タッチ操作はしない；
            // 本テストはダイアログ表示直後の構造検証に絞る。
            // 失敗パス・成功パスのメッセージ内容検証は別テストで実施）
            var virtualTouch = dialog.FindFirstDescendant(
                cf => cf.ByName(TestConstants.DebugVirtualTouchButtonName));
            virtualTouch.Should().NotBeNull(
                "DEBUG ビルドでは仮想タッチボタンが表示されるべき。Release ビルドではこのアサーションでスキップ判定が必要。");

            // StatusText 要素が UIA tree から発見できることを検証（StatusBorder 常時可視のため
            // タッチ前から発見可能であるべき）
            var statusText = Retry.WhileNull(
                () => dialog.FindFirstDescendant(
                    cf => cf.ByHelpText(TestConstants.StaffAuthStatusHelpText)),
                TimeSpan.FromSeconds(3)).Result;

            statusText.Should().NotBeNull(
                "Issue #1509: StatusText が UIA tree から発見できない。" +
                "StatusBorder.Visibility=Collapsed で AutomationTree から除外されている可能性。");

            // LiveSetting=Assertive が UIA から観察できること
            var liveSetting = statusText!.Properties.LiveSetting.ValueOrDefault;
            liveSetting.Should().Be(LiveSetting.Assertive,
                "Issue #1509: StatusText の LiveSetting が Assertive でない。");

            // 後片付け: キャンセルでダイアログを閉じる
            var cancelButton = dialog.FindFirstDescendant(
                cf => cf.ByName(TestConstants.StaffAuthCancelButtonName));
            cancelButton?.AsButton().Invoke();
        }

        [Fact]
        public void タイムアウト時_StatusTextがUIAtreeから発見可能でタイムアウトメッセージが反映されること()
        {
            using var fixture = AppFixture.LaunchWithSeededStaff();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = TriggerStaffAuthDialog(page, fixture);

            // StatusText を即座に取得（StatusBorder は常時可視のため UIA tree で発見可能）
            var statusText = dialog.FindFirstDescendant(
                cf => cf.ByHelpText(TestConstants.StaffAuthStatusHelpText));

            statusText.Should().NotBeNull(
                "Issue #1509: ダイアログ表示直後から StatusText が UIA tree から発見できるべき");

            // タイムアウト経過後にメッセージが反映されるのを単一の長めの retry で待つ
            // （デフォルト StaffCardTimeoutSeconds=60s + クローズ遅延 1s + 余裕 = 70s）
            Retry.WhileFalse(
                () => statusText!.Name.Contains("タイムアウト"),
                TimeSpan.FromSeconds(70));

            statusText!.Name.Should().Contain("タイムアウト",
                "Issue #1509: タイムアウト経過後に StatusText にタイムアウトメッセージが反映されていない");
        }

        [Fact]
        public void 認証成功時_StatusTextに成功メッセージが反映されてからダイアログが閉じること()
        {
            // LaunchWithSeededStaff で IDm "FFFF000000000001" の「テスト職員」を事前投入済み。
            // 仮想タッチボタンは "FFFF000000000001" を返すので、認証成功パスに入る。
            using var fixture = AppFixture.LaunchWithSeededStaff();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = TriggerStaffAuthDialog(page, fixture);

            var virtualTouch = dialog.FindFirstDescendant(
                cf => cf.ByName(TestConstants.DebugVirtualTouchButtonName));
            virtualTouch.Should().NotBeNull(
                "DEBUG ビルドでは仮想タッチボタンが表示されるべき。Release ビルドの場合は本テストをスキップ。");
            virtualTouch!.AsButton().Invoke();

            // 成功時は 700ms 後に閉じるため、Status 反映タイミングを早めに捕捉する
            var statusText = Retry.WhileNull(
                () => dialog.FindFirstDescendant(
                    cf => cf.ByHelpText(TestConstants.StaffAuthStatusHelpText)),
                TimeSpan.FromMilliseconds(500)).Result;

            statusText.Should().NotBeNull(
                "Issue #1509: 認証成功時にも StatusText が UIA tree から発見できるべき");
            statusText!.Name.Should().Contain("認証に成功",
                "Issue #1509: 認証成功メッセージが StatusText に反映されているべき。" +
                $"実際の Name: '{statusText.Name}'");

            // 700ms 後にダイアログが自動クローズすることを確認
            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(3));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "Issue #1509: 認証成功後 700ms でダイアログが自動的に閉じるべき");
        }

        /// <summary>
        /// StaffAuthDialog をトリガーするヘルパ。
        /// 職員管理 → 既存職員選択 → 削除ボタン経由で StaffAuthDialog を開く。
        /// </summary>
        /// <remarks>
        /// 前提: AppFixture.LaunchWithSeededStaff() で「テスト職員」が投入済み。
        /// 経路: ツールバー「職員管理」 → StaffManageDialog 表示 → 職員選択 → 削除ボタン → StaffAuthDialog 表示
        /// </remarks>
        private static Window TriggerStaffAuthDialog(MainWindowPage page, AppFixture fixture)
        {
            // 1. 職員管理ダイアログを開く
            page.ClickButton(TestConstants.OpenStaffManageButton);
            var staffManageDialog = page.WaitForDialog(TestConstants.StaffManageDialogName);

            // 2. 一覧の最初の職員行を選択（テスト職員が 1 件投入されている前提）
            var dataGrid = staffManageDialog.FindFirstDescendant(
                cf => cf.ByControlType(ControlType.DataGrid));
            dataGrid.Should().NotBeNull("StaffManageDialog に DataGrid が存在すべき");
            var firstRow = dataGrid!.FindFirstDescendant(
                cf => cf.ByControlType(ControlType.DataItem));
            firstRow.Should().NotBeNull("テスト職員が一覧に表示されているべき");
            firstRow!.Click();

            // 3. 削除ボタンクリック → StaffAuthDialog 表示
            var deleteButton = staffManageDialog.FindFirstDescendant(
                cf => cf.ByName(TestConstants.StaffManageDeleteButtonName));
            deleteButton.Should().NotBeNull("削除ボタンが存在すべき");
            deleteButton!.AsButton().Invoke();

            // 4. StaffAuthDialog の出現を待つ
            var authDialog = Retry.WhileNull(
                () => fixture.MainWindow.ModalWindows
                    .FirstOrDefault(w => w.Name == TestConstants.StaffAuthDialogName),
                TimeSpan.FromSeconds(5)).Result;

            authDialog.Should().NotBeNull(
                $"StaffAuthDialog（{TestConstants.StaffAuthDialogName}）が表示されるべき");
            return authDialog!.AsWindow();
        }
    }
}
