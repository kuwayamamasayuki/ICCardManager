using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Common.Charting;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Microsoft.Win32;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 管理者ダッシュボードの運用状況一覧に適用する絞り込み（Issue #1692）
    /// </summary>
    public enum AdminDashboardCardFilter
    {
        /// <summary>すべてのカード</summary>
        All,

        /// <summary>貸出中のみ</summary>
        Lent,

        /// <summary>長期未返却（督促対象）のみ</summary>
        LongTermUnreturned,

        /// <summary>残額不足のみ</summary>
        LowBalance,

        /// <summary>当月の帳票が未出力のもののみ</summary>
        ReportNotExported
    }

    /// <summary>
    /// 残高推移グラフに描画するカードの選択肢（Issue #1692）
    /// </summary>
    public partial class BalanceSeriesOption : ObservableObject
    {
        /// <summary>カードIDm</summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>表示用のカード名</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>グラフに描画するかどうか</summary>
        [ObservableProperty]
        private bool isSelected;
    }

    /// <summary>
    /// 残高推移グラフの 1 本の折れ線（Issue #1692）
    /// </summary>
    public class BalanceLine
    {
        /// <summary>系列名（カード名）</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>線の色のリソースキー名</summary>
        public string BrushKey { get; set; } = string.Empty;

        /// <summary>折れ線の頂点（欠測で分断された 1 セグメント分）</summary>
        public PointCollection Points { get; set; } = new PointCollection();

        /// <summary>頂点に置くマーカー</summary>
        public IReadOnlyList<ChartPoint> Markers { get; set; } = new ChartPoint[0];
    }

    /// <summary>
    /// グラフの凡例 1 項目（Issue #1692）
    /// </summary>
    public class ChartLegendItem
    {
        /// <summary>凡例のラベル</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>色のリソースキー名</summary>
        public string BrushKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// 管理者ダッシュボード画面の ViewModel（Issue #1692）
    /// </summary>
    /// <remarks>
    /// <para>
    /// グラフは外部ライブラリを使わず Canvas へ自前描画するため、座標は
    /// <see cref="ChartGeometryCalculator"/> の純粋関数で求めてここに保持する。
    /// プロット領域は固定ピクセルの定数とし、全体を <c>ScrollViewer</c> で包む。
    /// 実描画サイズに追随させると ViewModel が <c>ActualWidth</c> を知る必要が生じ、
    /// UI を起動しないと検証できなくなるため。
    /// </para>
    /// <para>
    /// 台帳は 6 年分保持されるため、利用分析は画面を開いた時点では集計せず、
    /// 分析タブを最初に開いたときに遅延ロードする。
    /// </para>
    /// </remarks>
    public partial class AdminDashboardViewModel : ViewModelBase
    {
        private readonly IAdminDashboardService _adminDashboardService;
        private readonly AdminDashboardExcelExportService _excelExportService;
        private readonly IDialogService _dialogService;
        private readonly ISafeFileLauncher _safeFileLauncher;

        #region グラフの描画領域（固定ピクセル）

        /// <summary>稼働状況グラフのカード名ラベル欄の幅</summary>
        internal const double UtilizationLabelWidth = 170;

        /// <summary>稼働状況グラフの 1 カードあたりの行の高さ</summary>
        internal const double UtilizationRowHeight = 28;

        /// <summary>稼働状況グラフ全体の幅</summary>
        internal const double UtilizationChartWidth = 720;

        /// <summary>稼働状況グラフの右余白（100% のラベルがはみ出さない幅）</summary>
        internal const double UtilizationRightPadding = 50;

        /// <summary>推移グラフ全体の幅</summary>
        internal const double TrendChartWidth = 860;

        /// <summary>推移グラフ全体の高さ</summary>
        internal const double TrendChartHeight = 270;

        /// <summary>推移グラフの Y 軸ラベル欄の幅</summary>
        internal const double TrendAxisLabelWidth = 72;

        /// <summary>推移グラフの X 軸ラベル欄の高さ</summary>
        internal const double TrendAxisLabelHeight = 26;

        /// <summary>推移グラフの上余白</summary>
        internal const double TrendTopPadding = 10;

        /// <summary>推移グラフの右余白</summary>
        internal const double TrendRightPadding = 16;

        /// <summary>棒グラフの目安の目盛り本数</summary>
        internal const int TargetTickCount = 5;

        /// <summary>X 軸に表示するラベルの最大本数（特大文字でも重ならない本数）</summary>
        internal const int MaxXAxisLabels = 8;

        /// <summary>折れ線のマーカーの直径</summary>
        internal const double MarkerSize = 7;

        /// <summary>棒とスロットの隙間比率</summary>
        internal const double BarGapRatio = 0.25;

        /// <summary>
        /// 系列の色として使うリソースキー。色相差を確保できる 5 色に限る。
        /// </summary>
        /// <remarks>
        /// 色値リテラルは使わず、AccessibilityStyles.xaml のブラシキーを
        /// ResourceKeyToBrushConverter 経由で解決する（Issue #1392、#1461 の方針）。
        /// </remarks>
        internal static readonly string[] SeriesBrushKeys =
        {
            "PrimaryBrush", "SuccessActionBrush", "WarningActionBrush", "DangerTextBrush", "InfoTextBrush"
        };

        #endregion

        #region 状態

        /// <summary>集計の基準日時</summary>
        [ObservableProperty]
        private DateTime asOf = DateTime.Now;

        /// <summary>運用状況の集計結果</summary>
        [ObservableProperty]
        private AdminDashboardOperationStatus operationStatus;

        /// <summary>絞り込み後のカード一覧</summary>
        public ObservableCollection<AdminDashboardCardStatus> FilteredCards { get; } = new();

        /// <summary>一覧に適用する絞り込み</summary>
        [ObservableProperty]
        private AdminDashboardCardFilter selectedFilter = AdminDashboardCardFilter.All;

        /// <summary>長期未返却と判定する日数</summary>
        [ObservableProperty]
        private int longTermUnreturnedDays = AppConstants.LongTermUnreturnedDays;

        /// <summary>長期未返却しきい値の選択肢</summary>
        public IReadOnlyList<int> LongTermUnreturnedDayOptions => AppConstants.LongTermUnreturnedDayOptions;

        /// <summary>利用分析の集計期間（か月）</summary>
        [ObservableProperty]
        private int analysisMonths = AppConstants.AdminDashboardDefaultMonths;

        /// <summary>利用分析の集計期間の選択肢</summary>
        public IReadOnlyList<int> AnalysisMonthOptions { get; } = new[] { 3, 6, 12, 24, 36 };

        /// <summary>利用分析の集計結果</summary>
        [ObservableProperty]
        private AdminDashboardAnalytics analytics;

        /// <summary>利用分析を読み込み済みかどうか</summary>
        [ObservableProperty]
        private bool isAnalyticsLoaded;

        /// <summary>ステータス表示のメッセージ</summary>
        [ObservableProperty]
        private string statusMessage = string.Empty;

        /// <summary>ステータスがエラーかどうか</summary>
        [ObservableProperty]
        private bool isStatusError;

        /// <summary>直近に出力した Excel ファイルのパス</summary>
        [ObservableProperty]
        private string lastExportedFile = string.Empty;

        #endregion

        #region グラフの描画データ

        /// <summary>稼働状況グラフの横棒</summary>
        public ObservableCollection<ChartBar> UtilizationBars { get; } = new();

        /// <summary>稼働状況グラフのカード名ラベル（Y 座標つき）</summary>
        public ObservableCollection<ChartAxisTick> UtilizationCategoryLabels { get; } = new();

        /// <summary>稼働状況グラフの X 軸目盛り</summary>
        public ObservableCollection<ChartAxisTick> UtilizationAxisTicks { get; } = new();

        /// <summary>稼働状況グラフの高さ（カード数に比例）</summary>
        [ObservableProperty]
        private double utilizationChartHeight = UtilizationRowHeight;

        /// <summary>月別利用額グラフの積み上げ棒</summary>
        public ObservableCollection<ChartBar> UsageBars { get; } = new();

        /// <summary>月別利用額グラフの Y 軸目盛り</summary>
        public ObservableCollection<ChartAxisTick> UsageAxisTicks { get; } = new();

        /// <summary>月別利用額グラフの X 軸ラベル</summary>
        public ObservableCollection<ChartAxisTick> UsageMonthLabels { get; } = new();

        /// <summary>月別利用額グラフの凡例</summary>
        public ObservableCollection<ChartLegendItem> UsageLegend { get; } = new();

        /// <summary>残高推移グラフの折れ線</summary>
        public ObservableCollection<BalanceLine> BalanceLines { get; } = new();

        /// <summary>残高推移グラフの Y 軸目盛り</summary>
        public ObservableCollection<ChartAxisTick> BalanceAxisTicks { get; } = new();

        /// <summary>残高推移グラフの X 軸ラベル</summary>
        public ObservableCollection<ChartAxisTick> BalanceMonthLabels { get; } = new();

        /// <summary>残高推移グラフに描画するカードの選択肢</summary>
        public ObservableCollection<BalanceSeriesOption> BalanceSeriesOptions { get; } = new();

        /// <summary>推移グラフの幅（XAML の Canvas にバインドする）</summary>
        public double TrendCanvasWidth => TrendChartWidth;

        /// <summary>推移グラフの高さ（XAML の Canvas にバインドする）</summary>
        public double TrendCanvasHeight => TrendChartHeight;

        /// <summary>稼働状況グラフの幅（XAML の Canvas にバインドする）</summary>
        public double UtilizationCanvasWidth => UtilizationChartWidth;

        #endregion

        public AdminDashboardViewModel(
            IAdminDashboardService adminDashboardService,
            AdminDashboardExcelExportService excelExportService,
            IDialogService dialogService,
            ISafeFileLauncher safeFileLauncher)
        {
            _adminDashboardService = adminDashboardService;
            _excelExportService = excelExportService;
            _dialogService = dialogService;
            _safeFileLauncher = safeFileLauncher;
        }

        #region 読み込み

        /// <summary>
        /// 運用状況を読み込む。画面を開いたときと「更新」ボタンから呼ばれる。
        /// </summary>
        /// <remarks>
        /// 利用分析はここでは読み込まない（6 年分の集計を開いた瞬間に走らせないため）。
        /// </remarks>
        [RelayCommand]
        public async Task LoadOperationStatusAsync()
        {
            using (BeginBusy("運用状況を集計中..."))
            {
                try
                {
                    AsOf = DateTime.Now;
                    OperationStatus = await _adminDashboardService.GetOperationStatusAsync(AsOf, LongTermUnreturnedDays);
                    ApplyFilter();
                    SetStatus(BuildOperationSummary(OperationStatus), false);
                }
                catch (Exception ex)
                {
                    // Issue #1614: 生の ex.Message を UI に出さず、3要素準拠の文言を表示。技術詳細はログへ逃がす。
                    ErrorDialogHelper.LogException(ex, "運用状況の集計");
                    SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "運用状況の集計"), true);
                }
            }
        }

        /// <summary>
        /// 利用分析を読み込む。分析タブを最初に開いたときと期間変更時に呼ばれる。
        /// </summary>
        [RelayCommand]
        public async Task LoadAnalyticsAsync()
        {
            using (BeginBusy("利用分析を集計中..."))
            {
                try
                {
                    var toDate = AsOf.Date;
                    var fromDate = new DateTime(toDate.Year, toDate.Month, 1).AddMonths(-(AnalysisMonths - 1));

                    Analytics = await _adminDashboardService.GetAnalyticsAsync(fromDate, toDate, AsOf);
                    IsAnalyticsLoaded = true;

                    RebuildBalanceSeriesOptions(Analytics);
                    RenderUtilizationChart(Analytics);
                    RenderUsageChart(Analytics);
                    RenderBalanceChart(Analytics);

                    SetStatus($"{AnalysisMonths}か月分（{fromDate:yyyy/MM}〜{toDate:yyyy/MM}）を集計しました", false);
                }
                catch (Exception ex)
                {
                    ErrorDialogHelper.LogException(ex, "利用分析の集計");
                    SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "利用分析の集計"), true);
                }
            }
        }

        /// <summary>
        /// 分析タブが選択されたときに一度だけ集計する。
        /// </summary>
        [RelayCommand]
        public async Task EnsureAnalyticsLoadedAsync()
        {
            if (IsAnalyticsLoaded)
            {
                return;
            }

            await LoadAnalyticsAsync();
        }

        /// <summary>
        /// 運用状況の要約文（スクリーンリーダー向けのアナウンスも兼ねる）。
        /// </summary>
        internal static string BuildOperationSummary(AdminDashboardOperationStatus status)
        {
            if (status == null)
            {
                return string.Empty;
            }

            return $"対象{status.TotalCardCount}枚: 貸出中{status.LentCardCount}／"
                + $"長期未返却{status.LongTermUnreturnedCount}／残額不足{status.LowBalanceCount}／"
                + $"帳票未出力{status.ReportNotExportedCount}";
        }

        partial void OnLongTermUnreturnedDaysChanged(int value)
        {
            // しきい値を変えたら再集計する。判定は集計サービス側で行っているため
            // 一覧の絞り込みだけでは反映されない。
            if (OperationStatus != null)
            {
                _ = LoadOperationStatusAsync();
            }
        }

        partial void OnSelectedFilterChanged(AdminDashboardCardFilter value) => ApplyFilter();

        #endregion

        #region 絞り込み

        /// <summary>
        /// 現在の絞り込みを一覧に適用する。
        /// </summary>
        internal void ApplyFilter()
        {
            FilteredCards.Clear();
            if (OperationStatus == null)
            {
                return;
            }

            foreach (var card in FilterCards(OperationStatus.Cards, SelectedFilter))
            {
                FilteredCards.Add(card);
            }
        }

        /// <summary>
        /// 絞り込みの判定（純粋関数）。
        /// </summary>
        internal static IEnumerable<AdminDashboardCardStatus> FilterCards(
            IEnumerable<AdminDashboardCardStatus> cards, AdminDashboardCardFilter filter)
        {
            var source = cards ?? Enumerable.Empty<AdminDashboardCardStatus>();

            switch (filter)
            {
                case AdminDashboardCardFilter.Lent:
                    return source.Where(c => c.IsLent);
                case AdminDashboardCardFilter.LongTermUnreturned:
                    return source.Where(c => c.IsLongTermUnreturned);
                case AdminDashboardCardFilter.LowBalance:
                    return source.Where(c => c.IsBalanceWarning);
                case AdminDashboardCardFilter.ReportNotExported:
                    return source.Where(c => c.ReportState == ReportExportState.NotExported);
                default:
                    return source;
            }
        }

        #endregion

        #region グラフの描画

        private void RenderUtilizationChart(AdminDashboardAnalytics source)
        {
            UtilizationBars.Clear();
            UtilizationCategoryLabels.Clear();
            UtilizationAxisTicks.Clear();

            var items = (source?.Utilizations ?? new CardUtilizationItem[0])
                .Take(AppConstants.AdminDashboardUtilizationChartMaxCards)
                .ToList();

            UtilizationChartHeight = Math.Max(UtilizationRowHeight, items.Count * UtilizationRowHeight);
            if (items.Count == 0)
            {
                return;
            }

            var area = new ChartPlotArea(
                UtilizationLabelWidth,
                0,
                UtilizationChartWidth - UtilizationLabelWidth - UtilizationRightPadding,
                UtilizationChartHeight);

            // 稼働率は 0〜100% の固定スケール。カードごとに軸が変わると比較できないため
            var scale = new AxisScale(0.0, 1.0, 0.25, 5);

            foreach (var bar in ChartGeometryCalculator.CalculateHorizontalBars(
                items.Select(i => i.UtilizationRate).ToList(), area, scale, BarGapRatio, SeriesBrushKeys[0]))
            {
                UtilizationBars.Add(bar);
            }

            var centers = ChartGeometryCalculator.CalculateCategoryCentersY(items.Count, area);
            for (var i = 0; i < items.Count; i++)
            {
                UtilizationCategoryLabels.Add(new ChartAxisTick(i, centers[i], items[i].DisplayName));
            }

            for (var i = 0; i < scale.TickCount; i++)
            {
                var value = scale.Min + (scale.TickInterval * i);
                var x = ChartScale.MapToPixel(value, scale.Min, scale.Max, area.Left, area.Right);
                UtilizationAxisTicks.Add(new ChartAxisTick(value, x, ChartScale.FormatPercentLabel(value)));
            }
        }

        private void RenderUsageChart(AdminDashboardAnalytics source)
        {
            UsageBars.Clear();
            UsageAxisTicks.Clear();
            UsageMonthLabels.Clear();
            UsageLegend.Clear();

            var series = source?.UsageSeries ?? new MonthlyUsageSeries[0];
            var labels = source?.MonthLabels ?? new string[0];
            if (series.Count == 0 || labels.Count == 0)
            {
                return;
            }

            var area = CreateTrendPlotArea();

            // 積み上げなので軸の上限は「月ごとの合計の最大値」で決める
            var valuesByMonth = new List<IReadOnlyList<double>>(labels.Count);
            var monthTotals = new List<double>(labels.Count);
            for (var monthIndex = 0; monthIndex < labels.Count; monthIndex++)
            {
                var column = series
                    .Select(s => monthIndex < s.MonthlyExpenses.Count ? (double)s.MonthlyExpenses[monthIndex] : 0.0)
                    .ToList();
                valuesByMonth.Add(column);
                monthTotals.Add(column.Sum());
            }

            var scale = ChartScale.CreateLinearScale(0.0, monthTotals.Count > 0 ? monthTotals.Max() : 0.0, TargetTickCount);

            foreach (var bar in ChartGeometryCalculator.CalculateStackedVerticalBars(
                valuesByMonth, area, scale, BarGapRatio, SeriesBrushKeys))
            {
                UsageBars.Add(bar);
            }

            foreach (var tick in ChartGeometryCalculator.CalculateYAxisTicks(scale, area, ChartScale.FormatAmountLabel))
            {
                UsageAxisTicks.Add(tick);
            }

            foreach (var tick in ChartGeometryCalculator.CalculateXAxisLabels(labels, area, MaxXAxisLabels))
            {
                UsageMonthLabels.Add(tick);
            }

            for (var i = 0; i < series.Count; i++)
            {
                UsageLegend.Add(new ChartLegendItem
                {
                    Label = series[i].Name,
                    BrushKey = SeriesBrushKeys[i % SeriesBrushKeys.Length]
                });
            }
        }

        private void RenderBalanceChart(AdminDashboardAnalytics source)
        {
            BalanceLines.Clear();
            BalanceAxisTicks.Clear();
            BalanceMonthLabels.Clear();

            var labels = source?.MonthLabels ?? new string[0];
            var selectedIdms = BalanceSeriesOptions.Where(o => o.IsSelected).Select(o => o.CardIdm).ToList();
            var selected = (source?.BalanceSeries ?? new MonthlyBalanceSeries[0])
                .Where(s => selectedIdms.Contains(s.CardIdm))
                .Take(AppConstants.AdminDashboardMaxSeries)
                .ToList();

            if (labels.Count == 0 || selected.Count == 0)
            {
                return;
            }

            var area = CreateTrendPlotArea();

            var maxBalance = selected
                .SelectMany(s => s.MonthlyBalances)
                .Where(v => v.HasValue)
                .Select(v => v.Value)
                .DefaultIfEmpty(0.0)
                .Max();
            var scale = ChartScale.CreateLinearScale(0.0, maxBalance, TargetTickCount);

            for (var i = 0; i < selected.Count; i++)
            {
                var brushKey = SeriesBrushKeys[i % SeriesBrushKeys.Length];
                var segments = ChartGeometryCalculator.CalculateLineSegments(
                    selected[i].MonthlyBalances, area, scale, MarkerSize);

                foreach (var segment in segments)
                {
                    BalanceLines.Add(new BalanceLine
                    {
                        DisplayName = selected[i].DisplayName,
                        BrushKey = brushKey,
                        Points = new PointCollection(segment.Select(p => new Point(p.X, p.Y))),
                        Markers = segment
                    });
                }
            }

            foreach (var tick in ChartGeometryCalculator.CalculateYAxisTicks(scale, area, ChartScale.FormatAmountLabel))
            {
                BalanceAxisTicks.Add(tick);
            }

            foreach (var tick in ChartGeometryCalculator.CalculateXAxisLabels(labels, area, MaxXAxisLabels))
            {
                BalanceMonthLabels.Add(tick);
            }
        }

        private static ChartPlotArea CreateTrendPlotArea()
            => new ChartPlotArea(
                TrendAxisLabelWidth,
                TrendTopPadding,
                TrendChartWidth - TrendAxisLabelWidth - TrendRightPadding,
                TrendChartHeight - TrendTopPadding - TrendAxisLabelHeight);

        /// <summary>
        /// 残高推移グラフのカード選択肢を作り直す。
        /// </summary>
        /// <remarks>
        /// 初期状態では残高の少ないカードから順に上限数まで選択する。
        /// 全カードを既定で描くと線が重なって読めず、色相差も確保できないため。
        /// </remarks>
        private void RebuildBalanceSeriesOptions(AdminDashboardAnalytics source)
        {
            foreach (var option in BalanceSeriesOptions)
            {
                option.PropertyChanged -= OnBalanceSeriesOptionChanged;
            }

            BalanceSeriesOptions.Clear();

            var series = source?.BalanceSeries ?? new MonthlyBalanceSeries[0];
            var defaultSelection = series
                .OrderBy(s => s.MonthlyBalances.LastOrDefault(v => v.HasValue) ?? double.MaxValue)
                .Take(AppConstants.AdminDashboardMaxSeries)
                .Select(s => s.CardIdm)
                .ToList();

            foreach (var s in series)
            {
                var option = new BalanceSeriesOption
                {
                    CardIdm = s.CardIdm,
                    DisplayName = s.DisplayName,
                    IsSelected = defaultSelection.Contains(s.CardIdm)
                };
                option.PropertyChanged += OnBalanceSeriesOptionChanged;
                BalanceSeriesOptions.Add(option);
            }
        }

        private void OnBalanceSeriesOptionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BalanceSeriesOption.IsSelected))
            {
                RenderBalanceChart(Analytics);
            }
        }

        #endregion

        #region Excel 出力

        /// <summary>
        /// 集計結果を Excel ファイルへ出力する。
        /// </summary>
        [RelayCommand]
        public async Task ExportToExcelAsync()
        {
            if (OperationStatus == null)
            {
                SetStatus("集計が完了していません。更新ボタンを押してから出力してください。", true);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "Excel ファイル (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                FileName = $"管理者ダッシュボード_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await ExportToExcelFileAsync(dialog.FileName);
        }

        /// <summary>
        /// 指定ファイルへの Excel 出力本体。<see cref="ExportToExcelAsync"/> からファイル選択後に呼ばれるほか、
        /// 単体テストから <see cref="SaveFileDialog"/> を介さずに実行する経路としても使用する。
        /// </summary>
        internal async Task ExportToExcelFileAsync(string filePath)
        {
            string errorMessage = null;
            using (BeginBusy("エクスポート中..."))
            {
                try
                {
                    await _excelExportService.ExportAsync(OperationStatus, Analytics, filePath);
                    LastExportedFile = filePath;
                    SetStatus("エクスポート完了", false);
                }
                catch (Exception ex)
                {
                    // Issue #1614: 生の ex.Message を UI に出さず、3要素準拠の文言を表示。技術詳細はログへ逃がす。
                    ErrorDialogHelper.LogException(ex, "管理者ダッシュボードのエクスポート");
                    errorMessage = ExceptionMessageFormatter.ToUserMessage(ex, "管理者ダッシュボードのエクスポート");
                    SetStatus(errorMessage, true);
                }
            }

            // Issue #1383: BeginBusy スコープを抜けて IsBusy=false が確定した後にダイアログを表示する。
            if (errorMessage != null)
            {
                _dialogService.ShowError(errorMessage, "エクスポートエラー");
            }
        }

        /// <summary>
        /// 出力した Excel ファイルを開く。
        /// </summary>
        [RelayCommand]
        public void OpenExportedFile()
        {
            // Issue #1465: 拡張子ホワイトリスト経由で安全に起動
            var result = _safeFileLauncher.LaunchFile(LastExportedFile);
            if (!result.Success)
            {
                SetStatus(result.ErrorMessage, true);
            }
        }

        #endregion

        private void SetStatus(string message, bool isError)
        {
            StatusMessage = message;
            IsStatusError = isError;
        }
    }
}
