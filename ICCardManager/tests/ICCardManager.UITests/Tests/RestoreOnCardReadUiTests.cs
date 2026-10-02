using System;
using System.Data.SQLite;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 交通系ICカード管理・職員管理の新規登録で、削除済みのカード・職員証を読み取ったときの復元の回帰テスト
    /// （Issue #2196。07_テスト設計書 UT-083 の手動確認「カード読み取りイベント経由の復元」を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 復元は <c>OnCardRead</c>（カードリーダーのイベント）から <c>Dispatcher</c> 経由で走る fire-and-forget
    /// （<c>HandleCardReadCoreAsync</c>）で、単体テストから完了を待てなかった。UI テストでは結果（確認の MessageBox と DB）を待てる。
    /// 両 ViewModel には削除済みの確認が 3 か所ずつある（メイン画面の未登録カード・保存・カードの読み取り）。本テストが
    /// 読み取りの経路を通っていることは、変異で確かめた（ほかの経路の確認を外しても緑のまま、読み取りの経路を外すと赤）。
    /// </para>
    /// <para>
    /// 読み取りはメイン画面の DEBUG パネル（<c>HybridCardReader.SimulateCardRead</c>）で起こす。ダイアログ側の
    /// 「[DEBUG] … 読み取りシミュレート」は毎回新しい IDm を作るので、削除済みのものを読ませられない。メイン画面はモーダルの間
    /// Win32 レベルで無効だが、UIA の Invoke は WPF のボタンを直接実行するので届き、読み取りのイベントはダイアログにも届く。
    /// DEBUG パネルを使うため Debug 起動のときだけ実行する。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class RestoreOnCardReadUiTests
    {
        private const string SkipReason =
            "Issue #2196: 読み取りは DEBUG パネル（DEBUG ビルド限定）で起こすため、Debug 起動のときだけ実行する。";

        private static readonly Target Card = new Target(
            RegressionSeedData.SeedWithDeletedVirtualTouchCard,
            TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName, TestConstants.CardNewRegistrationButton,
            TestConstants.DebugPanelIcCardButton, TestConstants.DeletedCardRestoreTitle,
            "SELECT is_deleted FROM ic_card WHERE card_idm = @id", ScreenshotSeedData.VirtualTouchCardIdm);

        private static readonly Target Staff = new Target(
            RegressionSeedData.SeedWithDeletedVirtualTouchStaff,
            TestConstants.OpenStaffManageButton, TestConstants.StaffManageDialogName, TestConstants.StaffNewRegistrationButton,
            TestConstants.DebugPanelStaffButton, TestConstants.DeletedStaffRestoreTitle,
            "SELECT is_deleted FROM staff WHERE staff_idm = @id", AppFixture.SeededStaffIdm);

        [SkippableFact]
        public void 交通系ICカード管理で削除済みのカードを読み取りはいを選ぶと復元されること() => AssertRestored(Card);

        [SkippableFact]
        public void 交通系ICカード管理で復元の確認にいいえと答えると復元されないこと() => AssertNotRestored(Card);

        [SkippableFact]
        public void 職員管理で削除済みの職員証を読み取りはいを選ぶと復元されること() => AssertRestored(Staff);

        [SkippableFact]
        public void 職員管理で復元の確認にいいえと答えると復元されないこと() => AssertNotRestored(Staff);

        private static void AssertRestored(Target target)
        {
            Skip.If(!IsDebug, SkipReason);
            using var fixture = AppFixture.LaunchWithSeed(target.Seed, AppFixture.SuppressDebugTestData);
            target.IsDeleted().Should().Be(1L, "前提: 読み取らせる対象は削除済みであること");

            ReadInNewRegistration(fixture, target, MessageBoxOperations.YesPrefix);

            var restored = Retry.WhileFalse(() => target.IsDeleted() == 0L, TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            restored.Should().BeTrue("復元の確認で「はい」を選んだら、復元されること（is_deleted が 0）");
            AssertNoLending();
        }

        private static void AssertNotRestored(Target target)
        {
            Skip.If(!IsDebug, SkipReason);
            using var fixture = AppFixture.LaunchWithSeed(target.Seed, AppFixture.SuppressDebugTestData);

            ReadInNewRegistration(fixture, target, MessageBoxOperations.NoPrefix);

            DialogLocator.IsOpen(fixture, fixture.MainWindow, target.DialogName)
                .Should().BeTrue("「いいえ」の後もダイアログは開いたままであること");
            target.IsDeleted().Should().Be(1L, "「いいえ」を選んだら、削除済みのままであること");
            AssertNoLending();
        }

        private static bool IsDebug => string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal);

        /// <summary>ダイアログで新規登録を始め、DEBUG パネルで削除済みのものを読み取らせ、復元の確認に答える。</summary>
        private static void ReadInNewRegistration(AppFixture fixture, Target target, string answer)
        {
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = page.ClickToolbarButtonAndWaitForDialog(target.OpenButton, target.DialogName);
            dialog.ClickButton(target.NewRegistrationButton);

            InvokeDebugPanelButton(page, target.DebugPanelButton);

            var confirmation = MessageBoxOperations.WaitFor(fixture, dialog.Window, target.RestoreTitle);
            MessageBoxOperations.Answer(confirmation, answer);
        }

        /// <summary>ダイアログを開いている間の読み取りが、メイン画面の貸出として処理されていないこと。</summary>
        private static void AssertNoLending()
        {
            DatabaseProbe.Count("SELECT COUNT(*) FROM ledger WHERE card_idm = @card AND is_lent_record = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(0L,
                "管理ダイアログを開いている間の読み取りは、メイン画面の貸出として記録されないこと");
        }

        /// <summary>復元の対象（交通系ICカード／職員）ごとの違い。</summary>
        private sealed class Target
        {
            private readonly string _isDeletedSql;
            private readonly string _id;

            public Target(Action<SQLiteConnection> seed, string openButton, string dialogName, string newRegistrationButton,
                string debugPanelButton, string restoreTitle, string isDeletedSql, string id)
            {
                Seed = seed;
                OpenButton = openButton;
                DialogName = dialogName;
                NewRegistrationButton = newRegistrationButton;
                DebugPanelButton = debugPanelButton;
                RestoreTitle = restoreTitle;
                _isDeletedSql = isDeletedSql;
                _id = id;
            }

            public Action<SQLiteConnection> Seed { get; }
            public string OpenButton { get; }
            public string DialogName { get; }
            public string NewRegistrationButton { get; }
            public string DebugPanelButton { get; }
            public string RestoreTitle { get; }

            public long IsDeleted() => DatabaseProbe.Count(_isDeletedSql, ("@id", _id));
        }
    }
}
