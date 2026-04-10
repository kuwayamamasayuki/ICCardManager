using System;

namespace ICCardManager.Common.ValueObjects
{
    /// <summary>
    /// 金額を表すValue Object（非負制約）
    /// </summary>
    /// <remarks>
    /// Income（受入）やExpense（払出）など、物品出納簿上の金額に使用する。
    /// 金額は常に0以上。
    /// </remarks>
    public readonly struct Money : IEquatable<Money>, IComparable<Money>
    {
        /// <summary>
        /// 金額（円）
        /// </summary>
        public int Amount { get; }

        /// <summary>
        /// 金額ゼロ
        /// </summary>
        public static readonly Money Zero = new Money(0);

        public Money(int amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "金額は0以上である必要があります");
            Amount = amount;
        }

        /// <summary>
        /// 金額がゼロかどうか
        /// </summary>
        public bool IsZero => Amount == 0;

        public static Money operator +(Money left, Money right) =>
            new Money(left.Amount + right.Amount);

        public static int operator -(Money left, Money right) =>
            left.Amount - right.Amount;

        public static implicit operator int(Money money) => money.Amount;
        public static explicit operator Money(int amount) => new Money(amount);

        public bool Equals(Money other) => Amount == other.Amount;
        public override bool Equals(object obj) => obj is Money other && Equals(other);
        public override int GetHashCode() => Amount;
        public override string ToString() => $"{Amount:#,0}円";

        public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

        public static bool operator ==(Money left, Money right) => left.Equals(right);
        public static bool operator !=(Money left, Money right) => !left.Equals(right);
        public static bool operator <(Money left, Money right) => left.Amount < right.Amount;
        public static bool operator >(Money left, Money right) => left.Amount > right.Amount;
        public static bool operator <=(Money left, Money right) => left.Amount <= right.Amount;
        public static bool operator >=(Money left, Money right) => left.Amount >= right.Amount;
    }
}
