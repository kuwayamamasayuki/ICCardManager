#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICCardManager.Common;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// 内容に合わせる列（<c>Width="Auto"</c> / <c>SizeToCells</c>）を、後から読み込んだ行の内容の幅まで実際に広げる（Issue #2393）
    /// </summary>
    /// <remarks>
    /// <para>
    /// DataGrid は行を読み込むたびにセルを測り、内容に合わせる列の「必要な幅」（<see cref="DataGridLength.DesiredValue"/>）を
    /// 更新する。ところが <c>*</c> 列と並ぶ画面では、必要な幅が増えても「表示する幅」（<see cref="DataGridLength.DisplayValue"/>）が
    /// 最初のレイアウトの値のまま据え置かれることがある。バス停名未入力の一覧では、利用者の列が
    /// 必要な幅 105.2 に対して表示する幅 100（<c>MinWidth</c>）のままで、「博多 花子 外2名」の末尾が切れていた
    /// （表示スケール 150% の実機で計測）。
    /// </para>
    /// <para>
    /// 行の読み込み（<see cref="DataGrid.LoadingRow"/>）の後、レイアウトが済んだ時点（<see cref="DispatcherPriority.Loaded"/>）で、
    /// 必要な幅が表示する幅を上回る列だけ、表示する幅を必要な幅へ引き上げる。列幅の種別（Auto）は変えないので、
    /// 以後も内容に合わせて広がる。広げた分は <c>*</c> 列が譲る。狭める方向には動かさない（DataGrid の Auto 列と同じ）。
    /// 職員が列の境界をドラッグして幅を決めた列は種別が固定幅に変わるので、対象から外れる。
    /// </para>
    /// <para>
    /// 引き上げの契機は行の読み込みだけである。行の読み込みを伴わずに必要な幅だけが増える変化
    /// （開いたままの画面で文字サイズの設定が変わる等）では引き上げない。ほかの一覧で使うときは、
    /// その画面にこの種の変化が無いかを確かめること（バス停名未入力の一覧はモーダルで、開いたまま文字サイズは変わらない）。
    /// </para>
    /// <para>
    /// 列幅を固定値にする案は採らない。氏名は最大 50 文字で、文字サイズ（小〜特大）でも必要な幅が変わるため、
    /// どの固定値でも切れる組み合わせが残る。
    /// </para>
    /// </remarks>
    public static class DataGridAutoColumnFit
    {
        /// <summary>
        /// 内容に合わせる列を、読み込んだ行の内容の幅まで広げるか
        /// </summary>
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(DataGridAutoColumnFit),
            new PropertyMetadata(false, OnIsEnabledChanged));

        /// <summary>
        /// 引き上げを予約済みか（同じレイアウトで読み込まれる複数の行の分を 1 回にまとめる）
        /// </summary>
        private static readonly DependencyProperty IsFitPendingProperty = DependencyProperty.RegisterAttached(
            "IsFitPending",
            typeof(bool),
            typeof(DataGridAutoColumnFit),
            new PropertyMetadata(false));

        /// <summary>
        /// 必要な幅が表示する幅をこれ以下しか上回らないときは、誤差として広げない
        /// </summary>
        private const double Tolerance = 0.01;

        public static bool GetIsEnabled(DependencyObject element)
        {
            if (element == null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            return (bool)element.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject element, bool value)
        {
            if (element == null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            element.SetValue(IsEnabledProperty, value);
        }

        /// <summary>
        /// 必要な幅が表示する幅を上回る「内容に合わせる列」の表示する幅を、必要な幅（<c>MaxWidth</c> まで）へ引き上げる
        /// </summary>
        /// <param name="columns">対象の列</param>
        /// <returns>1 列でも広げたら true</returns>
        public static bool FitToDesiredWidth(IEnumerable<DataGridColumn> columns)
        {
            if (columns == null)
            {
                throw new ArgumentNullException(nameof(columns));
            }

            var changed = false;
            foreach (var column in columns)
            {
                var width = column.Width;
                if (!width.IsAuto && !width.IsSizeToCells)
                {
                    continue;
                }

                var desired = Math.Min(width.DesiredValue, column.MaxWidth);
                if (double.IsNaN(desired) || double.IsInfinity(desired) || desired <= width.DisplayValue + Tolerance)
                {
                    continue;
                }

                column.Width = new DataGridLength(width.Value, width.UnitType, desired, desired);
                changed = true;
            }

            return changed;
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not DataGrid grid)
            {
                return;
            }

            grid.LoadingRow -= OnLoadingRow;
            if ((bool)e.NewValue)
            {
                grid.LoadingRow += OnLoadingRow;
            }
        }

        private static void OnLoadingRow(object? sender, DataGridRowEventArgs e)
        {
            if (sender is DataGrid grid)
            {
                ScheduleFit(grid);
            }
        }

        /// <summary>
        /// レイアウトが済んだ後に <see cref="FitToDesiredWidth"/> を 1 回実行するよう予約する（予約済みなら何もしない）
        /// </summary>
        internal static void ScheduleFit(DataGrid grid)
            => ScheduleFit(grid, columns => FitToDesiredWidth(columns));

        /// <summary>
        /// <see cref="ScheduleFit(DataGrid)"/> の本体。予約した処理が呼ぶ引き上げを差し替えられる（テスト用の継ぎ目）
        /// </summary>
        /// <remarks>
        /// DataGrid に属する列は列幅を書き換えた時点で計算し直されるため、テストでは「据え置かれた」状態を作れず、
        /// 予約した処理が実際に引き上げたかを列幅で観測できない。予約した処理が一覧の列を渡して引き上げを呼ぶことは、
        /// この継ぎ目で確かめる。
        /// </remarks>
        internal static void ScheduleFit(DataGrid grid, Action<IEnumerable<DataGridColumn>> fit)
        {
            if ((bool)grid.GetValue(IsFitPendingProperty))
            {
                return;
            }

            grid.SetValue(IsFitPendingProperty, true);

            // セルを測り終えるのはこの後のレイアウト（Render 優先度）。それより低い Loaded 優先度で引き上げる
            grid.Dispatcher.InvokeAsyncObserved(
                () =>
                {
                    grid.SetValue(IsFitPendingProperty, false);
                    fit(grid.Columns);
                },
                "一覧の列幅を内容に合わせる",
                DispatcherPriority.Loaded);
        }
    }
}
