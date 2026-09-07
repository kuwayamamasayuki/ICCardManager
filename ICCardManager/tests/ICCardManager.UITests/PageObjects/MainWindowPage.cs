using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
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

        // ── カード一覧・履歴（Issue #2016） ──────────────────

        /// <summary>
        /// カード一覧（右サイドバー）の行をクリックして利用履歴を開き、履歴表示エリアが現れるまで待つ。
        /// </summary>
        /// <param name="cardDisplayName">
        /// 行の AutomationProperties.Name（<c>IcCard.DisplayName</c> ＝ 「種別 管理番号」。例: 「はやかけん 001」）。
        /// </param>
        /// <returns>履歴表示エリア内の「履歴を閉じる」ボタン（表示完了の証拠）。</returns>
        public AutomationElement OpenCardHistory(string cardDisplayName)
        {
            var list = CardListElement
                ?? throw new InvalidOperationException(
                    $"カード一覧が見つかりません: AutomationProperties.Name=\"{TestConstants.CardList}\"");

            // 行テンプレートの Border に MouseBinding(LeftClick) が付いているため、まず実クリックで開く。
            // FlaUI のクリックは行の選択だけで終わり MouseBinding が発火しないことがある（実測）ので、
            // 短い待機で開かなければ、選択済みの行に対して Enter（ListView の KeyBinding）で開く。
            // ダッシュボードの再読み込みで行が作り直されると掴んでいた要素が古くなるため、試行のたびに行を探し直す。
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // ListViewItem は ItemContainerStyle で AutomationProperties.Name=DisplayName を持つ。
                // 行の出現はダッシュボードの非同期読み込み完了後なので Retry で待つ。
                var row = Retry.WhileNull(
                    () => list.FindFirstDescendant(cf => cf.ByName(cardDisplayName)),
                    TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result
                    ?? throw new TimeoutException(
                        $"カード一覧に行が現れませんでした: \"{cardDisplayName}\"（{TestConstants.DialogOpenTimeoutSeconds}秒タイムアウト）");

                ScreenshotHelper.RequireForeground(Window);
                row.Click();
                var closeButton = WaitForCloseHistoryButton(TimeSpan.FromSeconds(2));
                if (closeButton != null)
                {
                    return closeButton;
                }

                // Keyboard.Press は押し下げだけ（離さない）なので、押下＋解放の Type を使う
                row.Focus();
                Keyboard.Type(VirtualKeyShort.RETURN);
                closeButton = WaitForCloseHistoryButton(TimeSpan.FromSeconds(3));
                if (closeButton != null)
                {
                    return closeButton;
                }
            }

            throw new TimeoutException(
                $"履歴表示エリアが表示されませんでした（\"{TestConstants.CloseHistoryButton}\" ボタンが現れない。クリック＋Enter を {maxAttempts} 回試行）");
        }

        /// <summary>
        /// 履歴表示エリア内の「履歴を閉じる」ボタンが現れるまで待つ。
        /// エリア自体は Border（UIA ツリーに公開されない）なので、このボタンの出現で表示完了を判定する。
        /// </summary>
        private AutomationElement? WaitForCloseHistoryButton(TimeSpan timeout)
        {
            return Retry.WhileNull(
                () =>
                {
                    var button = FindByName(TestConstants.CloseHistoryButton);
                    return button != null && !button.IsOffscreen ? button : null;
                },
                timeout).Result;
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
