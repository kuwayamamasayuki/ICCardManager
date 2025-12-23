using System.Windows;
using ICCardManager.Common;
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

        Loaded += SettingsDialog_Loaded;
    }

    private async void SettingsDialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.ShowError(ex, "初期化エラー");
        }
    }

    /// <summary>
    /// 保存ボタンクリック
    /// </summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveAsync();

            // 保存が成功した場合（IsSavedがtrue）、ダイアログを閉じる
            if (_viewModel.IsSaved)
            {
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.ShowError(ex, "保存エラー");
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
