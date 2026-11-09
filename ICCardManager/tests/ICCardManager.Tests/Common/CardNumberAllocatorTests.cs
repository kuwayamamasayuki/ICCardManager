#nullable enable
using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2419: 管理番号の採番（「最大＋1」、表せなければ未使用の最小の正の整数）の単体テスト。
/// </summary>
/// <remarks>
/// 新規登録の採番（<c>CardRepository.GetNextCardNumberAsync</c>）と、移行時の重複の番号ずらし
/// （<c>Migration_008.ResolveDuplicates</c>、012 も使う）が共有する。
/// </remarks>
public class CardNumberAllocatorTests
{
    [Fact]
    public void ParseMax_DbNull_IsZero()
    {
        CardNumberAllocator.ParseMax(DBNull.Value).Should().Be(0L, "カードが 1 枚も無い種別は 1 から採番する");
    }

    [Fact]
    public void ParseMax_Int64BeyondInt32_IsKept()
    {
        CardNumberAllocator.ParseMax(12345678901L).Should().Be(12345678901L,
            "CAST(... AS INTEGER) は 64 ビット整数を返す。int へ変換すると OverflowException になる");
    }

    [Fact]
    public void Next_UsualMax_ReturnsMaxPlusOne()
    {
        var next = CardNumberAllocator.Next(5L, () => throw new InvalidOperationException("通常の採番で使用中の番号を読むべきではない"));

        next.Should().Be("6");
    }

    [Fact]
    public void Next_MaxBeyondInt32_ReturnsMaxPlusOne()
    {
        CardNumberAllocator.Next(12345678901L, () => Array.Empty<string>()).Should().Be("12345678902");
    }

    /// <summary>
    /// 最大が long.MaxValue（SQLite の CAST が範囲外の数字列を頭打ちにした値）なら、+1 で折り返さず
    /// 使われていない最小の正の整数を返す。比較は文字列（"01" は "1" を塞がない）。
    /// </summary>
    [Fact]
    public void Next_MaxSaturatesInt64_ReturnsSmallestUnusedPositiveNumber()
    {
        var next = CardNumberAllocator.Next(long.MaxValue, () => new[] { "99999999999999999999", "1", "2", "01", "4" });

        next.Should().Be("3");
    }

    [Fact]
    public void SmallestUnusedPositiveNumber_NoneUsed_IsOne()
    {
        CardNumberAllocator.SmallestUnusedPositiveNumber(Array.Empty<string>()).Should().Be("1");
    }
}
