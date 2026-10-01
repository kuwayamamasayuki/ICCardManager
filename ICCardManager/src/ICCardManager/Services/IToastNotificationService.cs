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
    /// 設定された画面隅（<see cref="ICCardManager.Models.ToastPosition"/>。既定は右上）に表示される、
    /// フォーカスを奪わない通知を管理するサービス。
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
        /// <param name="warningBalance">残額警告しきい値（isLowBalance=trueの場合に表示用）</param>
        void ShowReturnNotification(string cardType, string cardNumber, int balance, bool isLowBalance = false, int warningBalance = 0);

        /// <summary>
        /// 職員証認識通知を表示
        /// </summary>
        /// <param name="staffName">職員名</param>
        void ShowStaffRecognizedNotification(string staffName);

        /// <summary>
        /// 情報通知を表示
        /// </summary>
        /// <param name="title">タイトル</param>
        /// <param name="message">メッセージ</param>
        void ShowInfo(string title, string message);

        /// <summary>
        /// 警告通知を表示（自動で消える。表示時間は貸出・返却の通知より長い。Issue #2141）
        /// </summary>
        /// <param name="title">タイトル</param>
        /// <param name="message">メッセージ</param>
        void ShowWarning(string title, string message);

        /// <summary>
        /// 「操作は台帳に記録済みだが後処理が完了しなかった」ことの案内を表示（Issue #2141）
        /// </summary>
        /// <param name="title">タイトル（「返却は記録済み」等）</param>
        /// <param name="message">メッセージ（「再タッチしないでください」を含む）</param>
        /// <remarks>
        /// 警告の見た目で表示し、<see cref="ShowError"/> と同じく<b>自動では消さない</b>。
        /// 再タッチを止める指示が 3 秒で消えると、見逃した職員の再タッチが30秒ルールの逆処理で
        /// 逆の操作を新たに記録する（#1725 / #1805）。
        /// </remarks>
        void ShowRecordedNotice(string title, string message);

        /// <summary>
        /// エラー通知を表示
        /// </summary>
        /// <param name="title">タイトル</param>
        /// <param name="message">メッセージ</param>
        /// <remarks>
        /// 自動では消えない。同時に 1 枚だけ表示し、新しい通知が古い通知を置き換える（Issue #2141）。
        /// </remarks>
        void ShowError(string title, string message);

        /// <summary>
        /// 自動では消えない通知（エラー・記録済みの案内）を閉じる（Issue #2141）
        /// </summary>
        /// <remarks>
        /// 次の職員証タッチ（次の操作の開始）とメイン画面の Esc キーから呼ぶ。
        /// 前の職員宛ての案内を、次の職員の操作の上に残さないため。
        /// </remarks>
        void DismissPersistentNotifications();
    }
}
