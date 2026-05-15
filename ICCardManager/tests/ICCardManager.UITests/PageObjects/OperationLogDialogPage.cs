using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using ICCardManager.UITests.Infrastructure;

namespace ICCardManager.UITests.PageObjects
{
    /// <summary>
    /// 操作ログダイアログのページオブジェクト。Issue #1522 で追加。
    /// クイックフィルタ「今日/今月/先月」と操作種別 ComboBox へのアクセスを提供する。
    /// </summary>
    internal sealed class OperationLogDialogPage : DialogPageBase
    {
        public OperationLogDialogPage(Window dialogWindow)
            : base(dialogWindow)
        {
        }

        public AutomationElement? QuickFilterTodayButton =>
            FindByName(TestConstants.OperationLogQuickFilterToday);

        public AutomationElement? QuickFilterThisMonthButton =>
            FindByName(TestConstants.OperationLogQuickFilterThisMonth);

        public AutomationElement? QuickFilterLastMonthButton =>
            FindByName(TestConstants.OperationLogQuickFilterLastMonth);

        public AutomationElement? ActionTypeComboBox =>
            FindByName(TestConstants.OperationLogActionTypeComboBox);

        /// <summary>
        /// クイックフィルタ 3 ボタンを (今日, 今月, 先月) の順で返す。
        /// 1 個でも null なら例外。
        /// </summary>
        public (AutomationElement Today, AutomationElement ThisMonth, AutomationElement LastMonth)
            RequireQuickFilterButtons(TimeSpan? retryTimeout = null)
        {
            var today = FindByNameWithRetry(TestConstants.OperationLogQuickFilterToday, retryTimeout);
            var thisMonth = FindByNameWithRetry(TestConstants.OperationLogQuickFilterThisMonth, retryTimeout);
            var lastMonth = FindByNameWithRetry(TestConstants.OperationLogQuickFilterLastMonth, retryTimeout);

            if (today == null || thisMonth == null || lastMonth == null)
            {
                throw new InvalidOperationException(
                    "クイックフィルタ 3 ボタンが OperationLogDialog 内に揃って見つかりません。" +
                    $"今日={today != null}, 今月={thisMonth != null}, 先月={lastMonth != null}");
            }

            return (today!, thisMonth!, lastMonth!);
        }
    }
}
