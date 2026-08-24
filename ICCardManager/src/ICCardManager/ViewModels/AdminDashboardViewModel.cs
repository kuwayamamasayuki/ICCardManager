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
    /// グラフの代替一覧（DataGrid）の 1 行（Issue #1856）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 03_画面設計書 §3.23.4 の「各グラフの直下に同じ内容の一覧を必ず併置する」を満たすための行。
    /// 色・図形だけに依存せず、グレースケール印刷やスクリーンリーダーからも同じ内容を読める。
    /// </para>
    /// <para>
    /// Excel 出力（行＝年月・列＝系列）と違い「年月・系列・値」の縦持ちにしている。
    /// 集計期間が 3〜36 か月で可変なため列を横に並べると動的な列生成が要り、
    /// 36 列の表はそもそも読めない。縦持ちなら 1 行が自己完結し読み上げにも適する。
    /// </para>
    /// </remarks>
    public class ChartTableRow
    {
        /// <summary>年月ラベル（"yyyy/MM"）</summary>
        public string MonthLabel { get; set; } = string.Empty;

        /// <summary>系列名（職員名／カード名。月別利用額では「その他」を含む）</summary>
        public string SeriesName { get; set; } = string.Empty;

        /// <summary>
        /// その月の値（利用額または月末残高）。
        /// 残高で取引開始前の月は null（0 を入れると「残高が 0 になった」と誤読される）。
        /// </summary>
        public double? Value { get; set; }

        /// <summary>
        /// 一覧に表示する金額文字列。値が無い月（取引開始前）は空欄。
        /// </summary>
        /// <remarks>
        /// XAML の <c>StringFormat="{}{0:N0}円"</c> に null を渡すと単位の「円」だけが残り、
        /// 「残高が 0 円」とも「値が無い」とも読めない行になる（設計書 §3.23.4 は空欄と定めている）。
        /// 書式の適用有無を WPF のバインディングの挙動に委ねず、ここで確定させる。
        /// 数値としての並べ替えは <see cref="Value"/> を <c>SortMemberPath</c> に指定して保つ。
        /// </remarks>
        public string ValueText => Value.HasValue ? Value.Value.ToString("N0") + "円" : string.Empty;
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
        /// <para>
        /// 色値リテラルは使わず、AccessibilityStyles.xaml のブラシキーを
        /// ResourceKeyToBrushConverter 経由で解決する（Issue #1392、#1461 の方針）。
        /// </para>
        /// <para>
        /// グラフ系列専用のキー（<c>ChartSeries*Brush</c>）を使い、業務画面の意味色
        /// （<c>PrimaryBrush</c> / <c>SuccessActionBrush</c> 等）は流用しない（Issue #1855）。
        /// 意味色は「成功＝緑・警告＝橙・危険＝赤」という意味を担うため色数を自由に増やせず、
        /// 流用していた頃は 1 番目（<c>PrimaryBrush</c> #1976D2）と 5 番目
        /// （<c>InfoTextBrush</c> #1565C0）の ΔE が 7.1 しかなく肉眼では同色だった。
        /// </para>
        /// <para>
        /// 並び順は「先頭から n 色を採ったときの識別性」で決めている。系列が 5 本に満たない
        /// 期間では先頭から順に消費されるため、先に来る色ほど互いに離れている必要がある。
        /// </para>
        /// </remarks>
        internal static readonly string[] SeriesBrushKeys =
        {
            "ChartSeries1Brush", "ChartSeries2Brush", "ChartSeries3Brush", "ChartSeries4Brush", "ChartSeries5Brush"
        };

        /// <summary>
        /// 「その他」系列に固定で割り当てる色のリソースキー（Issue #1815）。
        /// </summary>
        /// <remarks>
        /// 上位系列の <see cref="SeriesBrushKeys"/> とは別枠で持つ。剰余で選ぶと
        /// 6 系列目（その他）が最上位系列と同色になり、積み上げ棒でも凡例でも
        /// 区別できなくなるため。集約された残りであることが色からも伝わるよう無彩色を充てる。
        /// 旧 <c>MutedTextBrush</c>（#666666）は、当時 5 番目の系列だった
        /// <c>InfoTextBrush</c>（#1565C0）と相対輝度がほぼ同一（0.133 / 0.133）で、
        /// 積み上げ棒で隣接するとグレースケール印刷・ロービジョン・第三色覚異常の
        /// いずれでも境界が見えなかった（Issue #1855）。
        /// </remarks>
        internal const string OtherSeriesBrushKey = "ChartSeriesOtherBrush";

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

        /// <summary>月別利用額グラフの代替一覧（Issue #1856）</summary>
        public ObservableCollection<ChartTableRow> UsageTableRows { get; } = new();

        /// <summary>残高推移グラフの折れ線</summary>
        public ObservableCollection<BalanceLine> BalanceLines { get; } = new();

        /// <summary>残高推移グラフの Y 軸目盛り</summary>
        public ObservableCollection<ChartAxisTick> BalanceAxisTicks { get; } = new();

        /// <summary>残高推移グラフの X 軸ラベル</summary>
        public ObservableCollection<ChartAxisTick> BalanceMonthLabels { get; } = new();

        /// <summary>残高推移グラフに描画するカードの選択肢</summary>
        public ObservableCollection<BalanceSeriesOption> BalanceSeriesOptions { get; } = new();

        /// <summary>残高推移グラフの代替一覧（Issue #1856）</summary>
        public ObservableCollection<ChartTableRow> BalanceTableRows { get; } = new();

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

        /// <summary>
        /// サマリータイルのクリックから一覧の絞り込みを切り替える。
        /// </summary>
        [RelayCommand]
        public void SetFilter(AdminDashboardCardFilter filter) => SelectedFilter = filter;

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
            UsageTableRows.Clear();

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

            // 棒と凡例が食い違わないよう、色は系列ごとに 1 度だけ決めて双方で使う
            var brushKeys = BuildUsageSeriesBrushKeys(series);

            foreach (var bar in ChartGeometryCalculator.CalculateStackedVerticalBars(
                valuesByMonth, area, scale, BarGapRatio, brushKeys))
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
                    BrushKey = brushKeys[i]
                });
            }

            // 代替一覧はグラフと同じメソッドで作る。別メソッドに分けると、
            // 系列の絞り込みや並びを片方だけ変えたときに「同じ内容」でなくなる（Issue #1856）。
            // 並びは積み上げ棒の読み取り順（月ごとに系列が積み上がる）へ揃える
            for (var monthIndex = 0; monthIndex < labels.Count; monthIndex++)
            {
                for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                {
                    UsageTableRows.Add(new ChartTableRow
                    {
                        MonthLabel = labels[monthIndex],
                        SeriesName = series[seriesIndex].Name,
                        Value = valuesByMonth[monthIndex][seriesIndex]
                    });
                }
            }
        }

        /// <summary>
        /// 月別利用額グラフの系列色を、系列と同じ並び・同じ長さで返す（Issue #1815）。
        /// </summary>
        /// <remarks>
        /// 「その他」は <see cref="OtherSeriesBrushKey"/> 固定で、上位系列の色番号としては数えない。
        /// </remarks>
        internal static IReadOnlyList<string> BuildUsageSeriesBrushKeys(IReadOnlyList<MonthlyUsageSeries> series)
        {
            var items = series ?? new MonthlyUsageSeries[0];
            var keys = new List<string>(items.Count);
            var topSeriesIndex = 0;

            foreach (var s in items)
            {
                if (s != null && s.IsOther)
                {
                    keys.Add(OtherSeriesBrushKey);
                    continue;
                }

                keys.Add(SeriesBrushKeys[topSeriesIndex % SeriesBrushKeys.Length]);
                topSeriesIndex++;
            }

            return keys;
        }

        private void RenderBalanceChart(AdminDashboardAnalytics source)
        {
            BalanceLines.Clear();
            BalanceAxisTicks.Clear();
            BalanceMonthLabels.Clear();
            BalanceTableRows.Clear();

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

            // 一覧の母集団は折れ線と同じ selected（チェックボックスの選択と上限の両方が効いた後）。
            // 一覧だけ全件にすると、グラフに描かれていない系列が並んで「同じ内容」でなくなる（Issue #1856）
            for (var monthIndex = 0; monthIndex < labels.Count; monthIndex++)
            {
                foreach (var s in selected)
                {
                    BalanceTableRows.Add(new ChartTableRow
                    {
                        MonthLabel = labels[monthIndex],
                        SeriesName = s.DisplayName,
                        Value = monthIndex < s.MonthlyBalances.Count ? s.MonthlyBalances[monthIndex] : null
                    });
                }
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
