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
            var virtualTouch = FindButton(dialog, TestConstants.DebugVirtualTouchButtonName);
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
            var cancelButton = FindButton(dialog, TestConstants.StaffAuthCancelButtonName);
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
            //
            // Issue #2018: 反映を確認した時点の Name を retry の中で確定させる。
            // タイムアウト後はダイアログが自動的に閉じるため、retry の外で Name を読み直すと
            // 要素が既に無く ElementNotAvailableException になり得る（「反映されなかった」ではなく
            // 「読めなかった」で落ちるので、失敗の原因を取り違える）。
            var observed = Retry.WhileNull(
                () =>
                {
                    try
                    {
                        var name = statusText!.Name;
                        return name != null && name.Contains("タイムアウト") ? name : null;
                    }
                    catch (Exception)
                    {
                        // ダイアログが閉じて要素が消えた場合。次の試行までに retry が打ち切られる。
                        return null;
                    }
                },
                TimeSpan.FromSeconds(70)).Result;

            observed.Should().NotBeNull(
                "Issue #1509: タイムアウト経過後に StatusText にタイムアウトメッセージが反映されていない");
            observed.Should().Contain("タイムアウト");
        }

        [Fact]
        public void 認証成功時_StatusTextに成功メッセージが反映されてからダイアログが閉じること()
        {
            // LaunchWithSeededStaff で IDm "FFFF000000000001" の「テスト職員」を事前投入済み。
            // 仮想タッチボタンは "FFFF000000000001" を返すので、認証成功パスに入る。
            using var fixture = AppFixture.LaunchWithSeededStaff();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = TriggerStaffAuthDialog(page, fixture);

            var virtualTouch = FindButton(dialog, TestConstants.DebugVirtualTouchButtonName);
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

            // 700ms 後に「認証ダイアログだけが」自動クローズすることを確認。
            //
            // Issue #2018: 呼び出し元の StaffManageDialog は開いたままなので、
            // ModalWindows が空になることを期待してはならない（この経路は #1500 の追加当初から
            // 削除ボタンを掴めておらず、この後段のアサーションは一度も実行されていなかった）。
            Retry.WhileTrue(
                () => IsStaffAuthDialogOpen(fixture),
                TimeSpan.FromSeconds(3));

            IsStaffAuthDialogOpen(fixture).Should().BeFalse(
                "Issue #1509: 認証成功後 700ms で認証ダイアログが自動的に閉じるべき");
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
            SelectRow(firstRow!);

            // 3. 削除ボタンクリック → StaffAuthDialog 表示
            //
            // Issue #2018: ボタンの有効化は選択の反映（DataGrid.SelectedItem → SelectedStaff の
            // TwoWay バインディング → NotNullToBoolConverter）を待つ必要があるため、
            // 「見つかった」ではなく「有効になった」まで待つ。ここで待たずに Invoke すると
            // ElementNotEnabledException になり、原因（行が選択できていない）が読み取れない。
            var deleteButton = Retry.WhileNull(
                () =>
                {
                    var button = FindButton(staffManageDialog, TestConstants.StaffManageDeleteButtonName);
                    return button != null && button.IsEnabled ? button : null;
                },
                TimeSpan.FromSeconds(5)).Result;

            deleteButton.Should().NotBeNull(
                $"職員削除ボタン（AutomationProperties.Name=\"{TestConstants.StaffManageDeleteButtonName}\"）が" +
                "有効な状態で存在すべき。無効のままなら一覧の行が選択できていない" +
                "（IsEnabled は SelectedStaff の null 判定にバインドされている）。");
            deleteButton!.AsButton().Invoke();

            // 4. StaffAuthDialog の出現を待つ
            var authDialog = Retry.WhileNull(
                () => FindStaffAuthDialog(fixture),
                TimeSpan.FromSeconds(5)).Result;

            authDialog.Should().NotBeNull(
                $"StaffAuthDialog（{TestConstants.StaffAuthDialogName}）が表示されるべき");
            return authDialog!;
        }

        /// <summary>
        /// 一覧の行を選択する。座標を使わず UIA の <c>SelectionItem</c> パターンで行う。
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #2018: 物理クリック（<c>AutomationElement.Click()</c>）は要素の中心座標へ
        /// マウスを送るため、testhost が DPI 非対応だと 150% 表示の環境で論理座標と物理座標が
        /// 食い違い、<b>例外も出さずに別の場所を押す</b>（行は選択されないまま先へ進み、
        /// 次の「削除ボタンを押す」で ElementNotEnabledException になる）。
        /// </para>
        /// <para>
        /// この経路は UIA ツリーの構造を検証するテストであり、マウス入力そのものは検証対象ではない。
        /// <c>SelectionItemPattern.Select()</c> は座標を介さないので、DPI・ウィンドウ位置・
        /// 前面かどうかに依存しない。DataGrid は <c>SelectionUnit</c> 既定（FullRow）・
        /// <c>SelectionMode="Single"</c> なので、行（DataItem）がこのパターンを持つ。
        /// パターンが無い実装へ変わった場合に備えてクリックへフォールバックする。
        /// </para>
        /// </remarks>
        private static void SelectRow(AutomationElement row)
        {
            var selectionItem = row.Patterns.SelectionItem.PatternOrDefault;
            if (selectionItem != null)
            {
                selectionItem.Select();
                return;
            }

            row.Focus();
            row.Click();
        }

        /// <summary>
        /// 指定スコープ配下から、<paramref name="automationName"/> を持つ<b>ボタン</b>を探す。
        /// </summary>
        /// <remarks>
        /// Issue #2018: <c>ByName</c> だけで探すと、ボタンの内側にある <c>Content</c> のテキスト要素
        /// （UIA では Text）に一致することがある。Text は Invoke パターンを持たないため
        /// <c>AsButton().Invoke()</c> が <c>PatternNotSupportedException</c> になり、
        /// 「名前が違う」という本当の原因が分かりにくい失敗になる。
        /// ControlType を And で加え、名前の偶然の一致で別種の要素を掴まないようにする。
        /// </remarks>
        private static AutomationElement? FindButton(AutomationElement scope, string automationName)
        {
            return scope.FindFirstDescendant(
                cf => cf.ByControlType(ControlType.Button).And(cf.ByName(automationName)));
        }

        /// <summary>
        /// 現在開いている StaffAuthDialog を返す（開いていなければ null）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// StaffAuthDialog は <c>Owner = Application.Current.MainWindow</c> で表示されるため、
        /// 呼び出し元が StaffManageDialog でもメインウィンドウの ModalWindows に現れる。
        /// </para>
        /// <para>
        /// Issue #2018: <c>ModalWindows</c> の列挙と <c>Name</c> の参照は、まさに閉じかけの
        /// ウィンドウに対して <c>ElementNotAvailableException</c> を投げ得る。FlaUI の
        /// <c>Retry</c> は既定で例外を握りつぶさないため、ここで受けて「開いていない」へ倒す。
        /// 「閉じるのを待つ」用途では、要素が消えていることは待っている状態そのもの。
        /// </para>
        /// </remarks>
        private static Window? FindStaffAuthDialog(AppFixture fixture)
        {
            try
            {
                return fixture.MainWindow.ModalWindows
                    .FirstOrDefault(w => w.Name == TestConstants.StaffAuthDialogName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsStaffAuthDialogOpen(AppFixture fixture)
            => FindStaffAuthDialog(fixture) != null;
    }
}
