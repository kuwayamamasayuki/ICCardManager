using System.Windows;

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
    }
}
