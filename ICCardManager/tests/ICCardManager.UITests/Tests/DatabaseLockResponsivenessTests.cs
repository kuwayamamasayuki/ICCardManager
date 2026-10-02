using System;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2202: DB がほかの接続に排他ロックされている間も、DB を読む操作の最中にメイン画面が応答し続けること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ViewModel が UI スレッドからリポジトリを <c>await</c> すると、DB の処理（SQLite のロック待ちを含む）が
    /// UI スレッドの上で同期的に走り、ロック待ち（busy_timeout と ADO 層の再試行）の間アプリ全体が固まっていた。
    /// #2197（<see cref="SettingsSaveResponsivenessTests"/>）は設定の保存だけを直した。#2202 で DbContext の入口
    /// （接続のリース・トランザクションの開始）で UI スレッドから移るようにしたので、ほかの画面の DB 操作も固まらない。
    /// </para>
    /// <para>
    /// 対象は履歴の月送り。交通系ICカード・職員の一覧はキャッシュを経由するため、ダイアログを開くだけでは
    /// DB に届かず検査にならない。月送りは期間の履歴を毎回 DB から読む（キャッシュしない）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class DatabaseLockResponsivenessTests
    {
        public DatabaseLockResponsivenessTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void DBがロックされている間も履歴の月送り中のメイン画面は応答しロックが外れると前の月が表示されること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);
            WaitForPeriod(page, PeriodLabel(DateTime.Today));

            using var dbLock = DatabaseExclusiveLock.Acquire();
            var previous = page.FindByNameWithRetry(TestConstants.HistoryPreviousMonthButton);
            previous.Should().NotBeNull($"履歴の期間選択に「{TestConstants.HistoryPreviousMonthButton}」ボタンがあること");
            ScreenshotHelper.RequireForeground(page.Window);
            previous!.Click();

            // 月送りの読み込みが DB のロックで待たされている間も、UI スレッドは UIA の問い合わせに応答する。
            // 問い合わせはロックを保持したまま行う（外してから問い合わせると、固まっていた実装でも応答が戻って緑になる）
            Exception? uiaFailure = null;
            var responded = Retry.WhileFalse(
                () =>
                {
                    try
                    {
                        // ツールバーのボタンは常に表示されている。見つかる＝UI スレッドが UIA の問い合わせを処理できた
                        return fixture.MainWindow.FindFirstDescendant(cf => cf.ByName(TestConstants.OpenSettingsButton)) != null;
                    }
                    catch (Exception ex)
                    {
                        // UI スレッドが止まっていると、UIA の問い合わせがタイムアウトの例外になる
                        uiaFailure = ex;
                        return false;
                    }
                },
                TimeSpan.FromSeconds(10)).Success;
            responded.Should().BeTrue(
                "DB がロックされている間も、履歴の月送りの読み込み中にメイン画面が応答すること" +
                (uiaFailure == null ? "。" : $"（UIA の問い合わせが失敗した: {uiaFailure.GetType().Name} — UI スレッドが止まっている）。"));

            // 対: ロックを外すと読み込みが終わり、前の月が表示される（待たされた読み込みが失われない）
            dbLock.Release();
            WaitForPeriod(page, PeriodLabel(DateTime.Today.AddMonths(-1)));
        }

        private static string PeriodLabel(DateTime month) =>
            month.ToString("yyyy年M月", CultureInfo.InvariantCulture);

        private static AutomationElement WaitForPeriod(MainWindowPage page, string label)
        {
            // busy_timeout（ローカル 5 秒）と ADO 層の再試行を待つ余裕を持たせる
            var element = Retry.WhileNull(
                () => page.Window.FindFirstDescendant(cf => cf.ByName(label)),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds + 30)).Result;
            element.Should().NotBeNull($"期間表示が「{label}」になること");
            return element!;
        }
    }
}
