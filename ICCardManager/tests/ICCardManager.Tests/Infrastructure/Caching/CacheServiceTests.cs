using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Caching;

/// <summary>
/// CacheServiceのテスト
/// </summary>
public class CacheServiceTests : IDisposable
{
    private readonly CacheService _sut;

    public CacheServiceTests()
    {
        _sut = new CacheService();
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
        var cache = new CacheService();
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

    #region Helper Classes

    private class TestObject
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    #endregion
}
