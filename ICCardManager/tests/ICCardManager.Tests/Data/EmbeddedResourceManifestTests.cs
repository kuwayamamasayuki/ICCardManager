using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Data.Migrations;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// 本番アセンブリの埋め込みリソース構成を固定するテスト（Issue #1610）。
///
/// schema.sql はかつて EmbeddedResource として登録されていたが、
/// GetManifestResourceStream で読み込む箇所が一切存在せず、
/// DB 構築は Migration_001〜009 が担う「デッドリソース」だった。
/// Issue #1610 で削除したため、再追加されないことをリグレッションとして検証する。
/// あわせて、実際に読み込まれるリソース（駅コードマスタ・帳票テンプレート）が
/// 巻き添えで失われていないことも確認する。
/// </summary>
public class EmbeddedResourceManifestTests
{
    // 本番アセンブリ（ICCardManager.exe 相当）を、確実に同アセンブリに属する型から取得する。
    private static readonly Assembly ProductionAssembly = typeof(Migration_001_Initial).Assembly;

    /// <summary>
    /// schema.sql は埋め込みリソースから削除されており、再追加されていないこと。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ManifestResources_DoesNotContainDeadSchemaSql()
    {
        var resourceNames = ProductionAssembly.GetManifestResourceNames();

        resourceNames.Should().NotContain(
            "ICCardManager.Data.schema.sql",
            "schema.sql は参照されないデッドリソースのため Issue #1610 で削除済み。DB 構築は Migrations/ が担う");
        resourceNames.Should().NotContain(
            name => name.EndsWith("schema.sql", System.StringComparison.OrdinalIgnoreCase),
            "論理名を変えた再追加も防ぐ");
    }

    /// <summary>
    /// 実際に GetManifestResourceStream で読み込まれるリソースは残存していること
    /// （schema.sql 削除の巻き添えでこれらを消していないことの保証）。
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("ICCardManager.Resources.StationCode.csv")]
    [InlineData("ICCardManager.Resources.Templates.物品出納簿テンプレート（市長事務部局）.xlsx")]
    [InlineData("ICCardManager.Resources.Templates.物品出納簿テンプレート（企業会計部局）.xlsx")]
    public void ManifestResources_StillContainsActivelyUsedResource(string expectedResourceName)
    {
        var resourceNames = ProductionAssembly.GetManifestResourceNames();

        resourceNames.Should().Contain(
            expectedResourceName,
            "このリソースは実コードから GetManifestResourceStream で読み込まれるため残存必須");
    }
}
