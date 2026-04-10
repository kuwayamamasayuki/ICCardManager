using System;

namespace ICCardManager.Common.ValueObjects
{
    /// <summary>
    /// 日本の会計年度（4月始まり）を表すValue Object
    /// </summary>
    /// <remarks>
    /// FiscalYearHelper の静的メソッド群をインスタンスメソッドとして自然に表現する。
    /// 例: new FiscalYear(2024).StartDate → 2024年4月1日
    /// </remarks>
    public readonly struct FiscalYear : IEquatable<FiscalYear>, IComparable<FiscalYear>
    {
        /// <summary>
        /// 年度を表す西暦年（例: 2024年度 = 2024年4月～2025年3月）
        /// </summary>
        public int Year { get; }

        public FiscalYear(int year)
        {
            if (year < 1 || year > 9999)
                throw new ArgumentOutOfRangeException(nameof(year), year, "年度は1〜9999の範囲で指定してください");
            Year = year;
        }

        /// <summary>
        /// 年度の開始日（4月1日）
        /// </summary>
        public DateTime StartDate => new DateTime(Year, 4, 1);

        /// <summary>
        /// 年度の終了日（翌年3月31日）
        /// </summary>
        public DateTime EndDate => new DateTime(Year + 1, 3, 31);

        /// <summary>
        /// 指定した日付がこの年度に含まれるか
        /// </summary>
        public bool Contains(DateTime date) => date >= StartDate && date <= EndDate.AddDays(1).AddTicks(-1);

        /// <summary>
        /// 指定された日付が属する年度を取得
        /// </summary>
        public static FiscalYear FromDate(DateTime date) =>
            new FiscalYear(date.Month >= 4 ? date.Year : date.Year - 1);

        /// <summary>
        /// 指定された年・月が属する年度を取得
        /// </summary>
        public static FiscalYear FromYearMonth(int year, int month) =>
            new FiscalYear(month >= 4 ? year : year - 1);

        /// <summary>
        /// 前月の年・月を取得
        /// </summary>
        public static (int Year, int Month) GetPreviousMonth(int year, int month)
        {
            if (month == 1)
                return (year - 1, 12);
            return (year, month - 1);
        }

        public static implicit operator int(FiscalYear fy) => fy.Year;
        public static explicit operator FiscalYear(int year) => new FiscalYear(year);

        public bool Equals(FiscalYear other) => Year == other.Year;
        public override bool Equals(object obj) => obj is FiscalYear other && Equals(other);
        public override int GetHashCode() => Year;
        public override string ToString() => $"{Year}年度";

        public int CompareTo(FiscalYear other) => Year.CompareTo(other.Year);

        public static bool operator ==(FiscalYear left, FiscalYear right) => left.Equals(right);
        public static bool operator !=(FiscalYear left, FiscalYear right) => !left.Equals(right);
        public static bool operator <(FiscalYear left, FiscalYear right) => left.Year < right.Year;
        public static bool operator >(FiscalYear left, FiscalYear right) => left.Year > right.Year;
        public static bool operator <=(FiscalYear left, FiscalYear right) => left.Year <= right.Year;
        public static bool operator >=(FiscalYear left, FiscalYear right) => left.Year >= right.Year;
    }
}
