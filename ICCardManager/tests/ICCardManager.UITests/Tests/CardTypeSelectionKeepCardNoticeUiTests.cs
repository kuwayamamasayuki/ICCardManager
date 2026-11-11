using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 未登録のカードをタッチしたときのカード種別選択ダイアログで、「登録が完了するまでカードを離さない」注意が
    /// 事前の読み取りに失敗したときだけ出ることの回帰テスト（Issue #2385）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// メイン画面は種別選択の前にカードの残高・利用履歴を読み取る（Issue #482 / #596）。読み取れていれば交通系ICカードの
    /// 登録時にカードを読み直さないのでカードを離してよく、読み取れなければ登録時に読み直すので離さないよう求める。
    /// 判定は単体テスト（<c>CardRegistrationPreReadTests</c>）が固定し、ここではメイン画面からダイアログへの受け渡しと
    /// 表示の切り替え（コードビハインド）を実際の画面で確かめる。
    /// </para>
    /// <para>
    /// 未登録のカードのタッチはメイン画面の DEBUG パネル（<c>HybridCardReader.SimulateCardRead</c>）で起こすため、
    /// Debug 起動のときだけ実行する。読み取れた側は環境変数 <c>ICCARDMANAGER_DEBUG_CARD_DATA</c>（本体の <c>DebugCardData</c>）で
    /// 仮想タッチのカードの残高と利用履歴を渡し、読み取れなかった側は渡さない（実カードリーダーへ委譲され、カードが無いので読めない）。
    /// ダイアログはキャンセルし、DB に何も書かないことも確かめる。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class CardTypeSelectionKeepCardNoticeUiTests : IDisposable
    {
        private const string SkipReason =
            "Issue #2385: 未登録カードのタッチは DEBUG パネル（DEBUG ビルド限定）で起こすため、Debug 起動のときだけ実行する。";

        private const int CardBalance = 4790;

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        private readonly string _cardDataPath =
            Path.Combine(Path.GetTempPath(), $"iccard-uitest-card-data-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            try
            {
                File.Delete(_cardDataPath);
            }
            catch (IOException)
            {
            }

            GC.SuppressFinalize(this);
        }

        [SkippableFact]
        public void 残高と利用履歴を事前に読み取れたときは注意を出さないこと()
        {
            SkipUnlessDebug();
            using var fixture = LaunchWithCardData();

            var dialog = TouchUnregisteredCard(fixture);
            dialog.FindByNameStartsWith(TestConstants.CardTypeSelectionKeepCardNoticePrefix)
                .Should().BeNull("事前に読み取れたので登録時にカードを読み直さず、離さないよう求める必要がない");

            CancelAndAssertNothingRegistered(fixture, dialog);
        }

        [SkippableFact]
        public void 事前の読み取りに失敗したときは注意を出すこと()
        {
            SkipUnlessDebug();
            // 仮想タッチのカードのデータを渡さない → 実カードリーダーへ委譲され、載っているカードが無いので読めない
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);

            var dialog = TouchUnregisteredCard(fixture);
            dialog.FindByNameStartsWith(TestConstants.CardTypeSelectionKeepCardNoticePrefix)
                .Should().NotBeNull("読み取れなかったので登録時にカードを読み直す。離さないよう求める");

            CancelAndAssertNothingRegistered(fixture, dialog);
        }

        // ── ヘルパー ─────────────────────────────────

        private static void SkipUnlessDebug() =>
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);

        /// <summary>
        /// メイン画面で未登録のカード（DEBUG パネルの「交通系ICカード」）をタッチし、カード種別選択ダイアログの中身が出るまで待つ。
        /// </summary>
        private static DialogPageBase TouchUnregisteredCard(AppFixture fixture)
        {
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            var dialog = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, page.Window, TestConstants.CardTypeSelectionDialogName));
            // 注意の有無を見る前に、ダイアログの中身（ボタン）が UIA に出るまで待つ（出る前の「無い」で緑にしない）
            dialog.FindByNameWithRetry(TestConstants.CardTypeSelectionIcCardButton, Wait)
                .Should().NotBeNull("カード種別選択ダイアログに「交通系ICカード」の選択肢があること");
            return dialog;
        }

        private static void CancelAndAssertNothingRegistered(AppFixture fixture, DialogPageBase dialog)
        {
            var cardCountBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card");
            dialog.ClickButton(TestConstants.CardTypeSelectionCancelButton);
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.CardTypeSelectionDialogName)
                .Should().BeTrue("キャンセルでダイアログが閉じること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card").Should().Be(cardCountBefore, "キャンセルしたので登録されないこと");
        }

        private AppFixture LaunchWithCardData()
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var json =
                "{ \"cards\": [ { " +
                $"\"idm\": \"{ScreenshotSeedData.VirtualTouchCardIdm}\", \"balance\": {CardBalance}, \"history\": [ " +
                $"{{ \"useDate\": \"{today}\", \"entryStation\": \"博多\", \"exitStation\": \"天神\", \"amount\": 210, \"balance\": {CardBalance} }} " +
                "] } ] }";
            File.WriteAllText(_cardDataPath, json);

            var environment = new Dictionary<string, string>(AppFixture.SuppressDebugTestData.ToDictionary(p => p.Key, p => p.Value))
            {
                ["ICCARDMANAGER_DEBUG_CARD_DATA"] = _cardDataPath,
            };
            return AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, environment);
        }
    }
}
