using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

public class AppConstantsTests
{
    [Fact]
    public void SystemName_ShouldBeExpectedValue()
    {
        AppConstants.SystemName.Should().Be("交通系ICカード管理システム：ピッすい");
    }

    [Fact]
    public void SystemName_ShouldStartWithBaseName()
    {
        AppConstants.SystemName.Should().StartWith("交通系ICカード管理システム");
    }

    [Fact]
    public void SystemName_ShouldContainNickname()
    {
        AppConstants.SystemName.Should().Contain("ピッすい");
    }
}
