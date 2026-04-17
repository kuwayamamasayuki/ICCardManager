using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1307: SummaryGenerator 静的状態共有のためのテストコレクション定義。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICCardManager.Services.SummaryGenerator"/> は
/// <c>_options</c> / <c>_transferStationGroups</c> を <c>static</c> フィールドとして保持しており、
/// <c>SummaryGenerator.Configure()</c> や <c>ResetToDefaults()</c> を呼ぶテストが並列実行されると、
/// 他のテストが読み取る <c>_options</c> を上書きしてしまい、偶発的な失敗（flaky test）を引き起こす。
/// </para>
/// <para>
/// 根本解決（静的状態のインスタンス化）は影響範囲が広いため別チケットで対応予定。
/// 本チケットでは、静的状態を変更するテストおよび上書き結果に影響を受けるテストを
/// 同一 xUnit Collection に属させ、<c>DisableParallelization = true</c> により
/// シリアル実行させることで偶発的失敗を解消する。
/// </para>
/// <para>
/// <b>運用ルール:</b> <c>SummaryGenerator.Configure</c> / <c>ResetToDefaults</c> を呼ぶテスト、
/// もしくは特定の摘要文字列（鉄道/バスの乗継統合や往復検出等）をアサートするテストを
/// 新規追加する場合は、必ず <c>[Collection("SummaryGenerator Static State")]</c>
/// 属性を付与すること。
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class SummaryGeneratorCollection
{
    public const string Name = "SummaryGenerator Static State";
}
