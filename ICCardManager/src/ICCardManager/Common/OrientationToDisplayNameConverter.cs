using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using System.Printing;
using System.Windows.Data;

namespace ICCardManager.Common
{
/// <summary>
    /// PageOrientationを日本語表示名に変換するコンバーター
    /// </summary>
    public class OrientationToDisplayNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PageOrientation orientation)
            {
                return orientation switch
                {
                    PageOrientation.Landscape => "横向き（A4横）",
                    PageOrientation.Portrait => "縦向き（A4縦）",
                    PageOrientation.ReverseLandscape => "横向き（逆）",
                    PageOrientation.ReversePortrait => "縦向き（逆）",
                    _ => orientation.ToString()
                };
            }

            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
