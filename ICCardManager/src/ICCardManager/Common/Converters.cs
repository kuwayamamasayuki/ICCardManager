using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ICCardManager.Common
{
    /// <summary>
    /// リソースキー文字列を <see cref="Brush"/> に解決するコンバーター（Issue #1461）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// SSOT 原則: ViewModel/DTO がカラーリテラル（例: "#FFEBEE"）を返してしまうと、
    /// テーマ切替や <c>AccessibilityStyles.xaml</c> の値変更に追従できない。
    /// このコンバーターは「キー名」（例: "ErrorBackgroundBrush"）を受け取り、
    /// <see cref="Application.Current"/> のリソースから <see cref="Brush"/> を解決する。
    /// </para>
    /// <para>
    /// null / 空文字列 / "Transparent" は <see cref="Brushes.Transparent"/> を返す。
    /// 解決に失敗した場合も <see cref="Brushes.Transparent"/> を返し、UI を破綻させない。
    /// </para>
    /// </remarks>
    public class ResourceKeyToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value as string;
            if (string.IsNullOrEmpty(key) || string.Equals(key, "Transparent", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Transparent;
            }

            if (Application.Current?.TryFindResource(key) is Brush brush)
            {
                return brush;
            }

            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 整数値をVisibilityに変換するコンバーター
    /// 0より大きい場合はVisible、それ以外はCollapsed
    /// </summary>
    public class IntToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                return intValue > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 真偽値を反転するコンバーター
    /// </summary>
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }

    /// <summary>
    /// 真偽値をVisibilityに変換するコンバーター
    /// trueの場合はVisible、falseの場合はCollapsed
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                // parameterが指定されている場合は反転
                var invert = parameter?.ToString()?.ToLower() == "invert";
                var isVisible = invert ? !boolValue : boolValue;
                return isVisible ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 文字列をVisibilityに変換するコンバーター
    /// 空でない文字列の場合はVisible、null/空の場合はCollapsed
    /// </summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var str = value as string;
            return string.IsNullOrEmpty(str) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 件数をスクリーンリーダー向けの自然な日本語テキストに変換するコンバーター（Issue #1278）。
    /// 0件の場合は空リストを示す説明文、それ以外は件数を含む簡潔な説明文を返す。
    /// </summary>
    /// <remarks>
    /// ConverterParameter で主語を指定できる（例: "登録カード"、"職員"、"履歴"）。
    /// 省略時は「項目」となる。
    ///
    /// 例:
    ///   - 値=5, parameter="登録カード" → "登録カードは5件です"
    ///   - 値=0, parameter="登録カード" → "登録カードはまだありません。新規登録してください"
    ///   - 値=1, parameter="職員"      → "職員は1件です"
    /// </remarks>
    public class CountToAccessibilityTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var subject = parameter as string;
            if (string.IsNullOrWhiteSpace(subject))
            {
                subject = "項目";
            }

            int count;
            if (value is int intValue)
            {
                count = intValue;
            }
            else if (value != null && int.TryParse(value.ToString(), out var parsed))
            {
                count = parsed;
            }
            else
            {
                return string.Empty;
            }

            if (count <= 0)
            {
                return $"{subject}はまだありません。新規登録してください";
            }

            return $"{subject}は{count}件です";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// ファイルサイズを人間が読みやすい形式に変換するコンバーター
    /// 例: 1024 → "1 KB", 1048576 → "1 MB"
    /// </summary>
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                string[] sizes = { "B", "KB", "MB", "GB" };
                int order = 0;
                double size = bytes;
                while (size >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    size /= 1024;
                }
                return $"{size:0.##} {sizes[order]}";
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
