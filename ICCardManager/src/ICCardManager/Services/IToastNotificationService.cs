using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Services
{
/// <summary>
    /// トースト通知サービスのインターフェース
    /// </summary>
    /// <remarks>
    /// 画面右上に表示されるフォーカスを奪わない通知を管理するサービス。
    /// 貸出・返却時の通知をメインウィンドウとは別ウィンドウで表示し、
    /// 職員の操作を妨げないようにする。
    /// </remarks>
    public interface IToastNotificationService
    {
        /// <summary>
        /// 貸出通知を表示
        /// </summary>
        /// <param name="cardType">カード種別（例: "はやかけん"）</param>
        /// <param name="cardNumber">カード番号（例: "H-001"）</param>
        void ShowLendNotification(string cardType, string cardNumber);

        /// <summary>
        /// 返却通知を表示
        /// </summary>
        /// <param name="cardType">カード種別</param>
        /// <param name="cardNumber">カード番号</param>
        /// <param name="balance">残額</param>
        /// <param name="isLowBalance">残額警告フラグ</param>
        void ShowReturnNotification(string cardType, string cardNumber, int balance, bool isLowBalance = false);

        /// <summary>
        /// 情報通知を表示
        /// </summary>
        /// <param name="title">タイトル</param>
        /// <param name="message">メッセージ</param>
        void ShowInfo(string title, string message);

        /// <summary>
        /// 警告通知を表示
        /// </summary>
        /// <param name="title">タイトル</param>
        /// <param name="message">メッセージ</param>
        void ShowWarning(string title, string message);

        /// <summary>
        /// エラー通知を表示
        /// </summary>
        /// <param name="title">タイトル</param>
        /// <param name="message">メッセージ</param>
        void ShowError(string title, string message);
    }
}
