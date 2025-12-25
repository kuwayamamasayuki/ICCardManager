using FluentAssertions;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// CardLockManagerの単体テスト
/// </summary>
public class CardLockManagerTests : IDisposable
{
    private readonly CardLockManager _lockManager;

    public CardLockManagerTests()
    {
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GetLock テスト

    /// <summary>
    /// 新しいカードIDmに対してロックが作成されることを確認
    /// </summary>
    [Fact]
    public void GetLock_NewCardIdm_CreatesNewLock()
    {
        // Arrange
        const string cardIdm = "0102030405060708";

        // Act
        var semaphore = _lockManager.GetLock(cardIdm);

        // Assert
        semaphore.Should().NotBeNull();
        _lockManager.LockCount.Should().Be(1);
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    /// <summary>
    /// 同じカードIDmに対して同じロックが返されることを確認
    /// </summary>
    [Fact]
    public void GetLock_SameCardIdm_ReturnsSameLock()
    {
        // Arrange
        const string cardIdm = "0102030405060708";

        // Act
        var semaphore1 = _lockManager.GetLock(cardIdm);
        _lockManager.ReleaseLockReference(cardIdm);
        var semaphore2 = _lockManager.GetLock(cardIdm);

        // Assert
        semaphore1.Should().BeSameAs(semaphore2);
        _lockManager.LockCount.Should().Be(1);
    }

    /// <summary>
    /// 異なるカードIDmに対して異なるロックが作成されることを確認
    /// </summary>
    [Fact]
    public void GetLock_DifferentCardIdm_CreatesDifferentLocks()
    {
        // Arrange
        const string cardIdm1 = "0102030405060708";
        const string cardIdm2 = "0807060504030201";

        // Act
        var semaphore1 = _lockManager.GetLock(cardIdm1);
        var semaphore2 = _lockManager.GetLock(cardIdm2);

        // Assert
        semaphore1.Should().NotBeSameAs(semaphore2);
        _lockManager.LockCount.Should().Be(2);
    }

    /// <summary>
    /// GetLockが参照カウントをインクリメントすることを確認
    /// </summary>
    [Fact]
    public void GetLock_MultipleCalls_IncrementsReferenceCount()
    {
        // Arrange
        const string cardIdm = "0102030405060708";

        // Act - 3回GetLockを呼び出し
        _lockManager.GetLock(cardIdm);
        _lockManager.GetLock(cardIdm);
        _lockManager.GetLock(cardIdm);

        // Assert - ロックは1つだが参照カウントが3
        _lockManager.LockCount.Should().Be(1);
        // 参照カウントがあるため、即座にクリーンアップされないことを確認
        _lockManager.CleanupExpiredLocks();
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    #endregion

    #region ReleaseLockReference テスト

    /// <summary>
    /// ReleaseLockReferenceが参照カウントをデクリメントすることを確認
    /// </summary>
    [Fact]
    public void ReleaseLockReference_DecreasesReferenceCount()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.GetLock(cardIdm);
        _lockManager.GetLock(cardIdm);

        // Act
        _lockManager.ReleaseLockReference(cardIdm);
        _lockManager.ReleaseLockReference(cardIdm);

        // Assert - 参照カウントが0になってもロックは即座には削除されない
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    /// <summary>
    /// 存在しないカードIDmでReleaseLockReferenceを呼んでもエラーにならないことを確認
    /// </summary>
    [Fact]
    public void ReleaseLockReference_NonExistentCardIdm_DoesNotThrow()
    {
        // Arrange
        const string cardIdm = "NONEXISTENT";

        // Act & Assert - 例外が発生しないことを確認
        var action = () => _lockManager.ReleaseLockReference(cardIdm);
        action.Should().NotThrow();
    }

    /// <summary>
    /// 参照カウントが0未満にならないことを確認
    /// </summary>
    [Fact]
    public void ReleaseLockReference_MultipleCalls_DoesNotGoNegative()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.GetLock(cardIdm); // 参照カウント: 1

        // Act - 参照カウント以上にReleaseを呼び出し
        _lockManager.ReleaseLockReference(cardIdm);
        _lockManager.ReleaseLockReference(cardIdm);
        _lockManager.ReleaseLockReference(cardIdm);

        // Assert - 例外なく完了
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    #endregion

    #region CleanupExpiredLocks テスト

    /// <summary>
    /// 期限切れで参照カウント0のロックがクリーンアップされることを確認
    /// </summary>
    [Fact]
    public void CleanupExpiredLocks_ExpiredLockWithZeroRefCount_RemovesLock()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.LockExpiration = TimeSpan.FromMilliseconds(10); // 10msで期限切れ

        var semaphore = _lockManager.GetLock(cardIdm);
        _lockManager.ReleaseLockReference(cardIdm);

        // Act - 期限切れを待ってからクリーンアップ
        Thread.Sleep(20);
        _lockManager.CleanupExpiredLocks();

        // Assert
        _lockManager.HasLock(cardIdm).Should().BeFalse();
        _lockManager.LockCount.Should().Be(0);
    }

    /// <summary>
    /// 参照カウントが0でないロックはクリーンアップされないことを確認
    /// </summary>
    [Fact]
    public void CleanupExpiredLocks_LockWithRefCount_NotRemoved()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.LockExpiration = TimeSpan.FromMilliseconds(10);

        _lockManager.GetLock(cardIdm); // 参照カウント: 1（Releaseしない）

        // Act - 期限切れを待ってからクリーンアップ
        Thread.Sleep(20);
        _lockManager.CleanupExpiredLocks();

        // Assert - 参照カウントがあるため削除されない
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    /// <summary>
    /// 使用中（ロック取得中）のSemaphoreSlimはクリーンアップされないことを確認
    /// </summary>
    [Fact]
    public async Task CleanupExpiredLocks_SemaphoreInUse_NotRemoved()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.LockExpiration = TimeSpan.FromMilliseconds(10);

        var semaphore = _lockManager.GetLock(cardIdm);
        await semaphore.WaitAsync(); // ロック取得
        _lockManager.ReleaseLockReference(cardIdm);

        // Act - 期限切れを待ってからクリーンアップ
        await Task.Delay(20);
        _lockManager.CleanupExpiredLocks();

        // Assert - SemaphoreSlimが使用中のため削除されない
        _lockManager.HasLock(cardIdm).Should().BeTrue();

        // Cleanup
        semaphore.Release();
    }

    /// <summary>
    /// 期限切れでないロックはクリーンアップされないことを確認
    /// </summary>
    [Fact]
    public void CleanupExpiredLocks_NotExpiredLock_NotRemoved()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.LockExpiration = TimeSpan.FromHours(1); // 1時間後に期限切れ

        _lockManager.GetLock(cardIdm);
        _lockManager.ReleaseLockReference(cardIdm);

        // Act
        _lockManager.CleanupExpiredLocks();

        // Assert - 期限切れでないため削除されない
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    /// <summary>
    /// 複数のロックで一部のみ期限切れの場合、期限切れのみクリーンアップされることを確認
    /// </summary>
    [Fact]
    public void CleanupExpiredLocks_MixedExpiration_OnlyExpiredRemoved()
    {
        // Arrange
        const string expiredCardIdm = "0102030405060708";
        const string recentCardIdm = "0807060504030201";
        _lockManager.LockExpiration = TimeSpan.FromMilliseconds(50);

        // expiredCardIdmを作成して参照解放
        _lockManager.GetLock(expiredCardIdm);
        _lockManager.ReleaseLockReference(expiredCardIdm);

        // 期限切れを待つ
        Thread.Sleep(60);

        // recentCardIdmを作成（これは期限切れでない）
        _lockManager.GetLock(recentCardIdm);
        _lockManager.ReleaseLockReference(recentCardIdm);

        // Act
        _lockManager.CleanupExpiredLocks();

        // Assert
        _lockManager.HasLock(expiredCardIdm).Should().BeFalse("期限切れのロックは削除される");
        _lockManager.HasLock(recentCardIdm).Should().BeTrue("期限切れでないロックは残る");
    }

    #endregion

    #region ClearAllLocks テスト

    /// <summary>
    /// ClearAllLocksがすべてのロックを削除することを確認
    /// </summary>
    [Fact]
    public void ClearAllLocks_RemovesAllLocks()
    {
        // Arrange
        _lockManager.GetLock("card1");
        _lockManager.GetLock("card2");
        _lockManager.GetLock("card3");

        // Act
        _lockManager.ClearAllLocks();

        // Assert
        _lockManager.LockCount.Should().Be(0);
        _lockManager.HasLock("card1").Should().BeFalse();
        _lockManager.HasLock("card2").Should().BeFalse();
        _lockManager.HasLock("card3").Should().BeFalse();
    }

    /// <summary>
    /// 空の状態でClearAllLocksを呼んでもエラーにならないことを確認
    /// </summary>
    [Fact]
    public void ClearAllLocks_EmptyManager_DoesNotThrow()
    {
        // Act & Assert
        var action = () => _lockManager.ClearAllLocks();
        action.Should().NotThrow();
    }

    #endregion

    #region RemoveLock テスト

    /// <summary>
    /// RemoveLockが特定のロックを削除することを確認
    /// </summary>
    [Fact]
    public void RemoveLock_ExistingLock_RemovesAndReturnsTrue()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.GetLock(cardIdm);

        // Act
        var result = _lockManager.RemoveLock(cardIdm);

        // Assert
        result.Should().BeTrue();
        _lockManager.HasLock(cardIdm).Should().BeFalse();
    }

