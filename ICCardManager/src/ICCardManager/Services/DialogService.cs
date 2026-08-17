using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ICCardManager.Services
{
    /// <summary>
    /// ダイアログサービスの実装
    /// </summary>
    /// <remarks>
    /// System.Windows.MessageBoxをラップし、IDialogServiceインターフェースを実装する。
    /// 本番環境ではこのクラスが使用され、テスト環境ではモックが使用される。
    /// </remarks>
    public class DialogService : IDialogService
    {
        /// <summary>
        /// モーダル表示のオーナーウィンドウを解決する（Issue #1794。テスト用の継ぎ目）
        /// </summary>
        /// <remarks>
        /// OS へ問い合わせる部分を切り出したテスト用の継ぎ目。
        /// 「アプリが非フォアグラウンドのときオーナーが NULL になる」状態は単体テストから
        /// 再現できないため、本メソッドを差し替えて配線だけを検証できるようにしている
        /// （<c>ConnectionDiagnosticsService.ProbeFolderWriteAccess</c> と同じ理由）。
        /// </remarks>
        protected virtual Window ResolveOwner() => Common.DialogOwnerResolver.Resolve();

        /// <summary>
        /// MessageBox を実際に表示する（テスト用の継ぎ目。<see cref="ResolveOwner"/> と同じ理由）
        /// </summary>
        /// <remarks>
        /// オーナーを解決できないときは従来どおりオーナー無しで表示する。
        /// 表示しないより、クリックシールドが無い状態でも表示するほうが望ましい。
        /// </remarks>
        protected virtual MessageBoxResult ShowMessageBoxCore(
            Window owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            return owner != null
                ? MessageBox.Show(owner, message, title, button, image)
                : MessageBox.Show(message, title, button, image);
        }

        /// <summary>
        /// オーナーを解決してから MessageBox を表示する。
        /// <b>本クラスからの MessageBox 表示は必ず本メソッドを経由すること</b>
        /// （直呼びは <c>DialogServiceOwnerTests</c> の静的検査が検出する）。
        /// </summary>
        private MessageBoxResult Show(string message, string title, MessageBoxButton button, MessageBoxImage image)
            => ShowMessageBoxCore(ResolveOwner(), message, title, button, image);

        /// <inheritdoc/>
        public bool ShowConfirmation(string message, string title)
            => Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        /// <inheritdoc/>
        public bool ShowWarningConfirmation(string message, string title)
            => Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        /// <inheritdoc/>
        public void ShowInformation(string message, string title)
            => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        /// <inheritdoc/>
        public void ShowWarning(string message, string title)
            => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        /// <inheritdoc/>
        public void ShowError(string message, string title)
            => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        /// <inheritdoc/>
        public Views.Dialogs.CardRegistrationModeResult? ShowCardRegistrationModeDialog(int? currentCardBalance = null)
        {
            var dialog = new Views.Dialogs.CardRegistrationModeDialog(currentCardBalance);
            dialog.Owner = Application.Current.MainWindow;

            var result = dialog.ShowDialog();
            if (result == true)
            {
                return dialog.Result;
            }

            return null;
        }
    }

    /// <summary>
    /// ナビゲーションサービスの実装（Issue #853）
    /// </summary>
    /// <remarks>
    /// DialogServiceを継承し、DIコンテナからダイアログを解決して表示する機能を追加する。
    /// IDialogServiceとINavigationServiceの両方のインターフェースを実装する。
    /// </remarks>
    public class NavigationService : DialogService, INavigationService
    {
        private readonly IServiceProvider _serviceProvider;

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc/>
        public bool? ShowDialog<TDialog>(Action<TDialog> configure = null) where TDialog : Window
        {
            var dialog = _serviceProvider.GetRequiredService<TDialog>();
            dialog.Owner = Application.Current.MainWindow;
            configure?.Invoke(dialog);
            return dialog.ShowDialog();
        }

        /// <inheritdoc/>
        public async Task<bool?> ShowDialogAsync<TDialog>(Func<TDialog, Task> configure = null) where TDialog : Window
        {
            var dialog = _serviceProvider.GetRequiredService<TDialog>();
            dialog.Owner = Application.Current.MainWindow;
            if (configure != null)
            {
                await configure(dialog);
            }
            return dialog.ShowDialog();
        }
    }
}
