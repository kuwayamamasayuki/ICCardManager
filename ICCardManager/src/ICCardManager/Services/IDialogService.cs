using System;

namespace ICCardManager.Services
{
    /// <summary>
    /// ダイアログサービスのインターフェース
    /// </summary>
    /// <remarks>
    /// MessageBoxを抽象化し、テスト時にモック可能にするためのインターフェース。
    /// ViewModelはこのインターフェースを通じてダイアログを表示し、
    /// 直接MessageBoxを呼び出さない。
    /// </remarks>
    public interface IDialogService
    {
        /// <summary>
        /// 確認ダイアログを表示（Yes/No）
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル</param>
        /// <returns>ユーザーがYesを選択した場合true</returns>
        bool ShowConfirmation(string message, string title);

        /// <summary>
        /// 警告付き確認ダイアログを表示（Yes/No）
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル</param>
        /// <returns>ユーザーがYesを選択した場合true</returns>
        bool ShowWarningConfirmation(string message, string title);

        /// <summary>
        /// 情報ダイアログを表示
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル</param>
        void ShowInformation(string message, string title);

        /// <summary>
        /// 警告ダイアログを表示
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル</param>
        void ShowWarning(string message, string title);

        /// <summary>
        /// エラーダイアログを表示
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル</param>
        void ShowError(string message, string title);
    }
}
