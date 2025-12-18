using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// 印刷プレビューダイアログ
/// </summary>
/// <remarks>
/// <para>
/// FlowDocumentPageViewerを使用してページ単位のプレビュー表示を実現します。
/// </para>
/// <para>
/// <strong>機能:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>ページナビゲーション（前へ/次へ/最初/最後）</description></item>
/// <item><description>キーボード操作（←→キーでページ移動）</description></item>
/// <item><description>ズーム機能（拡大/縮小）</description></item>
/// <item><description>用紙方向の変更（縦/横）</description></item>
/// </list>
/// </remarks>
public partial class PrintPreviewDialog : Window
{
    /// <summary>
    /// ViewModel
    /// </summary>
    public PrintPreviewViewModel ViewModel { get; }

    /// <summary>
    /// 初期化完了フラグ（初期化中のイベントを無視するため）
    /// </summary>
    private bool _isInitialized;

    /// <summary>
    /// MasterPageNumberプロパティの変更を監視するためのDescriptor
    /// </summary>
    private DependencyPropertyDescriptor? _masterPageNumberDescriptor;

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
        // ViewModelのドキュメントをFlowDocumentPageViewerに直接設定
        RefreshDocument();

        // FlowDocumentPageViewerのMasterPageNumberプロパティの変更を監視
        // これによりViewerのページ変更（組み込みナビゲーション含む）をViewModelに同期
        _masterPageNumberDescriptor = DependencyPropertyDescriptor.FromProperty(
            FlowDocumentPageViewer.MasterPageNumberProperty,
            typeof(FlowDocumentPageViewer));
        _masterPageNumberDescriptor?.AddValueChanged(DocumentViewer, OnMasterPageNumberChanged);

