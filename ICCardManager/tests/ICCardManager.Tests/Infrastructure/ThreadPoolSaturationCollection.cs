using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2213: スレッドプールを意図的に塞ぐテストを、他のテストコレクションと並列実行させないための定義。
/// </summary>
/// <remarks>
/// プールを塞いでいる間に他のテストが走ると、そのテストの <c>Task.Run</c> や <c>await</c> の続きが
/// 空きを待たされ、無関係なテストが時間切れで失敗し得る。<c>DisableParallelization = true</c> を付けた
/// コレクションは xUnit のスケジューラーが他のどのコレクションとも同時に走らせない。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ThreadPoolSaturationCollection
{
    public const string Name = "Thread Pool Saturation";
}
