using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// 印刷プレビューダイアログ
/// </summary>
public partial class PrintPreviewDialog : Window
{
    /// <summary>
    /// ViewModel
    /// </summary>
    public PrintPreviewViewModel ViewModel { get; }

    public PrintPreviewDialog(PrintPreviewViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// 閉じるボタン
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
