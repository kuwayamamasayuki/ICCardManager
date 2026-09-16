using System.Printing;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.ViewModels;

/// <summary>
/// 印刷プレビューViewModel
/// </summary>
public partial class PrintPreviewViewModel : ViewModelBase
{
    private readonly PrintService _printService;

    /// <summary>
    /// 再生成用の帳票データ（単一または複数カード）
    /// </summary>
    private List<ReportPrintData>? _reportDataList;

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
    /// ドキュメントを設定（単一カード、用紙方向変更時に再生成可能）
    /// </summary>
    public void SetDocument(ReportPrintData reportData, string title)
    {
        _reportDataList = new List<ReportPrintData> { reportData };
        Document = _printService.CreateFlowDocument(reportData, SelectedOrientation);
        DocumentTitle = title;
        CurrentPage = 1;
        InternalUpdatePageCount();
        StatusMessage = $"「{title}」を表示中";
    }

    /// <summary>
    /// ドキュメントを設定（複数カード、用紙方向変更時に再生成可能）
    /// </summary>
    public void SetDocument(List<ReportPrintData> reportDataList, string title)
    {
        _reportDataList = reportDataList;
        Document = _printService.CreateFlowDocumentForMultipleCards(reportDataList, SelectedOrientation);
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
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
                // ドキュメントがまだビジュアルツリーにアタッチされていない場合など
                // ページ数の取得に失敗することがある
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[PrintPreviewVM] ページ数取得エラー: {ex.Message}");
#endif
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
        if (Document == null || _reportDataList == null || _reportDataList.Count == 0)
        {
            StatusMessage = "印刷するドキュメントがありません";
            return;
        }

        // Issue #2047: 印刷ダイアログで確定した用紙寸法でドキュメントを組み直す。
        // プレビュー中の Document を書き換えると、改ページ位置と寸法が食い違ううえ、
        // 印刷後のプレビューにも寸法の変わったドキュメントが残る
        var result = _printService.PrintWithSettings(DocumentTitle, SelectedOrientation, BuildDocument);

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
    /// 帳票データから指定寸法（DIP）のドキュメントを組み立てる
    /// </summary>
    /// <remarks>
    /// プレビュー（用紙方向から決まる A4 寸法）と印刷（印刷ダイアログで確定した寸法）の
    /// 両方がこの 1 か所を通る。改ページ位置は寸法ごとに計算し直される。
    /// </remarks>
    private FlowDocument BuildDocument(System.Windows.Size pageSize)
    {
        return _reportDataList!.Count == 1
            ? _printService.CreateFlowDocument(_reportDataList[0], pageSize)
            : _printService.CreateFlowDocumentForMultipleCards(_reportDataList, pageSize);
    }

    /// <summary>
    /// 用紙方向変更時の処理
    /// </summary>
    partial void OnSelectedOrientationChanged(PageOrientation value)
    {
        // 帳票データがある場合はドキュメントを再生成（行数が変わるため）
        if (_reportDataList != null && _reportDataList.Count > 0)
        {
            Document = BuildDocument(PrintService.GetPageSize(value));

            // 用紙方向変更時は最初のページに戻す
            CurrentPage = 1;
            InternalUpdatePageCount();

            // FlowDocumentPageViewerに再描画を通知
            DocumentNeedsRefresh?.Invoke(this, EventArgs.Empty);

            var orientationName = GetOrientationDisplayName(value);
            StatusMessage = $"用紙方向を{orientationName}に変更しました";
        }
    }
}
