using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Infrastructure.Caching;

/// <summary>
/// CacheKeysのテスト
/// </summary>
public class CacheKeysTests
{
    #region CacheKeys Tests

    [Fact]
    public void CacheKeys_AllCards_ShouldHaveCorrectPrefix()
    {
        // Assert
        CacheKeys.AllCards.Should().StartWith("card:");
    }

    [Fact]
    public void CacheKeys_LentCards_ShouldHaveCorrectPrefix()
    {
        // Assert
        CacheKeys.LentCards.Should().StartWith("card:");
    }

    [Fact]
    public void CacheKeys_AvailableCards_ShouldHaveCorrectPrefix()
    {
        // Assert
        CacheKeys.AvailableCards.Should().StartWith("card:");
    }

    [Fact]
    public void CacheKeys_AllStaff_ShouldHaveCorrectPrefix()
    {
        // Assert
        CacheKeys.AllStaff.Should().StartWith("staff:");
    }

    [Fact]
    public void CacheKeys_AppSettings_ShouldHaveCorrectPrefix()
    {
        // Assert
        CacheKeys.AppSettings.Should().StartWith("settings:");
    }

    [Fact]
    public void CacheKeys_CardPrefixForInvalidation_ShouldMatchCardKeys()
    {
        // Assert
        CacheKeys.AllCards.Should().StartWith(CacheKeys.CardPrefixForInvalidation);
        CacheKeys.LentCards.Should().StartWith(CacheKeys.CardPrefixForInvalidation);
        CacheKeys.AvailableCards.Should().StartWith(CacheKeys.CardPrefixForInvalidation);
    }

    [Fact]
    public void CacheKeys_StaffPrefixForInvalidation_ShouldMatchStaffKeys()
    {
        // Assert
        CacheKeys.AllStaff.Should().StartWith(CacheKeys.StaffPrefixForInvalidation);
    }

    [Fact]
    public void CacheKeys_SettingsPrefixForInvalidation_ShouldMatchSettingsKeys()
    {
        // Assert
        CacheKeys.AppSettings.Should().StartWith(CacheKeys.SettingsPrefixForInvalidation);
    }

    #endregion
}
