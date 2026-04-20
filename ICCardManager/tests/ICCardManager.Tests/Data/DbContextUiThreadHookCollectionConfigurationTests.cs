using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Tests.Services;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1372: <see cref="DbContextUiThreadHookCollection"/> の設定および
/// 対象テストクラスへの <c>[Collection]</c> 属性付与を検証する回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICCardManager.Data.DbContext"/> の <c>IsOnUiThread</c> AsyncLocal フックを
/// 書き換えるテストクラスは、並列実行時のレースを避けるため必ず
/// <see cref="DbContextUiThreadHookCollection.Name"/> Collection に属している必要がある。
/// 本テストは将来の付与漏れを静的に検出する。
/// </para>
/// <para>
/// xUnit Scheduler のランタイム上の並列実行抑止そのものは単体テストでは検証困難なため、
/// 属性の付与・プロパティ値・命名の整合性を検証することで代替する。
/// （同一方針の先行事例: <c>SummaryGeneratorCollectionConfigurationTests</c>）
/// </para>
/// </remarks>
public class DbContextUiThreadHookCollectionConfigurationTests
{
    /// <summary>
    /// <see cref="DbContextUiThreadHookCollection"/> 自身が <c>CollectionDefinition</c> 属性を持ち、
    /// <c>DisableParallelization = true</c> が設定されていること。
    /// </summary>
    [Fact]
    public void Collection定義が並列実行を無効化していること()
    {
        var collectionType = typeof(DbContextUiThreadHookCollection);
        var attributeData = CustomAttributeData.GetCustomAttributes(collectionType)
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        attributeData.Should().NotBeNull(
            "DbContextUiThreadHookCollection は CollectionDefinition 属性を持つ必要がある");
        var name = attributeData!.ConstructorArguments[0].Value as string;
        name.Should().Be(DbContextUiThreadHookCollection.Name,
            "CollectionDefinition に渡された名前は定数 Name と一致する必要がある");
        var disableParallelization = attributeData.NamedArguments
            .FirstOrDefault(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
            .TypedValue.Value;
        disableParallelization.Should().Be(true,
            "DisableParallelization=true でないと AsyncLocal フックが並列実行時に干渉しうる");
    }

    /// <summary>
    /// <c>DbContext.IsOnUiThread</c> フックを書き換えるテストクラスが
    /// <see cref="DbContextUiThreadHookCollection"/> に属していること。
    /// </summary>
    /// <remarks>
    /// 新規に同フックを書き換えるテストクラスを追加した場合は、本 Theory の
    /// <c>InlineData</c> にも対象型を追加すること。追加漏れがあっても、
    /// レビュー時に本テストの差分が並ぶことで運用上の気付きとなる。
    /// </remarks>
    [Theory]
    [InlineData(typeof(DbContextUiThreadGuardTests))]
    [InlineData(typeof(BackupServiceUiThreadGuardTests))]
    public void UiThreadフック書換テストクラスがCollectionに属していること(Type testClass)
    {
        var attributeData = CustomAttributeData.GetCustomAttributes(testClass)
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute));

        attributeData.Should().NotBeNull(
            $"{testClass.Name} は [Collection(DbContextUiThreadHookCollection.Name)] を持つ必要がある");
        var name = attributeData!.ConstructorArguments[0].Value as string;
        name.Should().Be(DbContextUiThreadHookCollection.Name,
            $"{testClass.Name} の Collection 名が一致しない（DbContextUiThreadHookCollection.Name を参照すること）");
    }

    /// <summary>
    /// Collection 対象のテストクラスが <see cref="IDisposable"/> を実装していること。
    /// </summary>
    /// <remarks>
    /// <c>Dispose()</c> 内で <c>DbContext.IsOnUiThread</c> を既定値へ戻すことが必須。
    /// これにより、Collection によるシリアル化と併せて、テスト間のフック残存を二重に防ぐ。
    /// 実際の Reset 呼び出しはソースレベルで担保される。
    /// </remarks>
    [Theory]
    [InlineData(typeof(DbContextUiThreadGuardTests))]
    [InlineData(typeof(BackupServiceUiThreadGuardTests))]
    public void Collection対象テストクラスはIDisposableを実装していること(Type testClass)
    {
        var implementsDisposable = typeof(IDisposable).IsAssignableFrom(testClass);

        implementsDisposable.Should().BeTrue(
            $"{testClass.Name} は IDisposable を実装し Dispose でフックを既定値へ戻す必要がある");
    }
}
