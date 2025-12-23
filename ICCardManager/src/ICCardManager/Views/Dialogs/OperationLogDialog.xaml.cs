using System.Windows;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// 操作ログ検索ダイアログ
/// </summary>
public partial class OperationLogDialog : Window
{
    private readonly OperationLogSearchViewModel _viewModel;

    public OperationLogDialog(OperationLogSearchViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        // 画面表示時に初期検索を実行
        Loaded += OperationLogDialog_Loaded;
    }

    private async void OperationLogDialog_Loaded(object sender, RoutedEventArgs e)
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
