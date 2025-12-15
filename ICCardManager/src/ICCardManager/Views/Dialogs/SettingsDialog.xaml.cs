using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// 設定ダイアログ
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsDialog(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += async (s, e) => await _viewModel.InitializeAsync();

        // 保存完了時に自動的に閉じる
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsSaved) && _viewModel.IsSaved)
            {
                DialogResult = true;
                Close();
            }
        };
    }

    /// <summary>
    /// キャンセルボタンクリック
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
