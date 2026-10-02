using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// F1〜F6 で各ダイアログが開き、Esc で閉じることの回帰テスト
    /// （Issue #2194。07_テスト設計書 ST-007 M6・M7 の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 静的検査（<c>MainWindowKeyBindingTests</c>・<c>DialogEscapeCloseConventionTests</c>）は KeyBinding と Esc の手段の
    /// <b>宣言</b>までで、実際にキーを押したときにメイン画面がキーを受け取り、目的のダイアログが開くかは見ていない
    /// （フォーカスのある要素がキーを消費すると、宣言があっても開かない）。
    /// </para>
    /// <para>
    /// キー入力は前面のウィンドウへ届くので、押す前に前面化を確かめる（<see cref="ScreenshotHelper.RequireForeground"/>）。
    /// Release・Debug のどちらでも走る。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class FunctionKeyDialogTests
    {
        [Theory]
        [InlineData(VirtualKeyShort.F1, TestConstants.ReportDialogName)]
        [InlineData(VirtualKeyShort.F2, TestConstants.StaffManageDialogName)]
        [InlineData(VirtualKeyShort.F3, TestConstants.CardManageDialogName)]
        [InlineData(VirtualKeyShort.F4, TestConstants.DataExportImportDialogName)]
        [InlineData(VirtualKeyShort.F5, TestConstants.SettingsDialogName)]
        [InlineData(VirtualKeyShort.F6, TestConstants.SystemManageDialogName)]
        public void Fキーで対応するダイアログが開きEscで閉じること(VirtualKeyShort key, string dialogName)
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);

            ScreenshotHelper.RequireForeground(fixture.MainWindow);
            Keyboard.Type(key);
            var dialog = DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, dialogName);

            ScreenshotHelper.RequireForeground(dialog);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, dialogName)
                .Should().BeTrue($"{key} で開いた「{dialogName}」は Esc で閉じること");
        }
    }
}
