using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Xunit;

using System.Linq;
using System.Reflection;

namespace ICCardManager.Tests.Infrastructure.Caching;

/// <summary>
/// CacheKeysの単体テスト（Issue #1135）
/// キャッシュキー定数の値と一貫性を検証
/// </summary>
public class CacheKeysTests
{
    #region プレフィックス規則テスト

    [Fact]
    public void AllCards_CardPrefixで始まること()
    {
        CacheKeys.AllCards.Should().StartWith(CacheKeys.CardPrefixForInvalidation,
            "カード関連キーはCardPrefixで始まるべき");
    }

    [Fact]
    public void LentCards_CardPrefixで始まること()
    {
        CacheKeys.LentCards.Should().StartWith(CacheKeys.CardPrefixForInvalidation);
    }

    [Fact]
    public void AvailableCards_CardPrefixで始まること()
    {
        CacheKeys.AvailableCards.Should().StartWith(CacheKeys.CardPrefixForInvalidation);
    }

    [Fact]
    public void AllStaff_StaffPrefixで始まること()
    {
        CacheKeys.AllStaff.Should().StartWith(CacheKeys.StaffPrefixForInvalidation,
            "職員関連キーはStaffPrefixで始まるべき");
    }

    [Fact]
    public void AppSettings_SettingsPrefixで始まること()
    {
        CacheKeys.AppSettings.Should().StartWith(CacheKeys.SettingsPrefixForInvalidation,
            "設定関連キーはSettingsPrefixで始まるべき");
    }

    #endregion

    #region キーの一意性テスト

    [Fact]
    public void すべてのキーが一意であること()
    {
        // Arrange: publicなstring定数フィールドをすべて取得
        var keyFields = typeof(CacheKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.IsLiteral)
            .Select(f => new { f.Name, Value = (string)f.GetValue(null) })
            .ToList();

        // Assert: 値が重複していないこと
        var duplicates = keyFields
            .GroupBy(k => k.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' が {string.Join(", ", g.Select(k => k.Name))} で重複")
            .ToList();

        duplicates.Should().BeEmpty("すべてのキャッシュキーは一意であるべき");
    }

    #endregion

    #region プレフィックス値テスト

    [Fact]
    public void CardPrefixForInvalidation_コロンで終わること()
    {
        CacheKeys.CardPrefixForInvalidation.Should().EndWith(":",
            "InvalidateByPrefixで使うためコロンで終わるべき");
    }

    [Fact]
    public void StaffPrefixForInvalidation_コロンで終わること()
    {
        CacheKeys.StaffPrefixForInvalidation.Should().EndWith(":");
    }

    [Fact]
    public void SettingsPrefixForInvalidation_コロンで終わること()
    {
        CacheKeys.SettingsPrefixForInvalidation.Should().EndWith(":");
    }

    #endregion

    #region 具体値テスト

    [Fact]
    public void AllCards_期待される値であること()
    {
        CacheKeys.AllCards.Should().Be("card:all");
    }

    [Fact]
    public void LentCards_期待される値であること()
    {
        CacheKeys.LentCards.Should().Be("card:lent");
    }

    [Fact]
    public void AvailableCards_期待される値であること()
    {
        CacheKeys.AvailableCards.Should().Be("card:available");
    }

    [Fact]
    public void AllStaff_期待される値であること()
    {
        CacheKeys.AllStaff.Should().Be("staff:all");
    }

    [Fact]
    public void AppSettings_期待される値であること()
    {
        CacheKeys.AppSettings.Should().Be("settings:app");
    }

    #endregion
}
