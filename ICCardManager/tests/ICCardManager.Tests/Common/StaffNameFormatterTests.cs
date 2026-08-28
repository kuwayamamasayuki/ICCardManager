using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// StaffNameFormatter の単体テスト
/// Issue #1906: 複数名で同一交通系ICカードを利用した場合の「外N名」表記
/// </summary>
public class StaffNameFormatterTests
{
    [Fact]
    public void Format_同行者数0_氏名をそのまま返すこと()
    {
        StaffNameFormatter.Format("博多 花子", 0).Should().Be("博多 花子");
    }

    [Fact]
    public void Format_同行者数1_外1名を付けること()
    {
        StaffNameFormatter.Format("博多 花子", 1).Should().Be("博多 花子 外1名");
    }

    [Fact]
    public void Format_同行者数複数_人数を半角数字で付けること()
    {
        StaffNameFormatter.Format("博多 花子", 12).Should().Be("博多 花子 外12名");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Format_氏名が空_外N名だけを返すこと(string staffName)
    {
        StaffNameFormatter.Format(staffName, 2).Should().Be("外2名");
    }

    [Fact]
    public void Format_氏名が空で同行者数0_空文字を返すこと()
    {
        StaffNameFormatter.Format(null, 0).Should().Be(string.Empty);
    }

    [Fact]
    public void Format_負数_ArgumentOutOfRangeExceptionを投げること()
    {
        Action act = () => StaffNameFormatter.Format("博多 花子", -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MaxCompanionCount_上限は99であること()
    {
        StaffNameFormatter.MaxCompanionCount.Should().Be(99);
        StaffNameFormatter.Format("A", StaffNameFormatter.MaxCompanionCount).Should().Be("A 外99名");
    }
}
