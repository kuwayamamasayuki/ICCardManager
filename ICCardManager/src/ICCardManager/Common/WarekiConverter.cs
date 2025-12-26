using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;

namespace ICCardManager.Common
{
/// <summary>
    /// 西暦と和暦の変換を行うユーティリティクラス
    /// </summary>
    public static class WarekiConverter
    {
        private static readonly JapaneseCalendar JapaneseCalendar = new();
        private static readonly CultureInfo JapaneseCulture = new("ja-JP");

        /// <summary>
        /// 元号の略称マッピング
        /// </summary>
        private static readonly Dictionary<int, string> EraAbbreviations = new()
        {
            { 1, "M" },  // 明治
            { 2, "T" },  // 大正
            { 3, "S" },  // 昭和
            { 4, "H" },  // 平成
            { 5, "R" }   // 令和
        };

        /// <summary>
        /// DateTime を和暦文字列に変換します
        /// </summary>
        /// <param name="date">変換する日付</param>
        /// <returns>和暦文字列 (例: "R7.11.05")</returns>
        public static string ToWareki(DateTime date)
        {
            try
            {
                var era = JapaneseCalendar.GetEra(date);
                var year = JapaneseCalendar.GetYear(date);
                var month = date.Month;
                var day = date.Day;

                var eraAbbr = EraAbbreviations.TryGetValue(era, out var abbr) ? abbr : "?";

                return $"{eraAbbr}{year}.{month:D2}.{day:D2}";
            }
            catch
            {
                // 変換できない場合は西暦形式で返す
                return date.ToString("yyyy.MM.dd");
            }
        }

        /// <summary>
        /// DateTime を和暦文字列に変換します（年月のみ）
        /// </summary>
        /// <param name="date">変換する日付</param>
        /// <returns>和暦文字列 (例: "R7年11月")</returns>
        public static string ToWarekiYearMonth(DateTime date)
        {
            try
            {
                var era = JapaneseCalendar.GetEra(date);
                var year = JapaneseCalendar.GetYear(date);
                var month = date.Month;

                var eraAbbr = EraAbbreviations.TryGetValue(era, out var abbr) ? abbr : "?";

                return $"{eraAbbr}{year}年{month}月";
            }
            catch
            {
                return date.ToString("yyyy年M月");
            }
        }

        /// <summary>
        /// 和暦文字列を DateTime に変換します
        /// </summary>
        /// <param name="wareki">和暦文字列 (例: "R7.11.05")</param>
        /// <returns>変換されたDateTime、変換失敗時はnull</returns>
        public static DateTime? FromWareki(string wareki)
        {
            if (string.IsNullOrWhiteSpace(wareki))
            {
                return null;
            }

            try
            {
                // "R7.11.05" 形式をパース
                var eraChar = char.ToUpperInvariant(wareki[0]);
                var parts = wareki.Substring(1).Split('.');

                if (parts.Length != 3)
                {
                    return null;
                }

                if (!int.TryParse(parts[0], out var year) ||
                    !int.TryParse(parts[1], out var month) ||
                    !int.TryParse(parts[2], out var day))
                {
                    return null;
                }

                var era = eraChar switch
                {
                    'M' => 1,
                    'T' => 2,
                    'S' => 3,
                    'H' => 4,
                    'R' => 5,
                    _ => 0
                };

                if (era == 0)
                {
                    return null;
                }

                return JapaneseCalendar.ToDateTime(year, month, day, 0, 0, 0, 0, era);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 指定された日付が属する年度を取得します
        /// </summary>
        /// <param name="date">日付</param>
        /// <returns>年度（4月始まり）</returns>
        public static int GetFiscalYear(DateTime date)
        {
            return date.Month >= 4 ? date.Year : date.Year - 1;
        }

        /// <summary>
        /// 指定された年度の開始日を取得します
        /// </summary>
        /// <param name="fiscalYear">年度</param>
        /// <returns>年度開始日（4月1日）</returns>
        public static DateTime GetFiscalYearStart(int fiscalYear)
        {
            return new DateTime(fiscalYear, 4, 1);
        }

        /// <summary>
        /// 指定された年度の終了日を取得します
        /// </summary>
        /// <param name="fiscalYear">年度</param>
        /// <returns>年度終了日（翌年3月31日）</returns>
        public static DateTime GetFiscalYearEnd(int fiscalYear)
        {
            return new DateTime(fiscalYear + 1, 3, 31);
        }
    }
}
