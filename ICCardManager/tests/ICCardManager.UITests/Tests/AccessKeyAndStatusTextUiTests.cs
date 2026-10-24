using System;
using System.Data.SQLite;
using System.Globalization;
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
    /// Issue #2308: ステータスバーの状態表示と、設定ダイアログのアクセスキーの実機での回帰テスト。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 静的検査（<c>MainWindowStatusBarStateTextTests</c> / <c>AccessKeyConventionTests</c>）は XAML の<b>宣言</b>を固定する。
    /// 本テストは、実際に画面へ出る文字列（UIA の Name）と、Alt+キー 1 回でフォーカスが目的の欄へ移ることを確かめる。
    /// </para>
    /// <para>
    /// 設定の Alt+D は「同行者数入力を自動的に閉じるまでの秒数」と「データベース保存先フォルダ」が重複していた。
    /// WPF は重複したキーを押すたびにツリー順でフォーカスを循環させるので、<b>是正前は 1 回目の Alt+D で秒数欄へ移っていた</b>
    /// （秒数欄が先にある）。ただし秒数欄は同行者数入力のスキップが有効（Issue #2178 以降の既定）だと無効になり、
    /// 無効な要素はアクセスキーの対象から外れるので、スキップしない設定を投入して衝突が起きる状態で確かめる。キーの組み合わせは同時押し（<see cref="Keyboard.TypeSimultaneously"/>）で送り、押したままにしない。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class AccessKeyAndStatusTextUiTests
    {
        public AccessKeyAndStatusTextUiTests()
        {
            // 履歴を開く操作（OpenCardHistory）は行を実際にクリックするため、座標系をそろえる（DialogInitialFocusUiTests と同じ）
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void ステータスバーの状態表示が内部名ではなく日本語の状態名であること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // StatusBarItem は UIA ツリーに公開されないため、内部の TextBlock を表示文字列の前方一致で探す
            var statusText = Retry.WhileNull(
                () => page.FindByNameStartsWith(TestConstants.AppStateStatusTextPrefix),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            statusText.Should().NotBeNull($"ステータスバーに \"{TestConstants.AppStateStatusTextPrefix}\" で始まる表示があること");

            var name = statusText!.Name;
            name.Should().Contain("待機中", "起動直後は待機中（次アクションガイドと同じ状態名）であること");
            name.Should().NotContain("WaitingFor", "状態の内部名（AppState）を表示しないこと");
        }

        [Theory]
        [InlineData(VirtualKeyShort.KEY_D, TestConstants.SettingsDatabasePathInput)]
        [InlineData(VirtualKeyShort.KEY_I, TestConstants.SettingsCompanionCountTimeoutInput)]
        public void 設定ダイアログでAltキー1回で目的の入力欄へフォーカスが移ること(VirtualKeyShort key, string expectedName)
        {
            // 秒数欄は同行者数入力のスキップが有効だと操作不可になり、アクセスキーが働かない（重複も起きない）。
            // 衝突が実際に起きる状態（スキップしない）で確かめる
            using var fixture = AppFixture.LaunchWithSeed(
                ScreenshotSeedData.SeedWithCompanionCountInputEnabled, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = page.ClickToolbarButtonAndWaitForDialog(TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName);

            var target = dialog.Window.FindFirstDescendant(cf =>
                cf.ByName(expectedName).And(cf.ByControlType(ControlType.Edit)));
            target.Should().NotBeNull($"設定ダイアログに「{expectedName}」の入力欄があること");

            // キーの前の前提: 開いた直後の初期フォーカス（UT-054）が来てから押す
            var initialFocus = dialog.Window.FindFirstDescendant(cf => cf.ByName(TestConstants.SettingsToastPositionComboBox));
            PressAltAndAssert(dialog.Window, initialFocus, key,
                () => FocusInspection.HasFocusWithin(target!),
                () => $"「{expectedName}」へフォーカスが移ること（実際は {FocusInspection.DescribeFocused(target!)}）");
        }

        /// <summary>
        /// 行編集で導入行の残高の訂正提案が出ているとき、Alt+R 1 回で受入と残額が逆算値になること。
        /// </summary>
        /// <remarks>
        /// 是正前は「逆算した金額を適用(_G)」が「同行者数(_G)」と重複しており、Alt+G はフォーカスを移すだけでボタンを実行しなかった。
        /// 投入データは通常カードの新規購入行を受入・残額とも 12,000 円に書き換えたもの。直後の利用行（残額 9,740 円・払出 260 円）は
        /// カード由来で正しいとみなせるので、導入行の逆算値は 10,000 円になる（<c>DetectInitialBalanceCorrection</c> の形）。
        /// 行の修正は職員証認証を要するため Debug 起動のときだけ実行する。
        /// </remarks>
        [SkippableFact]
        public void 行編集で訂正提案が出ているときAltRキー1回で受入と残額が逆算値になること()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal),
                "Issue #2308: 行の修正は職員証認証を要し、仮想タッチ（DEBUG ビルド限定）でしか通せないため、Debug 起動のときだけ実行する。");
            using var fixture = AppFixture.LaunchWithSeed(SeedWithWrongInitialBalance, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);
            page.InvokeHistoryRowButton(InitialRowSummary, TestConstants.HistoryRowEditButton);
            PassStaffAuthentication(fixture, fixture.MainWindow);
            var edit = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName));

            var cf = edit.Window.ConditionFactory;
            Retry.WhileNull(
                    () => edit.Window.FindFirstDescendant(
                        cf.ByName(TestConstants.LedgerRowEditApplyInitialBalanceButton).And(cf.ByControlType(ControlType.Button))),
                    TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result
                .Should().NotBeNull("前提: 導入行の残高の訂正提案（逆算した金額を適用）が表示されていること");

            var income = FindEdit(edit, TestConstants.LedgerRowEditIncomeInput);
            var balance = FindEdit(edit, TestConstants.LedgerRowEditBalanceInput);
            Amount(income).Should().Be(WrongInitialAmount, "前提: 受入は書き換えた誤りの値で開くこと");
            Amount(balance).Should().Be(WrongInitialAmount, "前提: 残額は書き換えた誤りの値で開くこと");

            // キーの前の前提: 開いた直後の初期フォーカス（修正モードは出納日付。UT-054）が来てから押す
            var initialFocus = edit.Window.FindFirstDescendant(cf.ByName(TestConstants.LedgerRowEditDateInput));
            PressAltAndAssert(edit.Window, initialFocus, VirtualKeyShort.KEY_R,
                () => Amount(income) == CorrectInitialAmount && Amount(balance) == CorrectInitialAmount,
                () => $"受入と残額が逆算値 {CorrectInitialAmount} 円になること（実際は 受入 {income.Patterns.Value.Pattern.Value.Value}・" +
                      $"残額 {balance.Patterns.Value.Pattern.Value.Value}、フォーカスは {FocusInspection.DescribeFocused(income)}）");
        }

        /// <summary>
        /// 初期フォーカスが来るのを待って Alt+キーを 1 回だけ送り、期待した結果になったかを判定する。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>キーは再試行しない</b>。重複したアクセスキー（本 Issue の欠陥）は押すたびにフォーカスを循環させるので、
        /// 押し直すと 2 回目で目的の要素に届き、欠陥を見逃す。
        /// </para>
        /// <para>
        /// 失敗したときは、キー送信の直前・直後・判定時の前面ウィンドウを見て、アプリ以外（Windows の通知など）へ移っていたら
        /// 「外部にフォーカスを奪われた（判定不能）」と分かる別の失敗メッセージにする。キーの重複では前面のウィンドウは変わらないので、
        /// この切り分けで欠陥を見逃すことはない。
        /// </para>
        /// </remarks>
        private static void PressAltAndAssert(
            Window dialog, AutomationElement? initialFocus, VirtualKeyShort key, Func<bool> succeeded, Func<string> expectation)
        {
            initialFocus.Should().NotBeNull("前提: 初期フォーカスの要素がダイアログにあること");
            ScreenshotHelper.RequireForeground(dialog);
            FocusInspection.WaitForFocusWithin(initialFocus!).Should().BeTrue(
                $"前提: キーを押す前に初期フォーカスが「{initialFocus!.Name}」にあること（実際は {FocusInspection.DescribeFocused(initialFocus)}）");

            var appProcessId = ScreenshotHelper.ProcessIdOf(NativeWindows.HandleOf(dialog));
            var before = NativeWindows.Foreground;
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, key);
            var after = NativeWindows.Foreground;

            var ok = Retry.WhileFalse(succeeded, TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            if (ok)
            {
                return;
            }

            var atFailure = NativeWindows.Foreground;
            var label = KeyLabel(key);
            var stolen = new[] { before, after, atFailure }.Any(h => ScreenshotHelper.ProcessIdOf(h) != appProcessId);
            var foregrounds =
                $"前面ウィンドウ: 送信直前「{ScreenshotHelper.DescribeWindow(before)}」・送信直後「{ScreenshotHelper.DescribeWindow(after)}」・" +
                $"判定時「{ScreenshotHelper.DescribeWindow(atFailure)}」";
            if (stolen)
            {
                throw new InvalidOperationException(
                    $"Alt+{label} の判定不能: フォーカスをアプリ外のウィンドウに奪われました（キーの重複ではありません）。{foregrounds}。" +
                    "通知などを閉じてから実行し直してください。");
            }

            ok.Should().BeTrue($"Alt+{label} 1 回で{expectation()}。{foregrounds}");
        }

        /// <summary>失敗メッセージ用のキー表記（<c>KEY_D</c> → <c>D</c>）。</summary>
        private static string KeyLabel(VirtualKeyShort key)
        {
            var name = key.ToString();
            return name.StartsWith("KEY_", StringComparison.Ordinal) ? name.Substring(4) : name;
        }

        private const string InitialRowSummary = "新規購入";
        private const int WrongInitialAmount = 12000;

        /// <summary>直後の利用行（残額 9,740 円・払出 260 円）から逆算した導入時の残高。</summary>
        private const int CorrectInitialAmount = 10000;

        /// <summary>
        /// 通常の投入データの、通常カードの新規購入行だけを受入・残額とも誤った値にする（直後の行との残高チェーンが 1 か所だけ切れる）。
        /// </summary>
        private static void SeedWithWrongInitialBalance(SQLiteConnection conn)
        {
            ScreenshotSeedData.Seed(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ledger SET income = @amount, balance = @amount WHERE card_idm = @card AND summary = @summary";
            cmd.Parameters.AddWithValue("@amount", WrongInitialAmount);
            cmd.Parameters.AddWithValue("@card", ScreenshotSeedData.NormalCardIdm);
            cmd.Parameters.AddWithValue("@summary", InitialRowSummary);
            cmd.ExecuteNonQuery().Should().Be(1, "前提: 通常カードの新規購入行が 1 行あること");
        }

        private static AutomationElement FindEdit(DialogPageBase edit, string name)
        {
            var cf = edit.Window.ConditionFactory;
            return Retry.WhileNull(
                    () => edit.Window.FindFirstDescendant(cf.ByName(name).And(cf.ByControlType(ControlType.Edit))),
                    TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result
                ?? throw new InvalidOperationException($"入力欄が見つかりません: \"{name}\"");
        }

        /// <summary>入力欄の金額（桁区切りを除いて整数として読む。読めなければ null）。</summary>
        private static int? Amount(AutomationElement input)
        {
            var text = input.Patterns.Value.Pattern.Value.Value?.Replace(",", string.Empty).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
    }
}
