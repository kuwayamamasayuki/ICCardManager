using System;

namespace ICCardManager.Common
{
    /// <summary>
    /// 年度（4月始まり）に関するユーティリティ
    /// </summary>
    /// <remarks>
    /// Issue #1024: WarekiConverter、ReportService、ReportDataBuilder等に散在していた
    /// 年度計算ロジックを集約。日本の会計年度（4月～翌年3月）の計算を一元化する。
    /// </remarks>
    public static class FiscalYearHelper
    {
        /// <summary>
        /// 指定された年・月から年度を取得
        /// </summary>
        /// <param name="year">西暦年</param>
        /// <param name="month">月（1-12）</param>
        /// <returns>年度（例: 2024年4月〜2025年3月 → 2024）</returns>
        public static int GetFiscalYear(int year, int month)
        {
            return month >= 4 ? year : year - 1;
        }

        /// <summary>
        /// 指定された日付が属する年度を取得
        /// </summary>
        /// <param name="date">日付</param>
        /// <returns>年度（4月始まり）</returns>
        public static int GetFiscalYear(DateTime date)
        {
            return GetFiscalYear(date.Year, date.Month);
        }

        /// <summary>
        /// 指定された年度の開始日を取得
        /// </summary>
        /// <param name="fiscalYear">年度</param>
        /// <returns>年度開始日（4月1日）</returns>
        public static DateTime GetFiscalYearStart(int fiscalYear)
        {
            return new DateTime(fiscalYear, 4, 1);
        }

        /// <summary>
        /// 指定された年度の終了日を取得
        /// </summary>
        /// <param name="fiscalYear">年度</param>
        /// <returns>年度終了日（翌年3月31日）</returns>
        public static DateTime GetFiscalYearEnd(int fiscalYear)
        {
            return new DateTime(fiscalYear + 1, 3, 31);
        }

        /// <summary>
        /// 前月の年・月を取得
        /// </summary>
        /// <param name="year">西暦年</param>
        /// <param name="month">月（1-12）</param>
        /// <returns>前月の（年, 月）タプル</returns>
        public static (int Year, int Month) GetPreviousMonth(int year, int month)
        {
            if (month == 1)
            {
                return (year - 1, 12);
            }
            return (year, month - 1);
        }
    }
}
