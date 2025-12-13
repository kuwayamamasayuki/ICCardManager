using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Caching;

/// <summary>
/// CacheKeysとCacheDurationsのテスト
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

    #region CacheDurations Tests

    [Fact]
    public void CacheDurations_Settings_ShouldBeFiveMinutes()
    {
        // Assert
        CacheDurations.Settings.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void CacheDurations_CardList_ShouldBeOneMinute()
    {
        // Assert
        CacheDurations.CardList.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void CacheDurations_StaffList_ShouldBeOneMinute()
    {
        // Assert
        CacheDurations.StaffList.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void CacheDurations_LentCards_ShouldBeThirtySeconds()
    {
        // Assert
        CacheDurations.LentCards.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void CacheDurations_RelativeOrder_SettingsShouldBeLongestThenCardListThenLentCards()
    {
        // Assert - 設定は最も長く、貸出中カードは最も短い
        CacheDurations.Settings.Should().BeGreaterThan(CacheDurations.CardList);
        CacheDurations.CardList.Should().BeGreaterThan(CacheDurations.LentCards);
    }

    #endregion
}
