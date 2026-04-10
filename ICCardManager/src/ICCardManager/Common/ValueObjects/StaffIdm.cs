using System;
using System.Text.RegularExpressions;

namespace ICCardManager.Common.ValueObjects
{
    /// <summary>
    /// 職員証のIDm（製造ID）を表すValue Object
    /// </summary>
    /// <remarks>
    /// IDmは16進数16文字（8バイト）の文字列。大文字で正規化される。
    /// CardIdmとは型レベルで区別される。
    /// </remarks>
    public readonly struct StaffIdm : IEquatable<StaffIdm>
    {
        private static readonly Regex HexPattern = new Regex(@"^[0-9A-Fa-f]{16}$", RegexOptions.Compiled);

        private readonly string _value;

        /// <summary>
        /// 生の文字列値
        /// </summary>
        public string Value => _value ?? string.Empty;

        /// <summary>
        /// 有効なIDmかどうか
        /// </summary>
        public bool IsValid => !string.IsNullOrEmpty(_value);

        public StaffIdm(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                _value = null;
                return;
            }

            var upper = value.ToUpperInvariant();
            if (!HexPattern.IsMatch(upper))
                throw new ArgumentException($"StaffIdmは16進数16文字である必要があります: '{value}'", nameof(value));

            _value = upper;
        }

        /// <summary>
        /// バリデーションなしで内部的に生成（DBからの読み取り時など、既に検証済みの値に使用）
        /// </summary>
        internal static StaffIdm FromTrusted(string value)
        {
            if (string.IsNullOrEmpty(value))
                return default;
            return new StaffIdm(value);
        }

        public static implicit operator string(StaffIdm idm) => idm.Value;
        public static explicit operator StaffIdm(string value) => new StaffIdm(value);

        public bool Equals(StaffIdm other) =>
            string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) =>
            obj is StaffIdm other && Equals(other);

        public override int GetHashCode() =>
            _value != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(_value) : 0;

        public override string ToString() => Value;

        public static bool operator ==(StaffIdm left, StaffIdm right) => left.Equals(right);
        public static bool operator !=(StaffIdm left, StaffIdm right) => !left.Equals(right);
    }
}
