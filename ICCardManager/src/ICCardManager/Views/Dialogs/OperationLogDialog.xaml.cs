using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// 操作ログ検索ダイアログ
/// </summary>
public partial class OperationLogDialog : Window
{
    public OperationLogDialog(OperationLogSearchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 画面表示時に初期検索を実行
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
