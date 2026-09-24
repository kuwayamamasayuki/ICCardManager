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
using Microsoft.Extensions.Logging.Abstractions;


namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// StaffRepositoryの単体テスト
/// </summary>
public class StaffRepositoryTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly StaffRepository _repository;

    public StaffRepositoryTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheServiceMock = new Mock<ICacheService>();

        // キャッシュをバイパスしてファクトリ関数を直接実行するよう設定
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<Staff>>> factory, TimeSpan expiration) => factory());

        _repository = new StaffRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
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
    /// 登録済み職員が正しく取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAllAsync_WithStaff_ReturnsAllNonDeletedStaff()
    {
        // Arrange
        var staff1 = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        var staff2 = CreateTestStaff("STAFF00000000002", "鈴木花子", "002");
        await _repository.InsertAsync(staff1);
        await _repository.InsertAsync(staff2);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(s => s.StaffIdm == staff1.StaffIdm);
        result.Should().Contain(s => s.StaffIdm == staff2.StaffIdm);
    }

    /// <summary>
    /// 結果が名前順でソートされていることを確認
    /// </summary>
    [Fact]
    public async Task GetAllAsync_ReturnsStaffSortedByName()
    {
        // Arrange
        var staffYamada = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        var staffSuzuki = CreateTestStaff("STAFF00000000002", "鈴木花子", "002");
        var staffSato = CreateTestStaff("STAFF00000000003", "佐藤一郎", "003");
        await _repository.InsertAsync(staffYamada);
        await _repository.InsertAsync(staffSuzuki);
        await _repository.InsertAsync(staffSato);

        // Act
        var result = (await _repository.GetAllAsync()).ToList();

        // Assert
        result.Should().HaveCount(3);
        // SQLiteはUnicodeコードポイント順でソートされる（五十音順ではない）
        // 佐(U+4F50) < 山(U+5C71) < 鈴(U+9234)
        result[0].Name.Should().Be("佐藤一郎");
        result[1].Name.Should().Be("山田太郎");
        result[2].Name.Should().Be("鈴木花子");
    }

    /// <summary>
    /// 論理削除された職員は取得されないことを確認
    /// </summary>
    [Fact]
    public async Task GetAllAsync_ExcludesDeletedStaff()
    {
        // Arrange
        var staff1 = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        var staff2 = CreateTestStaff("STAFF00000000002", "鈴木花子", "002");
        await _repository.InsertAsync(staff1);
        await _repository.InsertAsync(staff2);
        await _repository.DeleteAsync(staff2.StaffIdm);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        result.Should().HaveCount(1);
        result.First().StaffIdm.Should().Be(staff1.StaffIdm);
    }

    #endregion

    #region GetByIdmAsync テスト

    /// <summary>
    /// 存在する職員をIDmで取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_ExistingStaff_ReturnsStaff()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        // Act
        var result = await _repository.GetByIdmAsync(staff.StaffIdm);

        // Assert
        result.Should().NotBeNull();
        result!.StaffIdm.Should().Be(staff.StaffIdm);
        result.Name.Should().Be(staff.Name);
        result.Number.Should().Be(staff.Number);
    }

    /// <summary>
    /// 存在しない職員IDmでnullを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_NonExistingStaff_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdmAsync("NOTEXISTINGIDM00");

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// 論理削除された職員はデフォルトで取得されないことを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_DeletedStaff_ReturnsNull()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);

        // Act
        var result = await _repository.GetByIdmAsync(staff.StaffIdm);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// includeDeletedオプションで論理削除された職員も取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetByIdmAsync_DeletedStaff_WithIncludeDeleted_ReturnsStaff()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);

        // Act
        var result = await _repository.GetByIdmAsync(staff.StaffIdm, includeDeleted: true);

        // Assert
        result.Should().NotBeNull();
        result!.StaffIdm.Should().Be(staff.StaffIdm);
        result.IsDeleted.Should().BeTrue();
        result.DeletedAt.Should().NotBeNull();
    }

    #endregion

    #region InsertAsync テスト

    /// <summary>
    /// 職員を正常に登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_ValidStaff_ReturnsTrue()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");

        // Act
        var result = await _repository.InsertAsync(staff);

        // Assert
        result.Should().BeTrue();

        var inserted = await _repository.GetByIdmAsync(staff.StaffIdm);
        inserted.Should().NotBeNull();
        inserted!.Name.Should().Be(staff.Name);
        inserted.Number.Should().Be(staff.Number);
    }

    /// <summary>
    /// 重複するIDmでの登録はエラーになることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_DuplicateIdm_ReturnsFalse()
    {
        // Arrange
        var staff1 = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        var staff2 = CreateTestStaff("STAFF00000000001", "鈴木花子", "002"); // 同じIDm
        await _repository.InsertAsync(staff1);

        // Act
        var result = await _repository.InsertAsync(staff2);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 職員番号がnullでも登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_WithNullNumber_SavesCorrectly()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", null);

        // Act
        var result = await _repository.InsertAsync(staff);

        // Assert
        result.Should().BeTrue();

        var inserted = await _repository.GetByIdmAsync(staff.StaffIdm);
        inserted!.Number.Should().BeNull();
    }

    /// <summary>
    /// メモ付き職員を登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_WithNote_SavesNote()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        staff.Note = "テストメモ";

        // Act
        await _repository.InsertAsync(staff);

        // Assert
        var inserted = await _repository.GetByIdmAsync(staff.StaffIdm);
        inserted!.Note.Should().Be("テストメモ");
    }

    #endregion

    #region UpdateAsync テスト

    /// <summary>
    /// 職員情報を更新できることを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ValidStaff_ReturnsTrue()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        staff.Name = "山田次郎";
        staff.Number = "999";
        staff.Note = "更新テスト";

        // Act
        var result = await _repository.UpdateAsync(staff);

        // Assert
        result.Should().BeTrue();

        var updated = await _repository.GetByIdmAsync(staff.StaffIdm);
        updated!.Name.Should().Be("山田次郎");
        updated.Number.Should().Be("999");
        updated.Note.Should().Be("更新テスト");
    }

    /// <summary>
    /// 存在しない職員の更新はfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_NonExistingStaff_ReturnsFalse()
    {
        // Arrange
        var staff = CreateTestStaff("NOTEXISTINGIDM00", "山田太郎", "001");

        // Act
        var result = await _repository.UpdateAsync(staff);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 論理削除された職員は更新できないことを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_DeletedStaff_ReturnsFalse()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);

        staff.Name = "山田次郎";

        // Act
        var result = await _repository.UpdateAsync(staff);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 名前をnullに更新はできない（必須項目）
    /// </summary>
    /// <remarks>
    /// Issue #2106: 旧版は名前ではなく職員番号を null にしており、テスト名の性質を一度も検査していなかった。
    /// 名前は staff.name の NOT NULL 制約で拒否され、DB 上の名前・職員番号はどちらも元のまま残る。
    /// 対のテスト <see cref="UpdateAsync_WithNullNumber_ClearsNumberAndKeepsName"/> で、
    /// 任意項目の null 化まで塞いでいないことを表明する。
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_WithNullName_StillRequiresName()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        staff.Name = null!;
        staff.Number = "999";

        // Act
        Func<Task> act = () => _repository.UpdateAsync(staff);

        // Assert: NOT NULL 制約違反で拒否され、行は一切変わらない
        (await act.Should().ThrowAsync<System.Data.SQLite.SQLiteException>())
            .Which.ResultCode.Should().Be(System.Data.SQLite.SQLiteErrorCode.Constraint);

        var unchanged = await _repository.GetByIdmAsync(staff.StaffIdm);
        unchanged!.Name.Should().Be("山田太郎");
        unchanged.Number.Should().Be("001");
    }

    /// <summary>
    /// 任意項目（職員番号）は null に更新でき、名前は保たれる
    /// </summary>
    [Fact]
    public async Task UpdateAsync_WithNullNumber_ClearsNumberAndKeepsName()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        staff.Number = null;

        // Act
        var result = await _repository.UpdateAsync(staff);

        // Assert
        result.Should().BeTrue();
        var updated = await _repository.GetByIdmAsync(staff.StaffIdm);
        updated!.Name.Should().Be("山田太郎");
        updated.Number.Should().BeNull();
    }

    #endregion

    #region DeleteAsync テスト

    /// <summary>
    /// 職員を論理削除できることを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_ExistingStaff_ReturnsTrue()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        // Act
        var result = await _repository.DeleteAsync(staff.StaffIdm);

        // Assert
        result.Should().BeTrue();

        var deleted = await _repository.GetByIdmAsync(staff.StaffIdm, includeDeleted: true);
        deleted!.IsDeleted.Should().BeTrue();
        deleted.DeletedAt.Should().NotBeNull();
    }

    /// <summary>
    /// 存在しない職員の削除はfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NonExistingStaff_ReturnsFalse()
    {
        // Act
        var result = await _repository.DeleteAsync("NOTEXISTINGIDM00");

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 既に削除された職員の再削除はfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_AlreadyDeletedStaff_ReturnsFalse()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);

        // Act
        var result = await _repository.DeleteAsync(staff.StaffIdm);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region RestoreAsync テスト（Issue #2107）

    /// <summary>
    /// 正当な側: 論理削除した職員を復元でき、削除日時が消えること
    /// </summary>
    [Fact]
    public async Task RestoreAsync_DeletedStaff_ReturnsTrueAndClearsDeletedAt()
    {
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);

        var result = await _repository.RestoreAsync(staff.StaffIdm);

        result.Should().BeTrue();
        var restored = await _repository.GetByIdmAsync(staff.StaffIdm);
        restored.Should().NotBeNull("復元した職員は有効な職員として取得できるべき");
        restored!.IsDeleted.Should().BeFalse();
        restored.DeletedAt.Should().BeNull();
    }

    /// <summary>
    /// 欠陥を突く側: 既に有効な職員の復元は false を返すこと（競合の検出）
    /// </summary>
    /// <remarks>
    /// 他 PC が先に復元した場合、この false が「先に復元された」という競合の案内の根拠になる（#1759）。
    /// <c>WHERE … AND is_deleted = 1</c> を消すと有効な行にも一致して true が返り、案内が出なくなる。
    /// </remarks>
    [Fact]
    public async Task RestoreAsync_ActiveStaff_ReturnsFalse()
    {
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        var result = await _repository.RestoreAsync(staff.StaffIdm);

        result.Should().BeFalse("有効な職員は復元の対象ではなく、影響行数 0 は競合を意味するため");
    }

    /// <summary>
    /// 存在しない職員の復元は false を返すこと
    /// </summary>
    [Fact]
    public async Task RestoreAsync_NonExistingStaff_ReturnsFalse()
    {
        var result = await _repository.RestoreAsync("NOTEXISTINGIDM00");

        result.Should().BeFalse();
    }

    /// <summary>
    /// トランザクション付きの復元は、コミットすれば反映され、ロールバックすれば削除状態のまま残ること
    /// </summary>
    /// <remarks>
    /// 職員 CSV 取込の復元経路が使うオーバーロード。コミットしない側の表明が無いと、
    /// トランザクションを無視して自動コミットする実装でも緑になる。
    /// </remarks>
    [Fact]
    public async Task RestoreAsync_WithTransaction_AppliesOnlyOnCommit()
    {
        var committed = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        var rolledBack = CreateTestStaff("STAFF00000000002", "鈴木花子", "002");
        await _repository.InsertAsync(committed);
        await _repository.InsertAsync(rolledBack);
        await _repository.DeleteAsync(committed.StaffIdm);
        await _repository.DeleteAsync(rolledBack.StaffIdm);

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.RestoreAsync(committed.StaffIdm, scope.Transaction)).Should().BeTrue();
            scope.Commit();
        }
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.RestoreAsync(rolledBack.StaffIdm, scope.Transaction)).Should().BeTrue();
            // Commit しない（Dispose でロールバック）
        }

        (await _repository.GetByIdmAsync(committed.StaffIdm))!.IsDeleted.Should().BeFalse();
        (await _repository.GetByIdmAsync(rolledBack.StaffIdm, includeDeleted: true))!.IsDeleted.Should().BeTrue(
            "ロールバックした復元は反映されないべき");
    }

    #endregion

    #region キャッシュ無効化（Issue #1759 / #2107）

    // 影響行数 0 は「他 PC がこの職員の状態を変えた」ことの証明であり、手元の職員一覧が古いと確定した瞬間である。
    // そこで無効化しないと、競合を案内された利用者が一覧を再読込しても古い一覧が返る（#1759）。
    // `if (result > 0)` で無効化を囲む退行は、DB の状態を見るテストでは検出できないため、キャッシュへの呼び出しで表明する。

    [Fact]
    public async Task UpdateAsync_ZeroRowsAffected_StillInvalidatesStaffCache()
    {
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);
        _cacheServiceMock.Invocations.Clear();

        var result = await _repository.UpdateAsync(CreateTestStaff(staff.StaffIdm, "山田次郎", "001"));

        result.Should().BeFalse("前提: 削除済みの職員は更新されない（影響行数 0）");
        VerifyStaffCacheInvalidated(Times.Once());
    }

    [Fact]
    public async Task DeleteAsync_ZeroRowsAffected_StillInvalidatesStaffCache()
    {
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);
        _cacheServiceMock.Invocations.Clear();

        var result = await _repository.DeleteAsync(staff.StaffIdm);

        result.Should().BeFalse("前提: 既に削除済み（影響行数 0）");
        VerifyStaffCacheInvalidated(Times.Once());
    }

    [Fact]
    public async Task RestoreAsync_ZeroRowsAffected_StillInvalidatesStaffCache()
    {
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        _cacheServiceMock.Invocations.Clear();

        var result = await _repository.RestoreAsync(staff.StaffIdm);

        result.Should().BeFalse("前提: 既に有効（影響行数 0）");
        VerifyStaffCacheInvalidated(Times.Once());
    }

    /// <summary>
    /// 対の表明: トランザクション内の更新・復元ではキャッシュを無効化しないこと
    /// </summary>
    /// <remarks>
    /// コミット前に無効化すると、並行する読み取りが未確定の値を読み直してキャッシュへ載せ得る。
    /// これが無いと、トランザクションの有無を問わず無条件に無効化する実装でも上の 3 件が緑になる。
    /// </remarks>
    [Fact]
    public async Task UpdateAndRestoreAsync_WithinTransaction_DoNotInvalidateStaffCache()
    {
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);
        _cacheServiceMock.Invocations.Clear();

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.RestoreAsync(staff.StaffIdm, scope.Transaction)).Should().BeTrue("前提: 復元が実際に行われる");
            (await _repository.UpdateAsync(CreateTestStaff(staff.StaffIdm, "山田次郎", "001"), scope.Transaction)).Should().BeTrue("前提: 更新が実際に行われる");
            scope.Commit();
        }

        VerifyStaffCacheInvalidated(Times.Never());
    }

    private void VerifyStaffCacheInvalidated(Times times)
        => _cacheServiceMock.Verify(c => c.InvalidateByPrefix(CacheKeys.StaffPrefixForInvalidation), times);

    #endregion

    #region ExistsAsync テスト

    /// <summary>
    /// 存在する職員でtrueを返すことを確認
    /// </summary>
    [Fact]
    public async Task ExistsAsync_ExistingStaff_ReturnsTrue()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        // Act
        var result = await _repository.ExistsAsync(staff.StaffIdm);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// 存在しない職員でfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task ExistsAsync_NonExistingStaff_ReturnsFalse()
    {
        // Act
        var result = await _repository.ExistsAsync("NOTEXISTINGIDM00");

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// 論理削除された職員でもtrueを返すことを確認（物理的には存在する）
    /// </summary>
    [Fact]
    public async Task ExistsAsync_DeletedStaff_ReturnsTrue()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);
        await _repository.DeleteAsync(staff.StaffIdm);

        // Act
        var result = await _repository.ExistsAsync(staff.StaffIdm);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region ヘルパーメソッド

    private static Staff CreateTestStaff(string staffIdm, string name, string? number)
    {
        return new Staff
        {
            StaffIdm = staffIdm,
            Name = name,
            Number = number,
            IsDeleted = false
        };
    }

    #endregion
}
