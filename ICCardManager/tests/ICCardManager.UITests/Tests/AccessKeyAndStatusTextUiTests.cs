using System;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

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
    /// （秒数欄が先にある）。キーの組み合わせは同時押し（<see cref="Keyboard.TypeSimultaneously"/>）で送り、押したままにしない。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class AccessKeyAndStatusTextUiTests
    {
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
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = page.ClickToolbarButtonAndWaitForDialog(TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName);

            var target = dialog.Window.FindFirstDescendant(cf =>
                cf.ByName(expectedName).And(cf.ByControlType(ControlType.Edit)));
            target.Should().NotBeNull($"設定ダイアログに「{expectedName}」の入力欄があること");

            ScreenshotHelper.RequireForeground(dialog.Window);
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, key);

            FocusInspection.WaitForFocusWithin(target!).Should().BeTrue(
                $"Alt+{key} 1 回で「{expectedName}」へフォーカスが移ること（実際は {FocusInspection.DescribeFocused(target!)}）");
        }
    }
}
