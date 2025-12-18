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
    /// ページ変更イベントの再帰を防ぐフラグ
    /// </summary>
    private bool _isUpdatingPage;

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
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        // これによりViewerのページ変更をViewModelに同期
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
        if (_isUpdatingPage || DocumentViewer.Document == null) return;

        // MasterPageNumberは0ベース、CurrentPageは1ベース
        int viewerPage = DocumentViewer.MasterPageNumber + 1;

        if (viewerPage != ViewModel.CurrentPage && viewerPage >= 1 && viewerPage <= DocumentViewer.PageCount)
        {
            try
            {
                _isUpdatingPage = true;
                ViewModel.CurrentPage = viewerPage;
            }
            finally
            {
                _isUpdatingPage = false;
            }
        }
    }

    /// <summary>
    /// ウィンドウアンロード時
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // イベント購読解除（メモリリーク防止）
        ViewModel.DocumentNeedsRefresh -= OnDocumentNeedsRefresh;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _masterPageNumberDescriptor?.RemoveValueChanged(DocumentViewer, OnMasterPageNumberChanged);
    }

    /// <summary>
    /// ViewModelのプロパティ変更ハンドラ
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrintPreviewViewModel.CurrentPage))
        {
            // 再帰防止: Viewer→ViewModel同期中はスキップ
            if (_isUpdatingPage) return;

            // ViewModelのCurrentPageが変更されたらビューアーのページを切り替え
            _isUpdatingPage = true;
            GoToPage(ViewModel.CurrentPage);

            // Viewerのページ変更は非同期のため、フラグリセットを遅延させる
            // ContextIdleでUI更新完了後にリセット
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isUpdatingPage = false;
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
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
            UpdatePageCountFromViewer();

            // ドキュメント更新後は最初のページに移動（ViewModelと同期）
            DocumentViewer.FirstPage();
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
        if (DocumentViewer.Document != null)
        {
            // FlowDocumentPageViewerのPageCountプロパティを使用
            var pageCount = DocumentViewer.PageCount;
            if (pageCount > 0 && pageCount != ViewModel.TotalPages)
            {
                ViewModel.TotalPages = pageCount;
            }
        }
    }

    /// <summary>
    /// 指定したページに移動
    /// </summary>
    /// <param name="pageNumber">1から始まるページ番号</param>
    private void GoToPage(int pageNumber)
    {
        if (DocumentViewer.Document == null || pageNumber < 1)
        {
            return;
        }

        // FlowDocumentPageViewerはGoToPageコマンドで直接ページ移動できる
        // ページ番号は1ベース
        if (pageNumber >= 1 && pageNumber <= DocumentViewer.PageCount)
        {
            // NavigationCommands.GoToPageを使用
            if (NavigationCommands.GoToPage.CanExecute(pageNumber, DocumentViewer))
            {
                NavigationCommands.GoToPage.Execute(pageNumber, DocumentViewer);
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
    /// キーボードイベントハンドラ（← →キーでページ移動）
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                // 前のページへ
                if (ViewModel.PreviousPageCommand.CanExecute(null))
                {
                    ViewModel.PreviousPageCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Right:
            case Key.PageDown:
                // 次のページへ
                if (ViewModel.NextPageCommand.CanExecute(null))
                {
                    ViewModel.NextPageCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Home:
                // 最初のページへ
                if (ViewModel.FirstPageCommand.CanExecute(null))
                {
                    ViewModel.FirstPageCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.End:
                // 最後のページへ
                if (ViewModel.LastPageCommand.CanExecute(null))
                {
                    ViewModel.LastPageCommand.Execute(null);
                    e.Handled = true;
                }
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
