using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 設定ダイアログで入力した値が保存されることの回帰テスト
    /// （Issue #2194。07_テスト設計書 ST-007 M4・M8 の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// M8 は「マウスで開き、入力欄をクリックして数値を打ち込み、Tab で次へ移り、保存をクリックする」という
    /// <b>実際の入力の組み合わせ</b>の確認なので、UIA の Invoke や ValuePattern ではなくマウスとキーボードで操作する
    /// （バインディングの更新が LostFocus 契機なら、ValuePattern で値を入れると保存に届かない場合を見逃す）。
    /// 座標でクリックするため DPI 対応と左上への移動で座標系をそろえる。
    /// </para>
    /// <para>
    /// M4 の「再起動後も反映される」は、DB に書かれた値と、ダイアログを開き直したときの表示の 2 つで表明する
    /// （UI テストの起動基盤は終了時に DB を退避前へ戻すため、同じ DB で再起動することはできない。
    /// 設定は起動のたびに DB から読み直されるので、DB に書かれていれば再起動後も反映される）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class SettingsPersistenceTests
    {
        /// <summary>残額警告しきい値の既定値（<c>AppSettings.WarningBalance</c>）と異なる値を入れる（既定値のままだと保存を検査できない）。</summary>
        private const string NewWarningBalance = "7000";

        public SettingsPersistenceTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void マウスで開いて入力欄に打ち込み保存した値がDBに書かれ開き直しても表示されること()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 1. マウスで設定ダイアログを開く
            var settings = OpenSettingsByMouse(fixture, page);
            var input = RequireInput(settings);
            Digits(input).Should().NotBe(NewWarningBalance, "前提: 入れる値は現在の値と異なること（同じだと保存を検査できない）");

            // 2. 入力欄をクリックし、全選択して打ち込み、Tab で次へ移る
            ScreenshotHelper.RequireForeground(settings.Window);
            input.Click();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(NewWarningBalance);
            Keyboard.Type(VirtualKeyShort.TAB);

            // 3.「保存」をクリック → 保存に成功するとダイアログは閉じる
            var save = settings.FindByNameWithRetry(TestConstants.SettingsSaveButton);
            save.Should().NotBeNull($"設定ダイアログに「{TestConstants.SettingsSaveButton}」ボタンがあること");
            save!.Click();
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.SettingsDialogName)
                .Should().BeTrue($"保存に成功したら設定ダイアログが閉じること。開いていたウィンドウ: {DialogLocator.DescribeOpenWindows(fixture)}");

            // 4. DB に書かれている（＝再起動後も反映される）
            DatabaseProbe.Scalar("SELECT value FROM settings WHERE key = 'warning_balance'")
                .Should().Be(NewWarningBalance, "打ち込んだ残額警告しきい値が設定として保存されること");

            // 5. 開き直すと、保存した値が表示される
            var reopened = OpenSettingsByMouse(fixture, page);
            Digits(RequireInput(reopened)).Should().Be(NewWarningBalance, "開き直した設定ダイアログに保存した値が表示されること");
        }

        private static DialogPageBase OpenSettingsByMouse(AppFixture fixture, MainWindowPage page)
        {
            var button = page.FindByNameWithRetry(TestConstants.OpenSettingsButton);
            button.Should().NotBeNull($"メイン画面に「{TestConstants.OpenSettingsButton}」ボタンがあること");
            ScreenshotHelper.RequireForeground(page.Window);
            button!.Click();
            return new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.SettingsDialogName));
        }

        private static AutomationElement RequireInput(DialogPageBase settings)
        {
            var input = settings.Window.FindFirstDescendant(cf => cf.ByName(TestConstants.SettingsWarningBalanceInput)
                .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)));
            input.Should().NotBeNull($"設定ダイアログに「{TestConstants.SettingsWarningBalanceInput}」の入力欄があること");
            return input!;
        }

        /// <summary>入力欄の値の数字だけを取り出す（桁区切りの表示に左右されない）。</summary>
        private static string Digits(AutomationElement input) =>
            new string((input.Patterns.Value.Pattern.Value.Value ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}
