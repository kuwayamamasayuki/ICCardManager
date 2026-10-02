using System;
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
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class SettingsSaveResponsivenessTests
    {
        [Fact]
        public void DBがロックされている間も保存中の設定ダイアログは応答しロックが外れると保存されて閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var settings = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName);

            using var dbLock = DatabaseExclusiveLock.Acquire();
            settings.ClickButton(TestConstants.SettingsSaveButton);

            // 保存が DB で待たされている間も UI スレッドは応答し、処理中オーバーレイが描画される
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
                .Should().NotBeNull("待たされた保存も最後まで実行され、設定が DB に書かれること");
        }
    }
}
