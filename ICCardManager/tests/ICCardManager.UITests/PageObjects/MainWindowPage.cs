using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using ICCardManager.UITests.Infrastructure;

namespace ICCardManager.UITests.PageObjects
{
    /// <summary>
    /// メインウィンドウのページオブジェクト。
    /// ツールバー、ステータスバー、コンテンツエリアへのアクセスを提供する。
    /// </summary>
    internal sealed class MainWindowPage : DialogPageBase
    {
        private readonly UIA3Automation _automation;

        public MainWindowPage(Window mainWindow, UIA3Automation automation)
            : base(mainWindow)
        {
            _automation = automation;
        }

        // ── ツールバーボタン ──────────────────────────────

        public AutomationElement? ReportButton =>
            FindByName(TestConstants.OpenReportButton);

        public AutomationElement? StaffManageButton =>
            FindByName(TestConstants.OpenStaffManageButton);

        public AutomationElement? CardManageButton =>
            FindByName(TestConstants.OpenCardManageButton);

        public AutomationElement? DataExportImportButton =>
            FindByName(TestConstants.OpenDataExportImportButton);

        public AutomationElement? SettingsButton =>
            FindByName(TestConstants.OpenSettingsButton);

        public AutomationElement? SystemManageButton =>
            FindByName(TestConstants.OpenSystemManageButton);

        public AutomationElement? HelpButton =>
            FindByName(TestConstants.OpenHelpButton);

        public AutomationElement? ExitButton =>
            FindByName(TestConstants.ExitButton);

        // ── ステータスバー ────────────────────────────────
        // WPF の StatusBarItem は UIA ツリーに公開されない。
        // 内部の TextBlock テキスト内容でプレフィックス検索する。

        public AutomationElement? CardReaderStatusElement =>
            FindByNameStartsWith(TestConstants.CardReaderStatusTextPrefix);

        public AutomationElement? AppVersionElement =>
            FindByNameStartsWith(TestConstants.AppVersionTextPrefix);

        // ── コンテンツエリア ──────────────────────────────
        // Border も UIA ツリーに公開されないため、
        // 内部の TextBlock テキスト内容で検索する。

        public AutomationElement? UsageGuideElement =>
            FindByName(TestConstants.UsageGuideText);

        public AutomationElement? CardListElement =>
            FindByName(TestConstants.CardList);

        // ── ダイアログ操作 ────────────────────────────────

        /// <summary>
        /// ツールバーボタンをクリックし、指定名のダイアログが開くまで待機する。
        /// </summary>
        /// <param name="buttonAutomationName">クリックするボタンの AutomationProperties.Name</param>
        /// <param name="dialogAutomationName">期待するダイアログの AutomationProperties.Name</param>
        /// <returns>開いたダイアログのページオブジェクト</returns>
        public DialogPageBase ClickToolbarButtonAndWaitForDialog(
            string buttonAutomationName,
            string dialogAutomationName)
        {
            ClickButton(buttonAutomationName);

            var dialog = WaitForDialog(dialogAutomationName);
            return new DialogPageBase(dialog);
        }

        /// <summary>
        /// 指定名のモーダルダイアログウィンドウが開くまで待機する。
        /// </summary>
        public Window WaitForDialog(string dialogAutomationName)
        {
            var result = Retry.WhileNull(
                () =>
                {
                    var modalWindows = Window.ModalWindows;
                    foreach (var w in modalWindows)
                    {
                        if (w.Name == dialogAutomationName)
                            return w;
                    }
                    return null;
                },
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds));

            if (result.Result == null)
            {
                throw new TimeoutException(
                    $"ダイアログが開きませんでした: \"{dialogAutomationName}\"（{TestConstants.DialogOpenTimeoutSeconds}秒タイムアウト）");
            }

            return result.Result;
        }

        /// <summary>
        /// 終了ボタンをクリックしてアプリケーションを終了する。
        /// </summary>
        public void ClickExitButton()
        {
            ClickButton(TestConstants.ExitButton);
        }
    }
}
