using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1307: <see cref="SummaryGeneratorCollection"/> の設定および
/// 対象テストクラスへの <c>[Collection]</c> 属性付与を検証する回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICCardManager.Services.SummaryGenerator"/> の静的フィールド <c>_options</c> /
/// <c>_transferStationGroups</c> を変更するテスト（<c>Configure</c> / <c>ResetToDefaults</c> 呼び出し）と、
/// その影響を受けるテストは、必ず <see cref="SummaryGeneratorCollection.Name"/> Collection に
/// 属している必要がある。本テストは将来 Collection 属性の付与漏れを静的に検出する。
/// </para>
/// <para>
/// ランタイム上の並列実行抑止そのもの（xUnit Scheduler の挙動）は単体テストでは検証困難なため、
/// 属性の付与・プロパティ値・命名の整合性を検証することで代替する。
/// </para>
/// </remarks>
public class SummaryGeneratorCollectionConfigurationTests
{
    /// <summary>
    /// <see cref="SummaryGeneratorCollection"/> 自身が <c>CollectionDefinition</c> 属性を持ち、
    /// <c>DisableParallelization = true</c> が設定されていること。
    /// </summary>
    /// <remarks>
    /// xUnit の <c>CollectionDefinitionAttribute</c> / <c>CollectionAttribute</c> は
    /// <c>Name</c> プロパティを公開していないため、<see cref="CustomAttributeData"/> 経由で
    /// コンストラクタ引数として渡された名前を取得する。
    /// </remarks>
    [Fact]
    public void Collection定義が並列実行を無効化していること()
    {
        // Arrange
        var collectionType = typeof(SummaryGeneratorCollection);
        var attributeData = CustomAttributeData.GetCustomAttributes(collectionType)
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        // Assert
        attributeData.Should().NotBeNull(
            "SummaryGeneratorCollection は CollectionDefinition 属性を持つ必要がある");
        var name = attributeData!.ConstructorArguments[0].Value as string;
        name.Should().Be(SummaryGeneratorCollection.Name);
        var disableParallelization = attributeData.NamedArguments
            .FirstOrDefault(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
            .TypedValue.Value;
        disableParallelization.Should().Be(true,
            "DisableParallelization=true でないと静的状態が並列実行で汚染される");
    }

    /// <summary>
    /// 静的状態を変更するテストクラスが <see cref="SummaryGeneratorCollection"/> に属していること。
    /// </summary>
    [Theory]
    [InlineData(typeof(SummaryGeneratorTests))]
    [InlineData(typeof(SummaryGeneratorEdgeCaseTests))]
    [InlineData(typeof(SummaryGeneratorComprehensiveTests))]
    [InlineData(typeof(OrganizationOptionsTests))]
    public void SummaryGenerator関連テストクラスがCollectionに属していること(Type testClass)
    {
        // Act
        var attributeData = CustomAttributeData.GetCustomAttributes(testClass)
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute));

        // Assert
        attributeData.Should().NotBeNull(
            $"{testClass.Name} は [Collection(SummaryGeneratorCollection.Name)] を持つ必要がある");
        var name = attributeData!.ConstructorArguments[0].Value as string;
        name.Should().Be(SummaryGeneratorCollection.Name,
            $"{testClass.Name} の Collection 名が一致しない");
    }

    /// <summary>
    /// Collection 対象のテストクラスが IDisposable を実装し、
    /// Dispose で ResetToDefaults を呼べる形になっていること。
    /// </summary>
    /// <remarks>
    /// 静的状態を確実にクリーンアップするためのベースライン。
    /// 実際の Reset 呼び出しはソースレベルで担保される。
    /// </remarks>
    [Theory]
    [InlineData(typeof(SummaryGeneratorTests))]
    [InlineData(typeof(SummaryGeneratorEdgeCaseTests))]
    [InlineData(typeof(SummaryGeneratorComprehensiveTests))]
    [InlineData(typeof(OrganizationOptionsTests))]
    public void Collection対象テストクラスはIDisposableを実装していること(Type testClass)
    {
        // Act
        var implementsDisposable = typeof(IDisposable).IsAssignableFrom(testClass);

        // Assert
        implementsDisposable.Should().BeTrue(
            $"{testClass.Name} は IDisposable を実装し Dispose で ResetToDefaults を呼ぶ必要がある");
    }
}
