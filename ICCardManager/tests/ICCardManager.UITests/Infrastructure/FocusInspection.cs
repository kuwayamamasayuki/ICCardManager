using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// キーボード フォーカスの所在を調べる（Issue #2192 / #2194）。
    /// </summary>
    internal static class FocusInspection
    {
        /// <summary>
        /// フォーカスのある要素が <paramref name="element"/> 自身かその子孫か。
        /// </summary>
        /// <remarks>
        /// DataGrid は行やセル、ComboBox・DatePicker は内側の入力欄がフォーカスを持つため、
        /// 要素そのものの HasKeyboardFocus だけでは判定できない。フォーカスのある要素から親をたどる。
        /// </remarks>
        public static bool HasFocusWithin(AutomationElement element)
        {
            try
            {
                var current = element.Automation.FocusedElement();
                for (var depth = 0; current != null && depth < 32; depth++)
                {
                    if (current.Equals(element))
                    {
                        return true;
                    }

                    current = current.Parent;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>フォーカスが <paramref name="element"/> の内側へ来るのを待つ。来たら true。</summary>
        public static bool WaitForFocusWithin(AutomationElement element, TimeSpan? timeout = null) =>
            Retry.WhileFalse(() => HasFocusWithin(element), timeout ?? TimeSpan.FromSeconds(5)).Success;

        /// <summary>いまフォーカスのある要素の説明（失敗メッセージ用）。</summary>
        public static string DescribeFocused(AutomationElement anyElement)
        {
            try
            {
                var focused = anyElement.Automation.FocusedElement();
                return focused == null ? "（なし）" : $"「{focused.Name}」({focused.ControlType})";
            }
            catch (Exception ex)
            {
                return $"（取得に失敗: {ex.GetType().Name}）";
            }
        }
    }
}
