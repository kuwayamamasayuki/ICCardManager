using System;

namespace ICCardManager.Common
{
    /// <summary>
    /// 表示用フォーマットユーティリティ
    /// </summary>
    /// <remarks>
    /// Issue #1024: コードベース全体に散在していた金額・日時フォーマットパターンを集約。
    /// </remarks>
    public static class DisplayFormatters
    {
        /// <summary>
        /// 金額を「{value:N0}円」形式でフォーマット（nullable版）
        /// </summary>
        /// <param name="value">金額（nullの場合はfallbackを返す）</param>
        /// <param name="fallback">null時の代替文字列（デフォルト: 空文字）</param>
        /// <returns>フォーマット済み金額文字列</returns>
        public static string FormatAmountWithUnit(int? value, string fallback = "")
        {
            return value.HasValue ? $"{value.Value:N0}円" : fallback;
        }

        /// <summary>
        /// 金額を「{value:N0}円」形式でフォーマット（0は非表示、int版）
        /// </summary>
        /// <param name="value">金額（0の場合はfallbackを返す）</param>
        /// <param name="fallback">0時の代替文字列（デフォルト: 空文字）</param>
        /// <returns>フォーマット済み金額文字列</returns>
        public static string FormatAmountWithUnitOrEmpty(int value, string fallback = "")
        {
            return value > 0 ? $"{value:N0}円" : fallback;
        }

        /// <summary>
        /// 金額を「{value:N0}」形式（円なし）でフォーマット（0は非表示）
        /// </summary>
        /// <param name="value">金額（0の場合はfallbackを返す）</param>
        /// <param name="fallback">0時の代替文字列（デフォルト: 空文字）</param>
        /// <returns>フォーマット済み金額文字列</returns>
        public static string FormatAmountOrEmpty(int value, string fallback = "")
        {
            return value > 0 ? $"{value:N0}" : fallback;
        }

        /// <summary>
        /// 残額を「{value:N0}」形式（円なし）でフォーマット（常に表示）
        /// </summary>
        /// <param name="value">残額</param>
        /// <returns>フォーマット済み残額文字列</returns>
        public static string FormatBalance(int value)
        {
            return $"{value:N0}";
        }

        /// <summary>
        /// 残額を「¥{value:N0}」形式でフォーマット
        /// </summary>
        /// <param name="value">残額</param>
        /// <returns>フォーマット済み残額文字列</returns>
        public static string FormatBalanceWithYenPrefix(int value)
        {
            return $"¥{value:N0}";
        }

        /// <summary>
        /// 残額を「{value:N0}円」形式でフォーマット（常に表示）
        /// </summary>
        /// <param name="value">残額</param>
        /// <returns>フォーマット済み残額文字列</returns>
        public static string FormatBalanceWithUnit(int value)
        {
            return $"{value:N0}円";
        }

        /// <summary>
        /// 日付を「yyyy/MM/dd」形式でフォーマット
        /// </summary>
        /// <param name="date">日付</param>
        /// <returns>フォーマット済み日付文字列</returns>
        public static string FormatDate(DateTime date)
        {
            return date.ToString("yyyy/MM/dd");
        }

        /// <summary>
        /// 日付を「yyyy/MM/dd」形式でフォーマット（nullable版）
        /// </summary>
        /// <param name="date">日付（nullの場合はfallbackを返す）</param>
        /// <param name="fallback">null時の代替文字列（デフォルト: "-"）</param>
        /// <returns>フォーマット済み日付文字列</returns>
        public static string FormatDate(DateTime? date, string fallback = "-")
        {
            return date?.ToString("yyyy/MM/dd") ?? fallback;
        }

        /// <summary>
        /// 日時を「yyyy/MM/dd HH:mm」形式でフォーマット
        /// </summary>
        /// <param name="date">日時</param>
        /// <returns>フォーマット済み日時文字列</returns>
        public static string FormatDateTime(DateTime date)
        {
            return date.ToString("yyyy/MM/dd HH:mm");
        }

        /// <summary>
        /// 日時を「yyyy/MM/dd HH:mm」形式でフォーマット（nullable版）
        /// </summary>
        /// <param name="date">日時（nullの場合はfallbackを返す）</param>
        /// <param name="fallback">null時の代替文字列（デフォルト: "-"）</param>
        /// <returns>フォーマット済み日時文字列</returns>
        public static string FormatDateTime(DateTime? date, string fallback = "-")
        {
            return date?.ToString("yyyy/MM/dd HH:mm") ?? fallback;
        }

        /// <summary>
        /// 日時を「yyyy/MM/dd HH:mm:ss」形式でフォーマット
        /// </summary>
        /// <param name="date">日時</param>
        /// <returns>フォーマット済み日時文字列（秒まで）</returns>
        public static string FormatTimestamp(DateTime date)
        {
            return date.ToString("yyyy/MM/dd HH:mm:ss");
        }
    }
}
