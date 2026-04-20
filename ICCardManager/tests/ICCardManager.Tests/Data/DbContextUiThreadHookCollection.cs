using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1372: <c>DbContext.IsOnUiThread</c> 内部フックを書き換えるテストを
/// シリアル実行させるための xUnit テストコレクション定義。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICCardManager.Data.DbContext"/> の <c>IsOnUiThread</c> は
/// <c>AsyncLocal&lt;Func&lt;bool&gt;?&gt;</c> で保護されている（Issue #1281）ため、
/// 各 ExecutionContext では独立した値を持つ。しかし <c>BackupService</c> 系の
/// 実装は内部で <c>Task.Run</c> を用いて DB 接続リースをバックグラウンドへオフロードし、
/// 親 ExecutionContext の AsyncLocal 値を継承する。この継承チェーンと xUnit の
/// テストクラス並列実行時の ExecutionContext 切り替え境界が相互作用することで、
/// 稀に別テストクラスが書き換えたフックが読み出されるレースが観測されている
/// （CI 上でのフレーキー失敗として顕在化）。
/// </para>
/// <para>
/// 本 Collection に属するテストクラスは <c>DisableParallelization = true</c> により
/// 逐次実行されるため、AsyncLocal 境界のエッジケースに依らず安定した検証が可能となる。
/// </para>
/// <para>
/// <b>運用ルール:</b> <c>DbContext.IsOnUiThread</c> に値を代入（フック差し替え）する
/// テストクラスを新規追加する場合は、必ず
/// <c>[Collection(DbContextUiThreadHookCollection.Name)]</c> 属性を付与すること。
/// 付与漏れは <see cref="DbContextUiThreadHookCollectionConfigurationTests"/> が
/// リフレクションで検出する（新規対象クラスは同テストの <c>InlineData</c> にも追加する）。
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class DbContextUiThreadHookCollection
{
    public const string Name = "DbContext UI Thread Hook Static State";
}
