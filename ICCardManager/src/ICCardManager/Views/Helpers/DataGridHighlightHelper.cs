using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// DataGrid行のハイライト表示ユーティリティ
    /// </summary>
    /// <remarks>
    /// 新規登録・更新・復元後に該当行をスクロール表示し、
    /// 薄い黄色(#FFF9C4)で一瞬ハイライトしてフェードアウトさせる。
    /// </remarks>
    public static class DataGridHighlightHelper
    {
        private static readonly Color HighlightColor = Color.FromRgb(0xFF, 0xF9, 0xC4);

        /// <summary>
        /// 指定アイテムの行をスクロール表示し、黄色ハイライト→フェードアウトする
        /// </summary>
        /// <param name="dataGrid">対象のDataGrid</param>
        /// <param name="item">ハイライト対象のアイテム</param>
        /// <param name="durationSeconds">フェードアウトの秒数（デフォルト2秒）</param>
        public static void HighlightRow(DataGrid dataGrid, object item, double durationSeconds = 2.0)
        {
            dataGrid.ScrollIntoView(item);
            dataGrid.UpdateLayout();

            var row = dataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            if (row == null) return;

            var brush = new SolidColorBrush(HighlightColor);
            row.Background = brush;

            var animation = new ColorAnimation
            {
                From = HighlightColor,
                To = Colors.Transparent,
                Duration = new Duration(TimeSpan.FromSeconds(durationSeconds)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            animation.Completed += (s, e) => row.ClearValue(DataGridRow.BackgroundProperty);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }
    }
}
