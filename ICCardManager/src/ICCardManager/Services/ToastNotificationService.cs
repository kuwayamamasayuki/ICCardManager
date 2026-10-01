using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Views;

namespace ICCardManager.Services
{
    /// <summary>
    /// トースト通知サービスの実装
    /// </summary>
    /// <remarks>
    /// ToastNotificationWindowを使用して、設定された画面隅
    /// （<see cref="ICCardManager.Models.ToastPosition"/>。既定は右上）に通知を表示する。
    /// フォーカスを奪わないため、職員の操作を妨げない。
    /// </remarks>
    public class ToastNotificationService : IToastNotificationService
    {
        /// <summary>
        /// 貸出通知を表示
        /// </summary>
        public void ShowLendNotification(string cardType, string cardNumber)
        {
            var cardInfo = $"{cardType} {cardNumber}";
            ToastNotificationWindow.ShowLend(cardInfo);
        }

        /// <summary>
        /// 返却通知を表示
        /// </summary>
        public void ShowReturnNotification(string cardType, string cardNumber, int balance, bool isLowBalance = false, int warningBalance = 0)
        {
            var cardInfo = $"{cardType} {cardNumber}";
            ToastNotificationWindow.ShowReturn(cardInfo, balance, isLowBalance, warningBalance);
        }

        /// <summary>
        /// 職員証認識通知を表示
        /// </summary>
        public void ShowStaffRecognizedNotification(string staffName)
        {
            ToastNotificationWindow.Show(ToastType.Info, $"{staffName} さん", "交通系ICカードをタッチしてください");
        }

        /// <summary>
        /// 情報通知を表示
        /// </summary>
        public void ShowInfo(string title, string message)
        {
            ToastNotificationWindow.Show(ToastType.Info, title, message);
        }

        /// <summary>
        /// 警告通知を表示
        /// </summary>
        public void ShowWarning(string title, string message)
        {
            ToastNotificationWindow.Show(ToastType.Warning, title, message);
        }

        /// <summary>
        /// 記録済みの案内を表示（Issue #2141）
        /// </summary>
        /// <remarks>
        /// 警告の見た目で、自動では消さない（<see cref="ShowError"/> と同じ置き場を使う）。
        /// </remarks>
        public void ShowRecordedNotice(string title, string message)
        {
            ToastNotificationWindow.Show(ToastType.Warning, title, message, autoClose: false);
        }

        /// <summary>
        /// エラー通知を表示
        /// </summary>
        /// <remarks>
        /// エラー通知は自動消去されません（重要なエラーメッセージを見逃すことを防ぐ）。
        /// 同時に 1 枚だけ表示し、新しい通知が古い通知を置き換えます。クリック・メイン画面の Esc キー・
        /// 次の職員証タッチで閉じます（Issue #2141）。
        /// </remarks>
        public void ShowError(string title, string message)
        {
            ToastNotificationWindow.Show(ToastType.Error, title, message, autoClose: false);
        }

        /// <summary>
        /// 自動では消えない通知を閉じる（Issue #2141）
        /// </summary>
        public void DismissPersistentNotifications()
        {
            ToastNotificationWindow.DismissPersistent();
        }
    }
}
