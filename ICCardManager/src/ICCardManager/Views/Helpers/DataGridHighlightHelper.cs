using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// DataGrid行のハイライト表示ユーティリティ
    /// </summary>
    /// <remarks>
    /// 新規登録・更新・復元後に該当行をスクロール表示し、
    /// 薄い黄色（AccessibilityStyles.xaml の <c>RowHighlightColor</c>: #FFF9C4）で
    /// 一瞬ハイライトしてフェードアウトさせる。色値は SSOT を参照する（Issue #1392/#1461/#1613）。
    /// </remarks>
    public static class DataGridHighlightHelper
    {
        /// <summary>
        /// ハイライト色を AccessibilityStyles.xaml の <c>RowHighlightColor</c> リソースから解決する。
        /// ColorAnimation は Brush ではなく Color を補間するため、Brush キーではなく Color リソースを参照する。
        /// </summary>
        private static Color GetHighlightColor()
            => (Color)Application.Current.FindResource("RowHighlightColor");

        /// <summary>
        /// 指定アイテムの行をスクロール表示し、黄色ハイライト→フェードアウトする
        /// </summary>
        /// <param name="dataGrid">対象のDataGrid</param>
        /// <param name="item">ハイライト対象のアイテム</param>
        /// <param name="durationSeconds">フェードアウトの秒数（デフォルト2秒）</param>
        /// <param name="onCompleted">アニメーション完了時のコールバック（省略可）</param>
        public static void HighlightRow(DataGrid dataGrid, object item, double durationSeconds = 2.0, Action onCompleted = null)
        {
            // 選択を一旦解除して選択スタイルの干渉を防ぐ
            dataGrid.SelectedItem = null;

            dataGrid.ScrollIntoView(item);
            dataGrid.UpdateLayout();

            var row = dataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            if (row == null)
            {
                // コンテナがまだ未生成の場合、低優先度で再試行
                dataGrid.Dispatcher.InvokeAsync(() =>
                {
                    dataGrid.ScrollIntoView(item);
                    dataGrid.UpdateLayout();
                    var retryRow = dataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                    if (retryRow != null)
                    {
                        AnimateRow(retryRow, durationSeconds, onCompleted);
                    }
                }, DispatcherPriority.ContextIdle);
                return;
            }

            AnimateRow(row, durationSeconds, onCompleted);
        }

        /// <summary>
        /// 行の背景色アニメーションを実行
        /// </summary>
        private static void AnimateRow(DataGridRow row, double durationSeconds, Action onCompleted = null)
        {
            var highlightColor = GetHighlightColor();
            var brush = new SolidColorBrush(highlightColor);
            row.Background = brush;

            var animation = new ColorAnimation
            {
                From = highlightColor,
                To = Colors.Transparent,
                Duration = new Duration(TimeSpan.FromSeconds(durationSeconds)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            animation.Completed += (s, e) =>
            {
                row.ClearValue(DataGridRow.BackgroundProperty);
                onCompleted?.Invoke();
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
    }
}
