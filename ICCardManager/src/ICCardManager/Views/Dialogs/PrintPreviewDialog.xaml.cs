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

        // イベント購読
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewModel.DocumentNeedsRefresh += OnDocumentNeedsRefresh;
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
        RefreshDocument();
    }

    /// <summary>
    /// ウィンドウアンロード時
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // イベント購読解除（メモリリーク防止）
        ViewModel.DocumentNeedsRefresh -= OnDocumentNeedsRefresh;
    }

    /// <summary>
    /// ドキュメント再描画イベントハンドラ
    /// </summary>
    private void OnDocumentNeedsRefresh(object? sender, EventArgs e)
    {
        RefreshDocument();
    }

    /// <summary>
    /// ドキュメントを再設定して再描画
    /// </summary>
    private void RefreshDocument()
    {
        if (ViewModel.Document != null)
        {
            // 一度nullを設定してから再設定することで強制的に再描画
            DocumentViewer.Document = null;
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
