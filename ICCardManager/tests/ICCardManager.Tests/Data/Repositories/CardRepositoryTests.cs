using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// CardRepositoryの単体テスト
/// </summary>
public class CardRepositoryTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly CardRepository _repository;
    private readonly StaffRepository _staffRepository;

    // テスト用定数
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public CardRepositoryTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheServiceMock = new Mock<ICacheService>();

        // キャッシュをバイパスしてファクトリ関数を直接実行するよう設定
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());

        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<Staff>>> factory, TimeSpan expiration) => factory());

        _repository = new CardRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GetAllAsync テスト

    /// <summary>
    /// 空のデータベースでは空のリストを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetAllAsync_EmptyDatabase_ReturnsEmptyList()
    {
        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// 登録済みカードが正しく取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAllAsync_WithCards_ReturnsAllNonDeletedCards()
    {
        // Arrange
        var card1 = CreateTestCard("0102030405060708", "はやかけん", "H001");
        var card2 = CreateTestCard("0102030405060709", "nimoca", "N001");
        await _repository.InsertAsync(card1);
        await _repository.InsertAsync(card2);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(c => c.CardIdm == card1.CardIdm);
        result.Should().Contain(c => c.CardIdm == card2.CardIdm);
    }

    /// <summary>
    /// 論理削除されたカードは取得されないことを確認
    /// </summary>
    [Fact]
    public async Task GetAllAsync_ExcludesDeletedCards()
    {
        // Arrange
        var card1 = CreateTestCard("0102030405060708", "はやかけん", "H001");
        var card2 = CreateTestCard("0102030405060709", "nimoca", "N001");
        await _repository.InsertAsync(card1);
        await _repository.InsertAsync(card2);
        await _repository.DeleteAsync(card2.CardIdm);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.Should().HaveCount(1);
        result.First().CardIdm.Should().Be(card1.CardIdm);
    }

    #endregion

    #region GetByIdmAsync テスト

    /// <summary>
    /// 存在するカードをIDmで取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_ExistingCard_ReturnsCard()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);

        // Act
        var result = await _repository.GetByIdmAsync(card.CardIdm);

        // Assert
        result.Should().NotBeNull();
        result!.CardIdm.Should().Be(card.CardIdm);
        result.CardType.Should().Be(card.CardType);
        result.CardNumber.Should().Be(card.CardNumber);
    }

    /// <summary>
    /// 存在しないカードIDmでnullを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_NonExistingCard_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdmAsync("NOTEXISTINGIDM00");

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// 論理削除されたカードはデフォルトで取得されないことを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_DeletedCard_ReturnsNull()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.DeleteAsync(card.CardIdm);

        // Act
        var result = await _repository.GetByIdmAsync(card.CardIdm);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// includeDeletedオプションで論理削除されたカードも取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_DeletedCard_WithIncludeDeleted_ReturnsCard()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.DeleteAsync(card.CardIdm);

        // Act
        var result = await _repository.GetByIdmAsync(card.CardIdm, includeDeleted: true);

        // Assert
        result.Should().NotBeNull();
        result!.CardIdm.Should().Be(card.CardIdm);
        result.IsDeleted.Should().BeTrue();
    }

    #endregion

    #region GetAvailableAsync テスト

    /// <summary>
    /// 貸出可能なカードのみ取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAvailableAsync_ReturnsOnlyNonLentCards()
    {
        // Arrange
        var card1 = CreateTestCard("0102030405060708", "はやかけん", "H001");
        var card2 = CreateTestCard("0102030405060709", "nimoca", "N001");
        await _repository.InsertAsync(card1);
        await _repository.InsertAsync(card2);
        await _repository.UpdateLentStatusAsync(card2.CardIdm, true, DateTime.Now, null);

        // Act
        var result = await _repository.GetAvailableAsync();

        // Assert
        result.Should().HaveCount(1);
        result.First().CardIdm.Should().Be(card1.CardIdm);
    }

    #endregion

    #region GetLentAsync テスト

    /// <summary>
    /// 貸出中のカードのみ取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetLentAsync_ReturnsOnlyLentCards()
    {
        // Arrange
        var card1 = CreateTestCard("0102030405060708", "はやかけん", "H001");
        var card2 = CreateTestCard("0102030405060709", "nimoca", "N001");
        await _repository.InsertAsync(card1);
        await _repository.InsertAsync(card2);
        await _repository.UpdateLentStatusAsync(card2.CardIdm, true, DateTime.Now, null);

        // Act
        var result = await _repository.GetLentAsync();

        // Assert
        result.Should().HaveCount(1);
        result.First().CardIdm.Should().Be(card2.CardIdm);
        result.First().IsLent.Should().BeTrue();
    }

    #endregion

    #region InsertAsync テスト

    /// <summary>
    /// カードを正常に登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_ValidCard_ReturnsTrue()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");

        // Act
        var result = await _repository.InsertAsync(card);

        // Assert
        result.Should().BeTrue();

        var inserted = await _repository.GetByIdmAsync(card.CardIdm);
        inserted.Should().NotBeNull();
        inserted!.CardType.Should().Be(card.CardType);
        inserted.CardNumber.Should().Be(card.CardNumber);
    }

    /// <summary>
    /// 重複するIDmでの登録はエラーになることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_DuplicateIdm_ReturnsFalse()
    {
        // Arrange
        var card1 = CreateTestCard("0102030405060708", "はやかけん", "H001");
        var card2 = CreateTestCard("0102030405060708", "nimoca", "N001"); // 同じIDm
        await _repository.InsertAsync(card1);

        // Act
        var result = await _repository.InsertAsync(card2);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// メモ付きカードを登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_WithNote_SavesNote()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        card.Note = "テストメモ";

        // Act
        await _repository.InsertAsync(card);

        // Assert
        var inserted = await _repository.GetByIdmAsync(card.CardIdm);
        inserted!.Note.Should().Be("テストメモ");
    }

    #endregion

    #region UpdateAsync テスト

    /// <summary>
    /// カード情報を更新できることを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ValidCard_ReturnsTrue()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);

        card.CardNumber = "H002";
        card.Note = "更新テスト";

        // Act
        var result = await _repository.UpdateAsync(card);

        // Assert
        result.Should().BeTrue();

        var updated = await _repository.GetByIdmAsync(card.CardIdm);
        updated!.CardNumber.Should().Be("H002");
        updated.Note.Should().Be("更新テスト");
    }

    /// <summary>
    /// 存在しないカードの更新はfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_NonExistingCard_ReturnsFalse()
    {
        // Arrange
        var card = CreateTestCard("NOTEXISTINGIDM00", "はやかけん", "H001");

        // Act
        var result = await _repository.UpdateAsync(card);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 論理削除されたカードは更新できないことを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_DeletedCard_ReturnsFalse()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.DeleteAsync(card.CardIdm);

        card.CardNumber = "H002";

        // Act
        var result = await _repository.UpdateAsync(card);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region UpdateLentStatusAsync テスト

    /// <summary>
    /// 貸出状態を更新できることを確認
    /// </summary>
    [Fact]
    public async Task UpdateLentStatusAsync_SetLent_UpdatesCorrectly()
    {
        // Arrange
        await _staffRepository.InsertAsync(CreateTestStaff());
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        var lentAt = DateTime.Now;

        // Act
        var result = await _repository.UpdateLentStatusAsync(card.CardIdm, true, lentAt, TestStaffIdm);

        // Assert
        result.Should().BeTrue();

        var updated = await _repository.GetByIdmAsync(card.CardIdm);
        updated!.IsLent.Should().BeTrue();
        updated.LastLentAt.Should().NotBeNull();
        updated.LastLentStaff.Should().Be(TestStaffIdm);
    }

    /// <summary>
    /// 返却状態を更新できることを確認
    /// </summary>
    [Fact]
    public async Task UpdateLentStatusAsync_SetReturned_UpdatesCorrectly()
    {
        // Arrange
        await _staffRepository.InsertAsync(CreateTestStaff());
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.UpdateLentStatusAsync(card.CardIdm, true, DateTime.Now, TestStaffIdm);

        // Act
        var result = await _repository.UpdateLentStatusAsync(card.CardIdm, false, null, null);

        // Assert
        result.Should().BeTrue();

        var updated = await _repository.GetByIdmAsync(card.CardIdm);
        updated!.IsLent.Should().BeFalse();
    }

    #endregion

    #region DeleteAsync テスト

    /// <summary>
    /// カードを論理削除できることを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NonLentCard_ReturnsTrue()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);

        // Act
        var result = await _repository.DeleteAsync(card.CardIdm);

        // Assert
        result.Should().Be(ICCardManager.Data.Repositories.CardOperationResult.Success);

        var deleted = await _repository.GetByIdmAsync(card.CardIdm, includeDeleted: true);
        deleted!.IsDeleted.Should().BeTrue();
        deleted.DeletedAt.Should().NotBeNull();
    }

    /// <summary>
    /// 貸出中のカードは削除できないことを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_LentCard_ReturnsCardIsLent()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.UpdateLentStatusAsync(card.CardIdm, true, DateTime.Now, null);

        // Act
        var result = await _repository.DeleteAsync(card.CardIdm);

        // Assert
        result.Should().Be(ICCardManager.Data.Repositories.CardOperationResult.CardIsLent);

        var notDeleted = await _repository.GetByIdmAsync(card.CardIdm);
        notDeleted.Should().NotBeNull();
        notDeleted!.IsDeleted.Should().BeFalse();
    }

    /// <summary>
    /// 存在しないカードの削除はNotFoundを返すことを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NonExistingCard_ReturnsNotFound()
    {
        // Act
        var result = await _repository.DeleteAsync("NOTEXISTINGIDM00");

        // Assert
        result.Should().Be(ICCardManager.Data.Repositories.CardOperationResult.NotFound);
    }

    /// <summary>
    /// 既に削除済みのカードを再度削除するとConflictを返すことを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_AlreadyDeletedCard_ReturnsConflict()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.DeleteAsync(card.CardIdm); // 1回目の削除

        // Act
        var result = await _repository.DeleteAsync(card.CardIdm); // 2回目の削除

        // Assert
        result.Should().Be(ICCardManager.Data.Repositories.CardOperationResult.Conflict);
    }

    /// <summary>
    /// 払戻済のカードを削除するとConflictを返すことを確認
    /// </summary>
    [Fact]
    public async Task SetRefundedAsync_Success_ReturnsSuccess()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);

        // Act
        var result = await _repository.SetRefundedAsync(card.CardIdm);

        // Assert
        result.Should().Be(ICCardManager.Data.Repositories.CardOperationResult.Success);
    }

    /// <summary>
    /// 貸出中のカードを払い戻すとCardIsLentを返すことを確認
    /// </summary>
    [Fact]
    public async Task SetRefundedAsync_LentCard_ReturnsCardIsLent()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.UpdateLentStatusAsync(card.CardIdm, true, DateTime.Now, null);

        // Act
        var result = await _repository.SetRefundedAsync(card.CardIdm);

        // Assert
        result.Should().Be(ICCardManager.Data.Repositories.CardOperationResult.CardIsLent);
    }

    /// <summary>
    /// 既に払戻済のカードを再度払い戻すとConflictを返すことを確認
    /// </summary>
    [Fact]
    public async Task SetRefundedAsync_AlreadyRefunded_ReturnsConflict()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.SetRefundedAsync(card.CardIdm);

        // Act
        var result = await _repository.SetRefundedAsync(card.CardIdm);

        // Assert
        result.Should().Be(ICCardManager.Data.Repositories.CardOperationResult.Conflict);
    }

    #endregion

    #region ExistsAsync テスト

    /// <summary>
    /// 存在するカードでtrueを返すことを確認
    /// </summary>
    [Fact]
    public async Task ExistsAsync_ExistingCard_ReturnsTrue()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);

        // Act
        var result = await _repository.ExistsAsync(card.CardIdm);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// 存在しないカードでfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task ExistsAsync_NonExistingCard_ReturnsFalse()
    {
        // Act
        var result = await _repository.ExistsAsync("NOTEXISTINGIDM00");

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 論理削除されたカードでもtrueを返すことを確認（物理的には存在する）
    /// </summary>
    [Fact]
    public async Task ExistsAsync_DeletedCard_ReturnsTrue()
    {
        // Arrange
        var card = CreateTestCard("0102030405060708", "はやかけん", "H001");
        await _repository.InsertAsync(card);
        await _repository.DeleteAsync(card.CardIdm);

        // Act
        var result = await _repository.ExistsAsync(card.CardIdm);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region GetNextCardNumberAsync テスト

    /// <summary>
    /// 最初のカード番号は1を返すことを確認
    /// </summary>
    [Fact]
    public async Task GetNextCardNumberAsync_NoCards_Returns1()
    {
        // Act
        var result = await _repository.GetNextCardNumberAsync("はやかけん");

        // Assert
        result.Should().Be("1");
    }

    /// <summary>
    /// 次のカード番号を正しく返すことを確認
    /// </summary>
    [Fact]
    public async Task GetNextCardNumberAsync_WithExistingCards_ReturnsNextNumber()
    {
        // Arrange
        var card1 = CreateTestCard("0102030405060708", "はやかけん", "1");
        var card2 = CreateTestCard("0102030405060709", "はやかけん", "2");
        await _repository.InsertAsync(card1);
        await _repository.InsertAsync(card2);

        // Act
        var result = await _repository.GetNextCardNumberAsync("はやかけん");

        // Assert
        result.Should().Be("3");
    }

    /// <summary>
    /// カード種別ごとに独立した番号を返すことを確認
    /// </summary>
    [Fact]
    public async Task GetNextCardNumberAsync_DifferentCardTypes_IndependentNumbering()
    {
        // Arrange
        var card1 = CreateTestCard("0102030405060708", "はやかけん", "5");
        var card2 = CreateTestCard("0102030405060709", "nimoca", "10");
        await _repository.InsertAsync(card1);
        await _repository.InsertAsync(card2);

        // Act
        var hayakakenNext = await _repository.GetNextCardNumberAsync("はやかけん");
        var nimocaNext = await _repository.GetNextCardNumberAsync("nimoca");
        var sugocaNext = await _repository.GetNextCardNumberAsync("SUGOCA");

        // Assert
        hayakakenNext.Should().Be("6");
        nimocaNext.Should().Be("11");
        sugocaNext.Should().Be("1");
    }

    #endregion

    #region ヘルパーメソッド

    private static IcCard CreateTestCard(string cardIdm, string cardType, string cardNumber)
    {
        return new IcCard
        {
            CardIdm = cardIdm,
            CardType = cardType,
            CardNumber = cardNumber,
            IsDeleted = false,
            IsLent = false
        };
    }

    private static Staff CreateTestStaff()
    {
        return new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        };
    }

    #endregion

    #region Issue #1167: bypassCache テスト

    /// <summary>
    /// Issue #1167: GetAvailableAsync(bypassCache=true)はキャッシュを無効化してから取得すること
    /// </summary>
    [Fact]
    public async Task GetAvailableAsync_BypassCacheTrue_InvalidatesCacheAndQueriesDb()
    {
        // Arrange
        var card = CreateTestCard("0102030405060710", "はやかけん", "H100");
        await _repository.InsertAsync(card);

        // Act
        var result = await _repository.GetAvailableAsync(bypassCache: true);

        // Assert
        result.Should().HaveCount(1);
        // bypassCache=trueの場合はInvalidateが呼ばれること
        _cacheServiceMock.Verify(c => c.Invalidate(CacheKeys.AvailableCards), Times.Once);
        // GetOrCreateAsyncは呼ばれないこと
        _cacheServiceMock.Verify(c => c.GetOrCreateAsync(
            CacheKeys.AvailableCards,
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()), Times.Never);
    }

    /// <summary>
    /// Issue #1167: GetAvailableAsync(bypassCache=false)は通常のキャッシュ経路を使うこと
    /// </summary>
    [Fact]
    public async Task GetAvailableAsync_BypassCacheFalse_UsesCache()
    {
        // Arrange
        var card = CreateTestCard("0102030405060711", "nimoca", "N100");
        await _repository.InsertAsync(card);

        // Act
        var result = await _repository.GetAvailableAsync(bypassCache: false);

        // Assert
        result.Should().HaveCount(1);
        _cacheServiceMock.Verify(c => c.Invalidate(CacheKeys.AvailableCards), Times.Never);
        _cacheServiceMock.Verify(c => c.GetOrCreateAsync(
            CacheKeys.AvailableCards,
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()), Times.Once);
    }

    /// <summary>
    /// Issue #1167: GetLentAsync(bypassCache=true)はキャッシュを無効化してから取得すること
    /// </summary>
    [Fact]
    public async Task GetLentAsync_BypassCacheTrue_InvalidatesCacheAndQueriesDb()
    {
        // Arrange
        var card = CreateTestCard("0102030405060712", "SUGOCA", "S100");
        await _repository.InsertAsync(card);
        await _staffRepository.InsertAsync(CreateTestStaff());
        await _repository.UpdateLentStatusAsync(card.CardIdm, true, DateTime.Now, TestStaffIdm);

        // Act
        var result = await _repository.GetLentAsync(bypassCache: true);

        // Assert
        result.Should().HaveCount(1);
        result.First().IsLent.Should().BeTrue();
        _cacheServiceMock.Verify(c => c.Invalidate(CacheKeys.LentCards), Times.Once);
        _cacheServiceMock.Verify(c => c.GetOrCreateAsync(
            CacheKeys.LentCards,
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()), Times.Never);
    }

    /// <summary>
    /// Issue #1167: bypassCacheのデフォルト値はfalseであること（後方互換性）
    /// </summary>
    [Fact]
    public async Task GetAvailableAsync_DefaultBypassCache_UsesCache()
    {
        // Arrange
        var card = CreateTestCard("0102030405060713", "はやかけん", "H101");
        await _repository.InsertAsync(card);

        // Act: bypassCacheパラメータを指定せずに呼び出す
        var result = await _repository.GetAvailableAsync();

        // Assert
        result.Should().HaveCount(1);
        _cacheServiceMock.Verify(c => c.Invalidate(CacheKeys.AvailableCards), Times.Never);
        _cacheServiceMock.Verify(c => c.GetOrCreateAsync(
            CacheKeys.AvailableCards,
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()), Times.Once);
    }

    #endregion
}
