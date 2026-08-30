using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Infrastructure.Caching;

/// <summary>
/// CacheServiceのテスト
/// </summary>
public class CacheServiceTests : IDisposable
{
    private readonly CacheService _sut;

    public CacheServiceTests()
    {
        _sut = new CacheService(NullLogger<CacheService>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
    }

    #region Set/Get Tests

    [Fact]
    public void Set_ShouldStoreValue()
    {
        // Arrange
        const string key = "test:key";
        const string value = "test-value";

        // Act
        _sut.Set(key, value, TimeSpan.FromMinutes(1));

        // Assert
        var result = _sut.Get<string>(key);
        result.Should().Be(value);
    }

    [Fact]
    public void Get_WithNonExistentKey_ShouldReturnDefault()
    {
        // Act
        var result = _sut.Get<string>("non-existent-key");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Get_WithValueType_ShouldReturnDefault()
    {
        // Act
        var result = _sut.Get<int>("non-existent-int-key");

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void Set_WithComplexObject_ShouldStoreAndRetrieve()
    {
        // Arrange
        const string key = "test:complex";
        var value = new TestObject { Id = 1, Name = "Test" };

        // Act
        _sut.Set(key, value, TimeSpan.FromMinutes(1));
        var result = _sut.Get<TestObject>(key);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Name.Should().Be("Test");
    }

    [Fact]
    public void Set_WithSameKey_ShouldOverwriteValue()
    {
        // Arrange
        const string key = "test:overwrite";
        _sut.Set(key, "original", TimeSpan.FromMinutes(1));

        // Act
        _sut.Set(key, "updated", TimeSpan.FromMinutes(1));

        // Assert
        var result = _sut.Get<string>(key);
        result.Should().Be("updated");
    }

    #endregion

    #region GetOrCreateAsync Tests

    [Fact]
    public async Task GetOrCreateAsync_WithCacheMiss_ShouldCallFactory()
    {
        // Arrange
        const string key = "test:factory";
        var factoryCalled = false;

        // Act
        var result = await _sut.GetOrCreateAsync(key, async () =>
        {
            factoryCalled = true;
            await Task.Delay(1); // Simulate async work
            return "factory-value";
        }, TimeSpan.FromMinutes(1));

        // Assert
        result.Should().Be("factory-value");
        factoryCalled.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithCacheHit_ShouldNotCallFactory()
    {
        // Arrange
        const string key = "test:cache-hit";
        _sut.Set(key, "cached-value", TimeSpan.FromMinutes(1));
        var factoryCalled = false;

        // Act
        var result = await _sut.GetOrCreateAsync(key, async () =>
        {
            factoryCalled = true;
            await Task.Delay(1);
            return "factory-value";
        }, TimeSpan.FromMinutes(1));

        // Assert
        result.Should().Be("cached-value");
        factoryCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrCreateAsync_ShouldCacheResultFromFactory()
    {
        // Arrange
        const string key = "test:cache-result";
        var callCount = 0;

        // Act
        await _sut.GetOrCreateAsync(key, async () =>
        {
            callCount++;
            await Task.Delay(1);
            return $"value-{callCount}";
        }, TimeSpan.FromMinutes(1));

        await _sut.GetOrCreateAsync(key, async () =>
        {
            callCount++;
            await Task.Delay(1);
            return $"value-{callCount}";
        }, TimeSpan.FromMinutes(1));

        // Assert
        callCount.Should().Be(1);
        var cachedResult = _sut.Get<string>(key);
        cachedResult.Should().Be("value-1");
    }

    #endregion

    #region Invalidate Tests

    [Fact]
    public void Invalidate_ShouldRemoveSpecificKey()
    {
        // Arrange
        _sut.Set("key1", "value1", TimeSpan.FromMinutes(1));
        _sut.Set("key2", "value2", TimeSpan.FromMinutes(1));

        // Act
        _sut.Invalidate("key1");

        // Assert
        _sut.Get<string>("key1").Should().BeNull();
        _sut.Get<string>("key2").Should().Be("value2");
    }

    [Fact]
    public void Invalidate_WithNonExistentKey_ShouldNotThrow()
    {
        // Act & Assert
        var action = () => _sut.Invalidate("non-existent");
        action.Should().NotThrow();
    }

    #endregion

    #region InvalidateByPrefix Tests

    [Fact]
    public void InvalidateByPrefix_ShouldRemoveAllMatchingKeys()
    {
        // Arrange
        _sut.Set("card:all", "value1", TimeSpan.FromMinutes(1));
        _sut.Set("card:lent", "value2", TimeSpan.FromMinutes(1));
        _sut.Set("staff:all", "value3", TimeSpan.FromMinutes(1));

        // Act
        _sut.InvalidateByPrefix("card:");

        // Assert
        _sut.Get<string>("card:all").Should().BeNull();
        _sut.Get<string>("card:lent").Should().BeNull();
        _sut.Get<string>("staff:all").Should().Be("value3");
    }

    [Fact]
    public void InvalidateByPrefix_WithNoMatchingKeys_ShouldNotThrow()
    {
        // Arrange
        _sut.Set("key1", "value1", TimeSpan.FromMinutes(1));

        // Act & Assert
        var action = () => _sut.InvalidateByPrefix("nonexistent:");
        action.Should().NotThrow();
    }

    [Fact]
    public void InvalidateByPrefix_ShouldBeCaseInsensitive()
    {
        // Arrange
        _sut.Set("Card:All", "value1", TimeSpan.FromMinutes(1));
        _sut.Set("CARD:Lent", "value2", TimeSpan.FromMinutes(1));

        // Act
        _sut.InvalidateByPrefix("card:");

        // Assert
        _sut.Get<string>("Card:All").Should().BeNull();
        _sut.Get<string>("CARD:Lent").Should().BeNull();
    }

    #endregion

    #region Clear Tests

    [Fact]
    public void Clear_ShouldRemoveAllKeys()
    {
        // Arrange
        _sut.Set("key1", "value1", TimeSpan.FromMinutes(1));
        _sut.Set("key2", "value2", TimeSpan.FromMinutes(1));
        _sut.Set("key3", "value3", TimeSpan.FromMinutes(1));

        // Act
        _sut.Clear();

        // Assert
        _sut.Get<string>("key1").Should().BeNull();
        _sut.Get<string>("key2").Should().BeNull();
        _sut.Get<string>("key3").Should().BeNull();
    }

    [Fact]
    public void Clear_WithEmptyCache_ShouldNotThrow()
    {
        // Act & Assert
        var action = () => _sut.Clear();
        action.Should().NotThrow();
    }

    #endregion

    #region Cache Expiration Tests

    [Fact]
    public async Task CachedValue_ShouldExpireAfterDuration()
    {
        // Arrange
        const string key = "test:expiration";
        _sut.Set(key, "value", TimeSpan.FromMilliseconds(100));

        // Act
        await Task.Delay(150); // Wait longer than expiration

        // Assert
        var result = _sut.Get<string>(key);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CachedValue_ShouldNotExpireBeforeDuration()
    {
        // Arrange
        const string key = "test:not-expired";
        _sut.Set(key, "value", TimeSpan.FromSeconds(10));

        // Act
        await Task.Delay(50); // Wait less than expiration

        // Assert
        var result = _sut.Get<string>(key);
        result.Should().Be("value");
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        const string key = "test:concurrent";
        var tasks = new List<Task>();

        // Act
        for (var i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                _sut.Set($"{key}:{index}", $"value-{index}", TimeSpan.FromMinutes(1));
                _sut.Get<string>($"{key}:{index}");
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        for (var i = 0; i < 100; i++)
        {
            var result = _sut.Get<string>($"{key}:{i}");
            result.Should().Be($"value-{i}");
        }
    }

    [Fact]
    public async Task ConcurrentInvalidation_ShouldBeThreadSafe()
    {
        // Arrange
        for (var i = 0; i < 50; i++)
        {
            _sut.Set($"prefix:{i}", $"value-{i}", TimeSpan.FromMinutes(1));
        }

        var tasks = new List<Task>();

        // Act - Concurrent invalidation and read
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() => _sut.InvalidateByPrefix("prefix:")));
            tasks.Add(Task.Run(() => _sut.Get<string>("prefix:0")));
        }

        // Assert
        var action = async () => await Task.WhenAll(tasks);
        await action.Should().NotThrowAsync();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_ShouldNotThrowOnMultipleCalls()
    {
        // Arrange
        var cache = new CacheService(NullLogger<CacheService>.Instance);
        cache.Set("key", "value", TimeSpan.FromMinutes(1));

        // Act & Assert
        var action = () =>
        {
            cache.Dispose();
            cache.Dispose();
        };
        action.Should().NotThrow();
    }

    #endregion

    #region Issue #1167: ダブルチェックロッキング テスト

    /// <summary>
    /// Issue #1167: 並行呼び出しでもfactoryが1回だけ実行されること（ダブルチェックロッキング）
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_ConcurrentCalls_FactoryExecutedOnce()
    {
        // Arrange
        const string key = "concurrent:key";
        var factoryCallCount = 0;
        var factoryStartedEvent = new System.Threading.ManualResetEventSlim(false);
        var factoryReleaseEvent = new System.Threading.ManualResetEventSlim(false);

        async Task<string> SlowFactory()
        {
            System.Threading.Interlocked.Increment(ref factoryCallCount);
            factoryStartedEvent.Set();
            // 並行呼び出し側がロック待ちに入る時間を確保
            await Task.Run(() => factoryReleaseEvent.Wait(TimeSpan.FromSeconds(5)));
            return "factory-result";
        }

        // Act: 並行で10個のGetOrCreateAsyncを呼び出す
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _sut.GetOrCreateAsync(key, SlowFactory, TimeSpan.FromMinutes(1)))
            .ToArray();

        // 最初のfactoryが開始されるまで待つ
        factoryStartedEvent.Wait(TimeSpan.FromSeconds(5));
        // 残りの呼び出しはロック待ちに入る → factory完了を許可
        await Task.Delay(100);
        factoryReleaseEvent.Set();

        var results = await Task.WhenAll(tasks);

        // Assert: factoryは1回だけ実行され、全ての呼び出しが同じ値を取得
        factoryCallCount.Should().Be(1, "ダブルチェックロッキングによりfactoryは1回のみ実行されるべき");
        results.Should().AllSatisfy(r => r.Should().Be("factory-result"));
    }

    /// <summary>
    /// Issue #1167: 異なるキーは互いをブロックしないこと
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_DifferentKeys_DoNotBlockEachOther()
    {
        // Arrange
        var key1Started = new System.Threading.ManualResetEventSlim(false);
        var key2Started = new System.Threading.ManualResetEventSlim(false);

        async Task<string> Factory1()
        {
            key1Started.Set();
            await Task.Run(() => key2Started.Wait(TimeSpan.FromSeconds(5)));
            return "value1";
        }

        async Task<string> Factory2()
        {
            key2Started.Set();
            await Task.CompletedTask;
            return "value2";
        }

        // Act: key1のfactoryを開始してブロックしている間にkey2を呼ぶ
        var task1 = _sut.GetOrCreateAsync("key1", Factory1, TimeSpan.FromMinutes(1));
        key1Started.Wait(TimeSpan.FromSeconds(5));

        var task2 = _sut.GetOrCreateAsync("key2", Factory2, TimeSpan.FromMinutes(1));

        var result2 = await task2; // key2はブロックされずに完了
        var result1 = await task1;

        // Assert
        result1.Should().Be("value1");
        result2.Should().Be("value2");
    }

    /// <summary>
    /// Issue #1167: ロック取得後にキャッシュにヒットした場合factoryが実行されないこと
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_DoubleCheckHit_FactoryNotCalled()
    {
        // Arrange
        const string key = "double:check:key";
        // 先にキャッシュに値を入れておく
        _sut.Set(key, "preset-value", TimeSpan.FromMinutes(1));

        var factoryCalled = false;
        Task<string> Factory()
        {
            factoryCalled = true;
            return Task.FromResult("factory-value");
        }

        // Act
        var result = await _sut.GetOrCreateAsync(key, Factory, TimeSpan.FromMinutes(1));

        // Assert
        result.Should().Be("preset-value");
        factoryCalled.Should().BeFalse("キャッシュヒット時はfactoryが呼ばれないべき");
    }

    #endregion

    #region Issue #1257: キャッシュクリアと再取得の整合性

    /// <summary>
    /// Issue #1257: Invalidate 後の GetOrCreateAsync では factory が再実行され、最新値が返ること。
    /// </summary>
    /// <remarks>
    /// 共有モードで他PCがDB更新 → 自PCで Invalidate → 次回 GetOrCreateAsync で
    /// 最新値が取得される流れを検証。二重貸出リスク回避の基礎契約。
    /// </remarks>
    [Fact]
    public async Task Issue1257_GetOrCreateAsync_AfterInvalidate_ReExecutesFactoryWithFreshValue()
    {
        // Arrange: 初期値をキャッシュ
        const string key = "card:status";
        var factoryCallCount = 0;
        var currentValue = "lent";
        Task<string> Factory()
        {
            factoryCallCount++;
            return Task.FromResult(currentValue);
        }

        var first = await _sut.GetOrCreateAsync(key, Factory, TimeSpan.FromMinutes(1));
        first.Should().Be("lent");
        factoryCallCount.Should().Be(1);

        // Act: DB が他PCで更新された想定 → Invalidate → 新値を factory に返させる
        currentValue = "returned";
        _sut.Invalidate(key);
        var second = await _sut.GetOrCreateAsync(key, Factory, TimeSpan.FromMinutes(1));

        // Assert
        second.Should().Be("returned", "Invalidate後は factory が再実行され最新値が反映される");
        factoryCallCount.Should().Be(2, "Invalidate で TTL 未満でも factory が再実行される");
    }

    /// <summary>
    /// Issue #1257: InvalidateByPrefix 後、一致するキーのみ factory が再実行される。
    /// </summary>
    [Fact]
    public async Task Issue1257_GetOrCreateAsync_AfterInvalidateByPrefix_OnlyMatchingKeysReExecute()
    {
        // Arrange: 3種類のキーをキャッシュ
        var factoryCounts = new Dictionary<string, int>
        {
            ["card:1"] = 0,
            ["card:2"] = 0,
            ["staff:1"] = 0,
        };

        async Task<string> FactoryFor(string k)
        {
            factoryCounts[k]++;
            await Task.CompletedTask;
            return $"{k}=v{factoryCounts[k]}";
        }

        await _sut.GetOrCreateAsync("card:1", () => FactoryFor("card:1"), TimeSpan.FromMinutes(1));
        await _sut.GetOrCreateAsync("card:2", () => FactoryFor("card:2"), TimeSpan.FromMinutes(1));
        await _sut.GetOrCreateAsync("staff:1", () => FactoryFor("staff:1"), TimeSpan.FromMinutes(1));

        // Act: card: プレフィックスのみ無効化
        _sut.InvalidateByPrefix("card:");

        var card1Again = await _sut.GetOrCreateAsync("card:1", () => FactoryFor("card:1"), TimeSpan.FromMinutes(1));
        var card2Again = await _sut.GetOrCreateAsync("card:2", () => FactoryFor("card:2"), TimeSpan.FromMinutes(1));
        var staff1Again = await _sut.GetOrCreateAsync("staff:1", () => FactoryFor("staff:1"), TimeSpan.FromMinutes(1));

        // Assert: card:系のみ factory 再実行、staff:はキャッシュヒット維持
        factoryCounts["card:1"].Should().Be(2, "card:1 は無効化されたので factory 再実行");
        factoryCounts["card:2"].Should().Be(2, "card:2 も無効化対象");
        factoryCounts["staff:1"].Should().Be(1, "staff:1 は無効化対象外のためキャッシュヒット維持");

        card1Again.Should().Be("card:1=v2");
        card2Again.Should().Be("card:2=v2");
        staff1Again.Should().Be("staff:1=v1");
    }

    /// <summary>
    /// Issue #1257: Clear 後は全キーが再取得される（全面無効化）。
    /// </summary>
    [Fact]
    public async Task Issue1257_GetOrCreateAsync_AfterClear_AllKeysReExecuteFactory()
    {
        // Arrange
        var counts = new Dictionary<string, int> { ["k1"] = 0, ["k2"] = 0 };
        async Task<int> Factory(string k)
        {
            counts[k]++;
            await Task.CompletedTask;
            return counts[k];
        }

        await _sut.GetOrCreateAsync("k1", () => Factory("k1"), TimeSpan.FromMinutes(1));
        await _sut.GetOrCreateAsync("k2", () => Factory("k2"), TimeSpan.FromMinutes(1));

        // Act
        _sut.Clear();
        var v1Again = await _sut.GetOrCreateAsync("k1", () => Factory("k1"), TimeSpan.FromMinutes(1));
        var v2Again = await _sut.GetOrCreateAsync("k2", () => Factory("k2"), TimeSpan.FromMinutes(1));

        // Assert
        counts["k1"].Should().Be(2, "Clear 後は k1 の factory が再実行");
        counts["k2"].Should().Be(2, "Clear 後は k2 の factory も再実行");
        v1Again.Should().Be(2);
        v2Again.Should().Be(2);
    }

    /// <summary>
    /// Issue #1257: Invalidate と GetOrCreateAsync の並行実行で整合性が崩れないこと。
    /// </summary>
    /// <remarks>
    /// 並行下でも:
    /// - 例外が発生しない
    /// - 最終状態は factory が少なくとも1回は実行されている
    /// - 最終値は factory の戻り値（= 最新値）として読み出せる
    /// を検証する。ダブルチェックロッキングの副作用として、Invalidate タイミング次第で
    /// factory 実行回数は 1 〜 N 回のいずれも許容される（順序非決定）。
    /// </remarks>
    [Fact]
    public async Task Issue1257_ConcurrentInvalidateAndGetOrCreate_RemainsConsistent()
    {
        // Arrange
        const string key = "concurrent:integrity";
        var factoryCallCount = 0;
        Task<int> Factory()
        {
            return Task.FromResult(System.Threading.Interlocked.Increment(ref factoryCallCount));
        }

        // Act: 20回のGetOrCreateAsync と 20回のInvalidate を並行実行
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() => _sut.GetOrCreateAsync(key, Factory, TimeSpan.FromMinutes(1))));
            tasks.Add(Task.Run(() => _sut.Invalidate(key)));
        }

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("並行実行でも例外は発生しない");

        // Assert: 最終状態でキャッシュから値が取得できるか、再取得でfactoryが呼ばれる
        var finalValue = await _sut.GetOrCreateAsync(key, Factory, TimeSpan.FromMinutes(1));
        finalValue.Should().BeGreaterThan(0, "最終的に factory が実行され有効な値が得られる");
        factoryCallCount.Should().BeGreaterThan(0, "少なくとも1回は factory が実行された");
    }

    #endregion

    #region Issue #1943: PostEvictionCallback の EvictionReason 判別

    // MemoryCache は退避コールバックをスレッドプールで実行するため、同じキーへの再 Set で発火した
    // 旧エントリの後始末（EvictionReason.Replaced）が、Set 直後のキー追加より後に着地し得る。
    // 理由を見ずに追跡表から削除すると「キャッシュには生きているのに InvalidateByPrefix が消せないキー」
    // が生まれる（Issue #1759 が防ごうとした「削除済みの行が一覧に残る」状態）。
    // 着地の遅れは内部コールバック OnPostEviction を直接呼ぶことで確定的に再現する（スレッドを競争させない）。

    [Fact]
    public void Issue1943_再Setの遅延コールバックが着地してもキーは追跡表に残ること()
    {
        // Arrange: 同じキーへ再 Set（実キャッシュには新しい値が生きている）
        const string key = "card:all";
        _sut.Set(key, "v1", TimeSpan.FromMinutes(1));
        _sut.Set(key, "v2", TimeSpan.FromMinutes(1));

        // Act: 旧エントリの Replaced コールバックが遅れて着地した形
        _sut.OnPostEviction(key, "v1", EvictionReason.Replaced, null);

        // Assert
        _sut.TrackedKeys.Should().Contain(key, "再 Set 後のキーはキャッシュに生きている");
    }

    [Fact]
    public void Issue1943_再Setの遅延コールバック着地後もInvalidateByPrefixが消せること()
    {
        // Arrange
        const string key = "card:all";
        _sut.Set(key, "v1", TimeSpan.FromMinutes(1));
        _sut.Set(key, "v2", TimeSpan.FromMinutes(1));
        _sut.OnPostEviction(key, "v1", EvictionReason.Replaced, null);

        // Act: CardRepository.InvalidateCardCache と同じ経路
        _sut.InvalidateByPrefix("card:");

        // Assert
        _sut.Get<string>(key).Should().BeNull("無効化したキーがキャッシュに残ってはならない");
    }

    [Fact]
    public void Issue1943_明示削除の遅延コールバックが後続のSetを取りこぼさないこと()
    {
        // Arrange: Invalidate は同期で追跡表からも消すため、Removed のコールバックは後始末済みの重複。
        // その着地が後続の Set より遅れると、生きているキーを追跡表から落とす。
        const string key = "staff:all";
        _sut.Set(key, "v1", TimeSpan.FromMinutes(1));
        _sut.Invalidate(key);
        _sut.Set(key, "v2", TimeSpan.FromMinutes(1));

        // Act
        _sut.OnPostEviction(key, "v1", EvictionReason.Removed, null);

        // Assert
        _sut.TrackedKeys.Should().Contain(key);
        _sut.InvalidateByPrefix("staff:");
        _sut.Get<string>(key).Should().BeNull();
    }

    // 対の表明: 本当に追い出されたキーは追跡表からも消える（Replaced を除外しただけで
    // 追跡表が永久に増え続ける形にしない）
    [Theory]
    [InlineData(EvictionReason.Expired)]
    [InlineData(EvictionReason.Capacity)]
    [InlineData(EvictionReason.TokenExpired)]
    public async Task Issue1943_キャッシュ都合の退避では追跡表からも消えること(EvictionReason reason)
    {
        // Arrange: 「キャッシュには無く、追跡表にだけ残っている」＝実際に失われた状態を作る。
        // 追跡表は Set で追加され、期限切れの反映（本物のコールバック）はアクセス契機で起きるため、
        // アクセスせずに期限だけ切らせるとこの状態になる。
        const string key = "card:all";
        _sut.Set(key, "v1", TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);
        _sut.TrackedKeys.Should().Contain(key, "前提: 追跡表にはまだ残っている");

        // Act
        _sut.OnPostEviction(key, "v1", reason, null);

        // Assert
        _sut.TrackedKeys.Should().NotContain(key, "エントリが実際に失われた退避では追跡表も畳む");
    }

    // 理由の判別だけでは Expired 自身の遅延着地を塞げない。期限切れの検出は GetOrCreateAsync 冒頭の
    // TryGetValue で起きてコールバックを投げたあと、factory() の DB 往復を挟んでから Set へ進むため、
    // スレッドプールが詰まると旧世代の Expired が新しいエントリより後に着地する。
    // 理由だけを見て消すと、本 Issue が消したはずの「生きているのに追跡表から落ちたキー」がここに残る。
    [Fact]
    public void Issue1943_旧世代のExpiredが遅れて着地しても生きているキーは追跡表に残ること()
    {
        // Arrange: 期限切れの後に再取得され、新しいエントリが生きている状態
        const string key = "card:all";
        _sut.Set(key, "v1", TimeSpan.FromMinutes(1));

        // Act: 旧エントリの Expired コールバックが遅れて着地した形
        _sut.OnPostEviction(key, "v1", EvictionReason.Expired, null);

        // Assert
        _sut.TrackedKeys.Should().Contain(key, "新しいエントリがキャッシュに生きている");
        _sut.InvalidateByPrefix("card:");
        _sut.Get<string>(key).Should().BeNull("無効化したキーがキャッシュに残ってはならない");
    }

    [Fact]
    public async Task Issue1943_実際の有効期限切れでも追跡表から消えること()
    {
        // 継ぎ目の検証: 登録されたコールバックが OnPostEviction を通っていることを、
        // 実際の MemoryCache の期限切れ退避で確かめる（内部メソッドの直接呼び出しだけでは
        // 本番の登録経路が別物へ差し替わっても緑になるため）。
        const string key = "card:expiring";
        _sut.Set(key, "v1", TimeSpan.FromMilliseconds(50));

        // Act: 期限切れの検出はアクセス契機で行われるため、ポーリングして着地を待つ
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && _sut.TrackedKeys.Contains(key))
        {
            _sut.Get<string>(key);
            await Task.Delay(20);
        }

        // Assert
        _sut.TrackedKeys.Should().NotContain(key);
    }

    #endregion

    #region Helper Classes

    private class TestObject
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    #endregion
}