    /// <summary>
    /// 存在しないロックに対するRemoveLockがfalseを返すことを確認
    /// </summary>
    [Fact]
    public void RemoveLock_NonExistentLock_ReturnsFalse()
    {
        // Act
        var result = _lockManager.RemoveLock("NONEXISTENT");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region HasLock テスト

    /// <summary>
    /// 存在するロックに対してHasLockがtrueを返すことを確認
    /// </summary>
    [Fact]
    public void HasLock_ExistingLock_ReturnsTrue()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        _lockManager.GetLock(cardIdm);

        // Act & Assert
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    /// <summary>
    /// 存在しないロックに対してHasLockがfalseを返すことを確認
    /// </summary>
    [Fact]
    public void HasLock_NonExistentLock_ReturnsFalse()
    {
        // Act & Assert
        _lockManager.HasLock("NONEXISTENT").Should().BeFalse();
    }

    #endregion

    #region Dispose テスト

    /// <summary>
    /// Dispose後にGetLockを呼び出しても例外が発生しないことを確認
    /// （内部的にはクリーンアップされている）
    /// </summary>
    [Fact]
    public void Dispose_ClearsAllLocks()
    {
        // Arrange
        var localManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        localManager.GetLock("card1");
        localManager.GetLock("card2");

        // Act
        localManager.Dispose();

        // Assert
        localManager.LockCount.Should().Be(0);
    }

    /// <summary>
    /// 複数回Disposeを呼び出してもエラーにならないことを確認
    /// </summary>
    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        // Arrange
        var localManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        // Act & Assert
        var action = () =>
        {
            localManager.Dispose();
            localManager.Dispose();
            localManager.Dispose();
        };
        action.Should().NotThrow();
    }

