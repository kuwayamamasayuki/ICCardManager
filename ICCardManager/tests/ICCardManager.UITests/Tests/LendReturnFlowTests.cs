using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 貸出・返却フローの回帰テスト（Issue #2190。07_テスト設計書 ST-007 の M1・M2 を手動確認から置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DEBUG ビルドの仮想タッチで操作し、画面（トースト・次の操作ガイド・ダイアログ）と
    /// <b>DB に記録された内容</b>の両方を表明する。画面だけを見ると「トーストは出たが記録されていない」を見逃す。
    /// </para>
    /// <para>
    /// 経路の違い: 貸出（M1）は DEBUG パネルの「職員証」「交通系ICカード」で行い、本物のカード読み取りイベントと
    /// 同じ経路（<c>HybridCardReader.SimulateCardRead</c> → 状態遷移 → <c>LendAsync</c>）を通る。返却（M2）は
    /// 同じ経路では成立しない（履歴の読み取りが実カードリーダーへ委譲されて失敗する）ため、仮想タッチダイアログで行う。
    /// こちらは <c>LendingService.ReturnAsync</c> を直接呼び、返却後の処理（トースト・バス停名入力）は通常の返却と同じものを通る。
    /// 状態遷移からの返却の分岐は単体テスト（<c>MainViewModelIntegrationTests</c>）が担う。
    /// </para>
    /// <para>
    /// 起動時のテストデータ自動登録は止める（<see cref="AppFixture.SuppressDebugTestData"/>）。止めないと
    /// 仮想タッチ用のカードにデバッグ用の履歴が足され、DB の値が投入データだけで決まらなくなる。
    /// 音（ピッ／ピピッ）は表明できないため手動確認に残る。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class LendReturnFlowTests
    {
        private const string SkipReason =
            "Issue #2190: 仮想タッチは DEBUG ビルドにしか無いため、Debug 起動（ICCARDMANAGER_UITEST_CONFIGURATION 未設定または Debug）のときだけ実行する。";

        [SkippableFact]
        public void M1_職員証と交通系ICカードのタッチで貸出が記録され職員証タッチ待ちへ戻ること()
        {
            SkipUnlessDebug();

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedForVirtualTouch, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 前提: 未貸出で、職員証タッチ待ち
            LentStatusOf(ScreenshotSeedData.VirtualTouchCardIdm).Should().Be(0L, "前提: 仮想タッチ用カードは未貸出で投入されていること");
            WaitForNextAction(page, TestConstants.NextActionWaitingForStaffCard);

            // 1. 職員証タッチ → 職員の認識と、交通系ICカードタッチ待ち（操作者名入り）
            InvokeDebugPanelButton(page, TestConstants.DebugPanelStaffButton);
            WaitForToastWithTitle(fixture, $"{ScreenshotSeedData.PrimaryStaffName} さん");
            WaitForNextAction(page, ScreenshotSeedData.PrimaryStaffName + TestConstants.NextActionWaitingForIcCardSuffix);
            WaitForToastGone(fixture);

            // 2. 交通系ICカードタッチ → 貸出トースト（カード名入り）、職員証タッチ待ちへ戻る
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            var toast = WaitForToastWithTitle(fixture, TestConstants.LendToastTitle);
            ReadToastText(toast, TestConstants.ToastMessageHelpText).Should().Be(
                ScreenshotSeedData.VirtualTouchCardDisplayName, "貸出トーストの本文は貸し出したカードの「種別 管理番号」であること");
            WaitForNextAction(page, TestConstants.NextActionWaitingForStaffCard);

            // 3. 記録: カードが貸出中になり、貸出中レコードが「職員証をタッチした職員」の名義で 1 件だけある
            LentStatusOf(ScreenshotSeedData.VirtualTouchCardIdm).Should().Be(1L, "ic_card.is_lent が 1 になること");
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM ledger WHERE card_idm = @card AND is_lent_record = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(1L, "貸出中レコードがちょうど 1 件できること");
            DatabaseProbe.Scalar(
                "SELECT lender_idm FROM ledger WHERE card_idm = @card AND is_lent_record = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(AppFixture.SeededStaffIdm,
                "貸出者は職員証をタッチした職員であること");
            DatabaseProbe.Scalar(
                "SELECT staff_name FROM ledger WHERE card_idm = @card AND is_lent_record = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(ScreenshotSeedData.PrimaryStaffName);
        }

        [SkippableFact]
        public void M2_返却でトーストとバス停名入力が出て入力したバス停名が台帳に記録されること()
        {
            SkipUnlessDebug();

            // バス停名入力を出す（同行者数入力はスキップ）
            using var fixture = AppFixture.LaunchWithSeed(
                conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false),
                AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 1. 仮想タッチで返却（未貸出なので貸出→返却。履歴 1 件・駅名なし＝バス利用）
            ExecuteVirtualTouchWithOneEntry(page);
            WaitForToastWithTitle(fixture, TestConstants.ReturnToastTitle);

            // 2. バス利用の明細があるので、返却後にバス停名入力ダイアログが開く
            var dialog = new DialogPageBase(WaitForDialogLong(page, TestConstants.BusStopInputDialogName));

            // 返却そのものは、バス停名を入れる前に確定している（記録済み・貸出中レコードは削除）
            LentStatusOf(ScreenshotSeedData.VirtualTouchCardIdm).Should().Be(0L, "返却で ic_card.is_lent が 0 に戻ること");
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM ledger WHERE card_idm = @card AND is_lent_record = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(0L, "返却で貸出中レコードが削除されること");

            // 3. バス停名を入れて保存 → ダイアログが閉じ、明細と摘要に反映される
            const string busStops = "天神～博多駅前";
            var input = dialog.FindByNameWithRetry(TestConstants.BusStopNameInput);
            input.Should().NotBeNull($"バス停名入力ダイアログに入力欄（\"{TestConstants.BusStopNameInput}\"）があること");
            input!.Patterns.Value.Pattern.SetValue(busStops);
            dialog.ClickButton(TestConstants.BusStopSaveButton);

            var closed = Retry.WhileTrue(
                () => IsOpen(page, TestConstants.BusStopInputDialogName),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            closed.Should().BeTrue("保存でバス停名入力ダイアログが閉じること");

            DatabaseProbe.Scalar(
                "SELECT d.bus_stops FROM ledger_detail d JOIN ledger l ON l.id = d.ledger_id " +
                "WHERE l.card_idm = @card AND d.is_bus = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be(busStops,
                "入力したバス停名がバス利用の明細に保存されること");
            DatabaseProbe.Scalar(
                "SELECT l.summary FROM ledger l JOIN ledger_detail d ON l.id = d.ledger_id " +
                "WHERE l.card_idm = @card AND d.is_bus = 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)).Should().Be($"バス（{busStops}）",
                "摘要がバス停名で作り直されること（入力前は「バス（★）」）");
        }

        // ── ヘルパー ─────────────────────────────────

        private static void SkipUnlessDebug() =>
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);

        private static long LentStatusOf(string cardIdm) =>
            DatabaseProbe.Count("SELECT is_lent FROM ic_card WHERE card_idm = @card", ("@card", cardIdm));

        /// <summary>
        /// 「次の操作ガイド」が指定の文言になるのを待つ。要素の Name は <c>NextActionMessage</c> へバインドされている。
        /// </summary>
        private static void WaitForNextAction(MainWindowPage page, string expected)
        {
            var found = Retry.WhileNull(
                () => page.Window.FindFirstDescendant(cf => cf.ByName(expected)),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            found.Should().NotBeNull($"次の操作ガイドが「{expected}」になること");
        }

        private static bool IsOpen(MainWindowPage page, string dialogName)
        {
            try
            {
                foreach (var w in page.Window.ModalWindows)
                {
                    if (w.Name == dialogName)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                // 閉じる途中で要素が無効になった
                return false;
            }
        }
    }
}
