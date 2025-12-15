using System.Windows;
using System.Windows.Documents;
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

        // ウィンドウ表示後にドキュメントを設定
        Loaded += OnLoaded;
    }

    /// <summary>
    /// ドキュメントを設定（バインディングの代わりに直接設定）
    /// </summary>
    /// <remarks>
    /// FlowDocumentは一度に1つの親要素にしか所属できないため、
    /// データバインディングではなく直接設定する必要がある場合がある
    /// </remarks>
    public void SetDocument(FlowDocument document)
    {
        DocumentViewer.Document = document;
    }

    /// <summary>
    /// ウィンドウ読み込み完了
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ViewModelのドキュメントをFlowDocumentScrollViewerに直接設定
        if (ViewModel.Document != null)
        {
            DocumentViewer.Document = ViewModel.Document;
        }

        // ページ数を再計算
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
