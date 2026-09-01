using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1985: 現在カルチャ（<c>CultureInfo.DefaultThreadCurrentCulture</c>）を差し替える
/// テストのためのコレクション定義。
/// </summary>
/// <remarks>
/// <para>
/// 和暦カレンダーの回帰テストは <c>CultureInfo.CurrentCulture</c> を差し替えるが、
/// <b>実測すると .NET Framework 4.8 のこの環境では、その差し替えが <c>await</c> の継続へ
/// 引き継がれない</b>（`await Task.Yield()` の後にグレゴリオ暦へ戻る）。リポジトリ呼び出しは
/// 内部で <c>await</c> するため、差し替えだけでは「継続で走る整形が和暦にならず、
/// 修正前のコードでも緑になる」状態が起こり得る。
/// </para>
/// <para>
/// そこで <c>DefaultThreadCurrentCulture</c>（<b>プロセス全体</b>のスレッド既定）も併せて
/// 差し替える。これは他のテストへ漏れるため、<c>DisableParallelization = true</c> で
/// 直列実行させる — <see cref="Services.SummaryGeneratorCollection"/> /
/// <c>DbContextUiThreadHookCollection</c> が静的状態に対して採ったのと同じ形。
/// </para>
/// <para>
/// <b>運用ルール:</b> <c>CultureInfo.CurrentCulture</c> / <c>DefaultThreadCurrentCulture</c> を
/// 差し替えるテストを新規追加する場合は、必ず <c>[Collection(CurrentCultureCollection.Name)]</c>
/// を付与すること。
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class CurrentCultureCollection
{
    public const string Name = "CurrentCulture Process State";
}
