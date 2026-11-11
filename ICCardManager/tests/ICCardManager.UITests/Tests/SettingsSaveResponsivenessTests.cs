using System;
using System.Linq;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 設定の保存が DB のロックで待たされている間も、アプリが応答し続けることの回帰テスト（Issue #2197）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 故障: 設定の保存（<c>SettingsRepository.SaveAppSettingsAsync</c>）は名前こそ非同期だが、内部の
    /// <c>BeginTransactionAsync</c> の最初の <c>await</c>（セマフォの取得）が同期的に完了するため、<c>BEGIN IMMEDIATE</c> と
    /// 各 <c>INSERT</c> が呼び出し元のスレッド＝UI スレッドで実行されていた。他の接続（共有モードの他 PC 等）が書き込み中だと、
    /// busy_timeout（共有モード 15 秒）と ADO 層の再試行の間、アプリ全体が固まり、処理中オーバーレイも描画されなかった。
    /// </para>
    /// <para>
    /// テストから DB に排他ロックを掛けて保存を待たせ、その間にダイアログへ UIA で問い合わせる。UI スレッドが止まっていると
    /// UIA の問い合わせ自体が <c>COMException</c>（タイムアウト）になる（#2196 で実測した症状そのもの）。
    /// 対: ロックを外すと保存が終わり、ダイアログが閉じ、値が DB に入る（待たせた保存が失われない）。
    /// </para>
    /// <para>
    /// Issue #2388（レビュー 4 回目）: 保存は変えた項目のキーだけを書き、何も変えなければ DB へ何も書かない。以前の本テストは何も変えずに
    /// 保存していたため、#2388 以降は保存そのものはロックに当たらずに返り、待たされていたのは保存後の部署種別の読み直しだった
    /// （<c>SettingsViewModel.SaveCoreAsync</c> の <c>Task.Run</c> を外す #2197 の退行を入れても緑のまま）。そこで保存の前に
    /// 残額警告の値を変え、ロックで待たされる相手を<b>保存の書き込み</b>にし、ロックが外れた後に DB の値が変えた値になることを表明する
    /// （<c>SettingsPersistenceTests</c> と同じ形）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class SettingsSaveResponsivenessTests
    {
        /// <summary>残額警告しきい値の既定値（10,000 円）と異なる値（既定値のままだと保存が DB へ何も書かず、ロックに当たらない）</summary>
        private const string NewWarningBalance = "7000";

        [Fact]
        public void DBがロックされている間も保存中の設定ダイアログは応答しロックが外れると保存されて閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var settings = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName);

            // Issue #2305: 設定画面は開くたびに DB から読み込み、読み込みが終わるまで「保存」を押せない。
            // 読み込みの前にロックを取ると読み込みが待たされ、保存の待ちではなく読み込みの待ちを見てしまうので、
            // 保存ボタンが有効になる（＝読み込みが終わる）まで待ってからロックを取る
            settings.WaitUntilEnabled(TestConstants.SettingsSaveButton).Should().BeTrue(
                $"前提: 設定の読み込みが終わり「{TestConstants.SettingsSaveButton}」ボタンが有効になること");

            // Issue #2388: 値を変えて保存する（何も変えない保存は DB へ何も書かず、ロックで待たされない）。
            // 入力欄の束縛は UpdateSourceTrigger=PropertyChanged なので、UIA の Value パターンで入れれば画面の値に届く
            var input = settings.Window.FindFirstDescendant(cf => cf.ByName(TestConstants.SettingsWarningBalanceInput)
                .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)));
            input.Should().NotBeNull($"設定ダイアログに「{TestConstants.SettingsWarningBalanceInput}」の入力欄があること");
            Digits(input!.Patterns.Value.Pattern.Value.Value).Should().NotBe(
                NewWarningBalance, "前提: 入れる値は現在の値と異なること（同じだと保存が DB へ何も書かない）");
            input.Patterns.Value.Pattern.SetValue(NewWarningBalance);

            using var dbLock = DatabaseExclusiveLock.Acquire();
            settings.ClickButton(TestConstants.SettingsSaveButton);

            // 保存の書き込み（変えた残額警告）が DB のロックで待たされている間も UI スレッドは応答し、処理中オーバーレイ
            // 「保存中...」が描画される
            Exception? uiaFailure = null;
            var busyShown = Retry.WhileFalse(
                () =>
                {
                    try
                    {
                        return settings.FindByName(TestConstants.SettingsSavingBusyMessage) != null;
                    }
                    catch (Exception ex)
                    {
                        // UI スレッドが止まっていると、UIA の問い合わせがタイムアウトの例外になる
                        uiaFailure = ex;
                        return false;
                    }
                },
                TimeSpan.FromSeconds(10)).Success;
            busyShown.Should().BeTrue(
                $"DB がロックされている間も、保存中の設定ダイアログは応答し「{TestConstants.SettingsSavingBusyMessage}」が表示されること" +
                (uiaFailure == null ? "。" : $"（UIA の問い合わせが失敗した: {uiaFailure.GetType().Name} — UI スレッドが止まっている）。"));

            // 対: ロックを外すと保存が終わってダイアログが閉じ、値が DB に入る
            dbLock.Release();
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.SettingsDialogName)
                .Should().BeTrue($"ロックが外れたら保存が終わり、設定ダイアログが閉じること。開いていたウィンドウ: {DialogLocator.DescribeOpenWindows(fixture)}");
            DatabaseProbe.Scalar("SELECT value FROM settings WHERE key = 'warning_balance'")
                .Should().Be(NewWarningBalance, "待たされた保存も最後まで実行され、変えた残額警告が DB に書かれること");
        }

        /// <summary>入力欄の値の数字だけを取り出す（桁区切りの表示に左右されない）。</summary>
        private static string Digits(string? value) =>
            new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}