    #endregion

    #region 並行アクセステスト

    /// <summary>
    /// 並行して同じカードIDmに対してGetLockを呼び出しても安全に動作することを確認
    /// </summary>
    [Fact]
    public async Task GetLock_ConcurrentAccess_ThreadSafe()
    {
        // Arrange
        const string cardIdm = "0102030405060708";
        const int taskCount = 100;
        var tasks = new List<Task>();

        // Act - 100個のタスクで同時にGetLockを呼び出し
        for (var i = 0; i < taskCount; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var semaphore = _lockManager.GetLock(cardIdm);
                _lockManager.ReleaseLockReference(cardIdm);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - ロックは1つだけ存在
        _lockManager.LockCount.Should().Be(1);
        _lockManager.HasLock(cardIdm).Should().BeTrue();
    }

    /// <summary>
    /// 並行してCleanupExpiredLocksを呼び出しても安全に動作することを確認
    /// </summary>
    [Fact]
    public async Task CleanupExpiredLocks_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        _lockManager.LockExpiration = TimeSpan.FromMilliseconds(1);

        // 複数のロックを作成
        for (var i = 0; i < 10; i++)
        {
            _lockManager.GetLock($"card{i}");
            _lockManager.ReleaseLockReference($"card{i}");
        }

        Thread.Sleep(10); // 期限切れを待つ

        // Act - 複数のタスクで同時にクリーンアップ
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _lockManager.CleanupExpiredLocks()))
            .ToList();

        // Assert - 例外なく完了
        var action = async () => await Task.WhenAll(tasks);
        await action.Should().NotThrowAsync();
    }

    #endregion

    #region プロパティテスト

    /// <summary>
    /// LockExpirationプロパティが設定できることを確認
    /// </summary>
    [Fact]
    public void LockExpiration_CanBeSet()
    {
        // Arrange
        var newExpiration = TimeSpan.FromMinutes(30);

        // Act
        _lockManager.LockExpiration = newExpiration;

        // Assert
        _lockManager.LockExpiration.Should().Be(newExpiration);
    }

    /// <summary>
    /// CleanupIntervalプロパティが設定できることを確認
    /// </summary>
    [Fact]
    public void CleanupInterval_CanBeSet()
    {
        // Arrange
        var newInterval = TimeSpan.FromMinutes(5);

        // Act
        _lockManager.CleanupInterval = newInterval;

        // Assert
        _lockManager.CleanupInterval.Should().Be(newInterval);
    }

    /// <summary>
    /// LockCountが正しい値を返すことを確認
    /// </summary>
    [Fact]
    public void LockCount_ReturnsCorrectValue()
    {
        // Act & Assert
        _lockManager.LockCount.Should().Be(0);

        _lockManager.GetLock("card1");
        _lockManager.LockCount.Should().Be(1);

        _lockManager.GetLock("card2");
        _lockManager.LockCount.Should().Be(2);

        _lockManager.RemoveLock("card1");
        _lockManager.LockCount.Should().Be(1);
    }

    #endregion
}
