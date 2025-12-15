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

        // ウィンドウ表示後にページ数を再計算
        Loaded += OnLoaded;
    }

    /// <summary>
    /// ウィンドウ読み込み完了
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ドキュメントがビジュアルツリーにアタッチされた後でページ数を再計算
        ViewModel.RecalculatePageCount();
    }

    /// <summary>
    /// 閉じるボタン
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
