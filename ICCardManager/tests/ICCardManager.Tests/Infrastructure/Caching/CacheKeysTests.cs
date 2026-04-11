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

    #region 識別子含有テスト

    // プレフィックス規則+一意性だけでは「AllCardsとLentCardsの値が入れ替わる」誤編集を
    // 検出できないため、各キーがその意味を表す識別子を含むことを検証する。
    // (リテラル値そのものの検証ではなく、意味的整合性の検証)

    [Fact]
    public void AllCards_キー名にallを含むこと()
    {
        CacheKeys.AllCards.Should().Contain("all", "全カード一覧のキーなのでallを含むべき");
    }

    [Fact]
    public void LentCards_キー名にlentを含むこと()
    {
        CacheKeys.LentCards.Should().Contain("lent", "貸出中カードのキーなのでlentを含むべき");
    }

    [Fact]
    public void AvailableCards_キー名にavailableを含むこと()
    {
        CacheKeys.AvailableCards.Should().Contain("available", "利用可能カードのキーなのでavailableを含むべき");
    }

    [Fact]
    public void AllStaff_キー名にallを含むこと()
    {
        CacheKeys.AllStaff.Should().Contain("all");
    }

    #endregion
}
