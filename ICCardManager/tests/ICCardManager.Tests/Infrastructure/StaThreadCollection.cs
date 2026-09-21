using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2083: STA スレッドで WPF の型を組むテストを、他のテストコレクションと
/// 並列実行させないための xUnit テストコレクション定義。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StaTestRunner"/> が待っているのは計算時間ではなく<b>スケジューリング</b>である。
/// GitHub ホストランナーは 2 コア共有で、STA スレッドは初回に
/// <c>PresentationFramework</c> の初期化を伴うため、他のテストと CPU を奪い合う。
/// 実測 0.1 秒に対して上限 30 秒（約 290 倍の余裕）がありながら
/// PR #2082 の CI で打ち切られたので、<b>上限の倍率を増やしても根治しない</b>。
/// </para>
/// <para>
/// <c>DisableParallelization = true</c> を付けたコレクションは、xUnit のスケジューラーが
/// 他のどのコレクションとも同時に走らせない。競合の相手そのものが減るため、
/// 打ち切りの原因（CPU の奪い合い）が構造的に取り除かれる。
/// </para>
/// <para>
/// <b>運用ルール:</b> <see cref="StaTestRunner"/> を使うテストクラスには必ず
/// <c>[Collection(StaThreadCollection.Name)]</c> を付与すること。
/// 付与漏れと、ヘルパーを介さず自前で STA スレッドを組む形（＝複製の再発）は
/// <see cref="StaThreadCollectionConventionTests"/> が静的検査で検出する。
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class StaThreadCollection
{
    public const string Name = "STA Thread";
}
