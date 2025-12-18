using System.Printing;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Services;

namespace ICCardManager.ViewModels;

/// <summary>
/// 印刷プレビューViewModel
/// </summary>
public partial class PrintPreviewViewModel : ViewModelBase
{
    private readonly PrintService _printService;

    /// <summary>
    /// ドキュメントの再描画が必要な場合に発火するイベント
    /// </summary>
    public event EventHandler? DocumentNeedsRefresh;

    /// <summary>
    /// ナビゲーション要求イベント
    /// </summary>
    public event Action? NavigateNextRequested;
    public event Action? NavigatePreviousRequested;
    public event Action? NavigateFirstRequested;
    public event Action? NavigateLastRequested;

    [ObservableProperty]
    private FlowDocument? _document;

    [ObservableProperty]
    private string _documentTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveZoom))]
    private double _zoomLevel = 100;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private PageOrientation _selectedOrientation = PageOrientation.Landscape;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _contentScaleFactor = 1.0;

    /// <summary>
    /// A4横向きの幅（基準サイズ）
    /// </summary>
    private const double LandscapeWidth = 842;

    /// <summary>
    /// A4縦向きの幅
    /// </summary>
    private const double PortraitWidth = 595;

    /// <summary>
    /// コンテンツの自動縮小スケール（1.0 = 100%、縦向き時は小さくなる）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveZoom))]
    private double _contentScale = 1.0;

    /// <summary>
    /// 実効ズーム倍率（ZoomLevel * ContentScale）
    /// XAMLのScaleTransformにバインドする用
    /// </summary>
    public double EffectiveZoom => ZoomLevel * ContentScale;

    /// <summary>
    /// ズーム倍率の選択肢
    /// </summary>
    public double[] ZoomLevels { get; } = { 50, 75, 100, 125, 150, 200 };

    /// <summary>
    /// 用紙方向の選択肢
    /// </summary>
    public PageOrientation[] Orientations { get; } =
    {
        PageOrientation.Landscape,
        PageOrientation.Portrait
    };

    /// <summary>
    /// 最初のページかどうか
    /// </summary>
    public bool IsFirstPage => CurrentPage <= 1;

    /// <summary>
    /// 最後のページかどうか
    /// </summary>
    public bool IsLastPage => CurrentPage >= TotalPages;

    /// <summary>
    /// ページ表示テキスト
    /// </summary>
    public string PageDisplayText => TotalPages > 0
        ? $"{CurrentPage} / {TotalPages} ページ"
        : "ページなし";

    /// <summary>
    /// 用紙方向の表示名を取得
    /// </summary>
    public static string GetOrientationDisplayName(PageOrientation orientation)
    {
        return orientation switch
        {
            PageOrientation.Landscape => "横向き（A4横）",
            PageOrientation.Portrait => "縦向き（A4縦）",
            _ => orientation.ToString()
        };
    }

    public PrintPreviewViewModel(PrintService printService)
    {
        _printService = printService;
    }

    /// <summary>
    /// ドキュメントを設定
    /// </summary>
    public void SetDocument(FlowDocument document, string title)
    {
        Document = document;
        DocumentTitle = title;
        CurrentPage = 1;
        InternalUpdatePageCount();
        StatusMessage = $"「{title}」を表示中";
    }

    /// <summary>
    /// ページ数を再計算（ウィンドウ表示後に呼び出す）
    /// </summary>
    public void RecalculatePageCount()
    {
        InternalUpdatePageCount();
    }

    /// <summary>
    /// ページ数と現在ページを更新（Viewから呼び出し用）
    /// </summary>
    public void UpdatePageCount(int totalPages, int currentPage)
    {
        TotalPages = totalPages > 0 ? totalPages : 1;
        CurrentPage = Math.Max(1, Math.Min(currentPage, TotalPages));

        OnPropertyChanged(nameof(PageDisplayText));
        OnPropertyChanged(nameof(IsFirstPage));
        OnPropertyChanged(nameof(IsLastPage));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        FirstPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// ページ数を更新（内部用）
    /// </summary>
    private void InternalUpdatePageCount()
    {
        if (Document != null)
        {
            try
            {
                var paginator = ((IDocumentPaginatorSource)Document).DocumentPaginator;

                // ページ数計算のためにコンテンツサイズを設定
                paginator.PageSize = new System.Windows.Size(
                    Document.PageWidth,
                    Document.PageHeight);

                // ドキュメントがまだレンダリングされていない場合、PageCountが正確でない可能性がある
                // IsPageCountValidプロパティでチェック
                if (paginator.IsPageCountValid)
                {
                    TotalPages = paginator.PageCount > 0 ? paginator.PageCount : 1;
                }
                else
                {
                    // ページ数が確定していない場合はデフォルト値を設定
                    // ドキュメント表示後に再計算される
                    TotalPages = 1;
                }
            }
            catch (Exception ex)
            {
                // ドキュメントがまだビジュアルツリーにアタッチされていない場合など
                // ページ数の取得に失敗することがある
                System.Diagnostics.Debug.WriteLine($"[PrintPreviewVM] ページ数取得エラー: {ex.Message}");
                TotalPages = 1;
            }
        }
        else
        {
            TotalPages = 0;
        }

        OnPropertyChanged(nameof(PageDisplayText));
        OnPropertyChanged(nameof(IsFirstPage));
        OnPropertyChanged(nameof(IsLastPage));
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(PageDisplayText));
        OnPropertyChanged(nameof(IsFirstPage));
        OnPropertyChanged(nameof(IsLastPage));

        // ページナビゲーションコマンドのCanExecuteを更新
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        FirstPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnTotalPagesChanged(int value)
    {
        OnPropertyChanged(nameof(PageDisplayText));
        OnPropertyChanged(nameof(IsFirstPage));
        OnPropertyChanged(nameof(IsLastPage));

                // ページナビゲーションコマンドのCanExecuteを更新
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        FirstPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 次のページへ
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private void NextPage()
    {
        // Viewに対してナビゲーション要求を発火
        // CurrentPageはViewerのMasterPageNumber変更時に更新される
        NavigateNextRequested?.Invoke();
    }

    private bool CanGoNextPage() => !IsLastPage;

    /// <summary>
    /// 前のページへ
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoPreviousPage))]
    private void PreviousPage()
    {
        NavigatePreviousRequested?.Invoke();
    }

    private bool CanGoPreviousPage() => !IsFirstPage;

    /// <summary>
    /// 最初のページへ
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoFirstPage))]
    private void FirstPage()
    {
        NavigateFirstRequested?.Invoke();
    }

    private bool CanGoFirstPage() => !IsFirstPage;

    /// <summary>
    /// 最後のページへ
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoLastPage))]
    private void LastPage()
    {
        NavigateLastRequested?.Invoke();
    }

    private bool CanGoLastPage() => !IsLastPage;

    /// <summary>
    /// ズームイン
    /// </summary>
    [RelayCommand]
    private void ZoomIn()
    {
        var currentIndex = Array.IndexOf(ZoomLevels, ZoomLevel);
        if (currentIndex < ZoomLevels.Length - 1)
        {
            ZoomLevel = ZoomLevels[currentIndex + 1];
        }
        else if (currentIndex == -1)
        {
            // 現在の値がリストにない場合、次に大きい値を選択
            var nextLevel = ZoomLevels.FirstOrDefault(z => z > ZoomLevel);
            if (nextLevel > 0)
            {
                ZoomLevel = nextLevel;
            }
        }
    }

    /// <summary>
    /// ズームアウト
    /// </summary>
    [RelayCommand]
    private void ZoomOut()
    {
        var currentIndex = Array.IndexOf(ZoomLevels, ZoomLevel);
        if (currentIndex > 0)
        {
            ZoomLevel = ZoomLevels[currentIndex - 1];
        }
        else if (currentIndex == -1)
        {
            // 現在の値がリストにない場合、次に小さい値を選択
            var prevLevel = ZoomLevels.LastOrDefault(z => z < ZoomLevel);
            if (prevLevel > 0)
            {
                ZoomLevel = prevLevel;
            }
        }
    }

    /// <summary>
    /// 100%にリセット
    /// </summary>
    [RelayCommand]
    private void ResetZoom()
    {
        ZoomLevel = 100;
    }

    /// <summary>
    /// 印刷を実行
    /// </summary>
    [RelayCommand]
    private void Print()
    {
        if (Document == null)
        {
            StatusMessage = "印刷するドキュメントがありません";
            return;
        }

        var result = _printService.PrintWithSettings(Document, DocumentTitle, SelectedOrientation);

        if (result)
        {
            StatusMessage = "印刷を開始しました";
        }
        else
        {
            StatusMessage = "印刷がキャンセルされました";
        }
    }

    /// <summary>
    /// 用紙方向変更時の処理
    /// </summary>
    partial void OnSelectedOrientationChanged(PageOrientation value)
    {
        if (Document != null)
        {
            // A4サイズに基づいて用紙サイズを更新
            if (value == PageOrientation.Landscape)
            {
                Document.PageWidth = LandscapeWidth;   // A4横 (約29.7cm)
                Document.PageHeight = PortraitWidth;   // A4横 (約21cm)
                ContentScaleFactor = 1.0;              // 横向きはスケーリングなし
            }
            else
            {
                // 縦向きでもコンテンツは横向きの幅で描画（テーブルが収まるように）
                // ScaleTransformで縮小して縦向き用紙サイズに見せる
                ContentScaleFactor = PortraitWidth / LandscapeWidth;  // ≈ 0.707
                Document.PageWidth = LandscapeWidth;   // 842 (コンテンツが収まる幅)
                // 縮小後にA4縦の高さ(842)になるように設定: 842 / 0.707 ≈ 1191
                Document.PageHeight = LandscapeWidth / ContentScaleFactor;
            }

            // テーブルは比例幅（Star sizing）を使用しているため、
            // 用紙サイズに合わせて自動的にリサイズされる
            ContentScale = 1.0;

            // 用紙方向変更時は最初のページに戻す（ページ数が変わるため）
            CurrentPage = 1;

            InternalUpdatePageCount();

            // FlowDocumentPageViewerに再描画を通知
            DocumentNeedsRefresh?.Invoke(this, EventArgs.Empty);

            var orientationName = GetOrientationDisplayName(value);
            var scaleInfo = value == PageOrientation.Portrait
                ? $"（{ContentScaleFactor:P0}に縮小）"
                : "";
            StatusMessage = $"用紙方向を{orientationName}に変更しました{scaleInfo}";
        }
    }
}