        // フォーカスを設定してキーボード操作を有効化
        Focus();
    }

    /// <summary>
    /// FlowDocumentPageViewerのMasterPageNumber変更時のハンドラ
    /// </summary>
    private void OnMasterPageNumberChanged(object? sender, EventArgs e)
    {
        // 初期化完了前はイベントを無視（初期化時のFirstPage()呼び出し等による）
        if (!_isInitialized) return;
        if (DocumentViewer.Document == null) return;

        // MasterPageNumberは0ベース、表示は1ベース
        int currentPage = DocumentViewer.MasterPageNumber + 1;
        int totalPages = DocumentViewer.PageCount;

        // ツールバーのページ表示を直接更新（Viewerの値を直接反映）
        UpdatePageDisplay(currentPage, totalPages);
    }

    /// <summary>
    /// ページ表示を更新
    /// </summary>
    private void UpdatePageDisplay(int currentPage, int totalPages)
    {
        if (totalPages > 0)
        {
            PageDisplayTextBlock.Text = $"{currentPage} / {totalPages} ページ";
        }
        else
        {
            PageDisplayTextBlock.Text = "ページなし";
        }
    }

    /// <summary>
    /// ウィンドウアンロード時
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // イベント購読解除（メモリリーク防止）
        ViewModel.DocumentNeedsRefresh -= OnDocumentNeedsRefresh;
        _masterPageNumberDescriptor?.RemoveValueChanged(DocumentViewer, OnMasterPageNumberChanged);
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
        _isInitialized = false;

        if (ViewModel.Document != null)
        {
            // 一度nullを設定してから再設定することで強制的に再描画
            DocumentViewer.Document = null;
            DocumentViewer.Document = ViewModel.Document;

            // DocumentPaginatorのページ数計算完了イベントを購読
            var paginator = ((IDocumentPaginatorSource)ViewModel.Document).DocumentPaginator;
            paginator.ComputePageCountCompleted += OnComputePageCountCompleted;

            // ページ数の非同期計算を開始
            if (!paginator.IsPageCountValid)
            {
                paginator.ComputePageCountAsync();
            }
        }

        // ページ数を再計算（レンダリング完了後に実行、ContextIdleで確実にUI更新後）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ViewModel.RecalculatePageCount();

            // 最初のページに移動してMasterPageNumberを0にリセット
            DocumentViewer.FirstPage();

            var pageCount = DocumentViewer.PageCount;
            // FirstPage()後のMasterPageNumberを読み取る（0ベース→1ベースに変換）
            var currentPage = DocumentViewer.MasterPageNumber + 1;

            if (pageCount > 0)
            {
                ViewModel.TotalPages = pageCount;
                ViewModel.CurrentPage = currentPage;
                UpdatePageDisplay(currentPage, pageCount);
            }

            _isInitialized = true;
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// ページ数計算完了時のハンドラ
    /// </summary>
    private void OnComputePageCountCompleted(object? sender, AsyncCompletedEventArgs e)
    {
        if (sender is DocumentPaginator paginator)
        {
            // イベント購読解除（メモリリーク防止）
            paginator.ComputePageCountCompleted -= OnComputePageCountCompleted;
        }

        // UIスレッドでページ数を更新
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdatePageCountFromViewer();
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    /// <summary>
    /// ビューアーからページ数を取得してViewModelを更新
    /// </summary>
    private void UpdatePageCountFromViewer()
    {
        // 初期化完了前は何もしない（ContextIdleで正しく初期化されるため）
        if (!_isInitialized) return;

        if (DocumentViewer.Document != null)
        {
            // FlowDocumentPageViewerのPageCountプロパティを使用
            var pageCount = DocumentViewer.PageCount;
            var currentPage = DocumentViewer.MasterPageNumber + 1;

            if (pageCount > 0)
            {
                ViewModel.TotalPages = pageCount;
                ViewModel.CurrentPage = currentPage;
                UpdatePageDisplay(currentPage, pageCount);
            }
        }
    }

    /// <summary>
    /// ページ情報を更新
    /// </summary>
    private void UpdatePageInfo()
    {
        if (ViewModel.Document != null && DocumentViewer.Document != null)
        {
            // ドキュメントのページサイズをビューアのサイズとして設定
            // ZoomはFlowDocumentPageViewer内部で適用されるため、ここでは1:1で設定
            DocumentViewer.Width = ViewModel.Document.PageWidth;
            DocumentViewer.Height = ViewModel.Document.PageHeight;
        }
    }

    /// <summary>
    /// 最初のページへ移動
    /// </summary>
    private void FirstPageButton_Click(object sender, RoutedEventArgs e)
    {
        DocumentViewer.FirstPage();
    }

    /// <summary>
    /// 前のページへ移動
    /// </summary>
    private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        DocumentViewer.PreviousPage();
    }

    /// <summary>
    /// 次のページへ移動
    /// </summary>
    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        DocumentViewer.NextPage();
    }

    /// <summary>
    /// 最後のページへ移動
    /// </summary>
    private void LastPageButton_Click(object sender, RoutedEventArgs e)
    {
        DocumentViewer.LastPage();
    }

    /// <summary>
    /// キーボードイベントハンドラ（← →キーでページ移動）
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                // 前のページへ
                DocumentViewer.PreviousPage();
                e.Handled = true;
                break;

            case Key.Right:
            case Key.PageDown:
                // 次のページへ
                DocumentViewer.NextPage();
                e.Handled = true;
                break;

            case Key.Home:
                // 最初のページへ
                DocumentViewer.FirstPage();
                e.Handled = true;
                break;

            case Key.End:
                // 最後のページへ
                DocumentViewer.LastPage();
                e.Handled = true;
                break;

            case Key.OemPlus:
            case Key.Add:
                // ズームイン（Ctrl+または+キー）
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ViewModel.ZoomInCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.OemMinus:
            case Key.Subtract:
                // ズームアウト（Ctrl-または-キー）
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ViewModel.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.D0:
            case Key.NumPad0:
                // ズームリセット（Ctrl+0）
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ViewModel.ResetZoomCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>
    /// 閉じるボタン
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
