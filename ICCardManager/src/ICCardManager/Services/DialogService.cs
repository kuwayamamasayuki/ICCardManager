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
        /// <inheritdoc/>
        public bool ShowConfirmation(string message, string title)
        {
            var result = MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        /// <inheritdoc/>
        public bool ShowWarningConfirmation(string message, string title)
        {
            var result = MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }

        /// <inheritdoc/>
        public void ShowInformation(string message, string title)
        {
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <inheritdoc/>
        public void ShowWarning(string message, string title)
        {
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        /// <inheritdoc/>
        public void ShowError(string message, string title)
        {
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

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
