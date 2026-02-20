using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using ICCardManager.UITests.Infrastructure;

namespace ICCardManager.UITests.PageObjects
{
    /// <summary>
    /// ダイアログウィンドウの基底ページオブジェクト。
    /// 共通のヘルパーメソッドを提供する。
    /// </summary>
    internal class DialogPageBase
    {
        internal DialogPageBase(Window window)
        {
            Window = window ?? throw new ArgumentNullException(nameof(window));
        }

        /// <summary>
        /// ダイアログの Window 要素。
        /// </summary>
        public Window Window { get; }

        /// <summary>
        /// ダイアログのタイトル（AutomationProperties.Name）。
        /// </summary>
        public string Name => Window.Name;

        /// <summary>
        /// ダイアログを閉じる（Alt+F4）。
        /// </summary>
        public void Close()
        {
            Window.Close();
        }

        /// <summary>
        /// AutomationProperties.Name でボタンを検索してクリックする。
        /// </summary>
        public void ClickButton(string automationName)
        {
            var button = FindByName(automationName);
            if (button == null)
            {
                throw new InvalidOperationException(
                    $"ボタンが見つかりません: AutomationProperties.Name=\"{automationName}\"");
            }
            button.AsButton().Invoke();
        }

        /// <summary>
        /// AutomationProperties.Name で要素を検索する。
        /// </summary>
        public AutomationElement? FindByName(string automationName)
        {
            var cf = Window.ConditionFactory;
            return Window.FindFirstDescendant(
                cf.ByName(automationName));
        }

        /// <summary>
        /// AutomationProperties.Name で要素をリトライ付きで検索する。
        /// </summary>
        public AutomationElement? FindByNameWithRetry(string automationName, TimeSpan? timeout = null)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);
            var result = Retry.WhileNull(
                () =>
                {
                    var cf = Window.ConditionFactory;
                    return Window.FindFirstDescendant(cf.ByName(automationName));
                },
                effectiveTimeout);

            return result.Result;
        }

        /// <summary>
        /// Name が指定文字列で始まる要素を検索する。
        /// WPF の StatusBarItem や Border は UIA ツリーに公開されないため、
        /// 内部の TextBlock のテキスト内容で検索するために使用する。
        /// </summary>
        public AutomationElement? FindByNameStartsWith(string prefix)
        {
            return Window.FindAllDescendants()
                .FirstOrDefault(e => (e.Name ?? "").StartsWith(prefix));
        }
    }
}
