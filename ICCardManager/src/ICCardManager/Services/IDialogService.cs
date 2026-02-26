using System;
using System.Threading.Tasks;
using System.Windows;

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

        /// <summary>
        /// カード登録モード選択ダイアログを表示（Issue #510）
        /// </summary>
        /// <param name="currentCardBalance">カードの現在残高（繰越額のデフォルト値として使用）</param>
        /// <returns>選択結果。キャンセル時はnull</returns>
        Views.Dialogs.CardRegistrationModeResult? ShowCardRegistrationModeDialog(int? currentCardBalance = null);
    }

    /// <summary>
    /// ナビゲーションサービスのインターフェース（Issue #853）
    /// </summary>
    /// <remarks>
    /// <para>
    /// IDialogServiceを拡張し、DIコンテナからダイアログを解決して表示する機能を提供する。
    /// ViewModelはこのインターフェースを通じてダイアログを表示し、
    /// App.Current.ServiceProviderに直接依存しない。
    /// </para>
    /// <para>
    /// configureコールバックでダイアログ表示前の初期化（プロパティ設定、非同期データ読み込み等）を行える。
    /// Ownerは自動的にApplication.Current.MainWindowに設定されるが、configure内でオーバーライド可能。
    /// </para>
    /// </remarks>
    public interface INavigationService : IDialogService
    {
        /// <summary>
        /// DIコンテナからダイアログを解決し、Ownerを設定してShowDialogを呼び出す
        /// </summary>
        /// <typeparam name="TDialog">ダイアログのWindow型</typeparam>
        /// <param name="configure">ダイアログ表示前の設定コールバック（省略可）</param>
        /// <returns>ShowDialogの戻り値</returns>
        bool? ShowDialog<TDialog>(Action<TDialog> configure = null) where TDialog : Window;

        /// <summary>
        /// DIコンテナからダイアログを解決し、非同期初期化後にShowDialogを呼び出す
        /// </summary>
        /// <typeparam name="TDialog">ダイアログのWindow型</typeparam>
        /// <param name="configure">ダイアログ表示前の非同期設定コールバック（省略可）</param>
        /// <returns>ShowDialogの戻り値</returns>
        Task<bool?> ShowDialogAsync<TDialog>(Func<TDialog, Task> configure = null) where TDialog : Window;
    }
}
