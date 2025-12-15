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
    }

    /// <summary>
    /// 保存ボタンクリック
    /// </summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAsync();

        // 保存が成功した場合（IsSavedがtrue）、ダイアログを閉じる
        if (_viewModel.IsSaved)
        {
            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// キャンセルボタンクリック
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
