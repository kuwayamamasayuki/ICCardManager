using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


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

        _repository = new StaffRepository(_dbContext, _cacheServiceMock.Object);
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
    [Fact]
    public async Task UpdateAsync_WithNullName_StillRequiresName()
    {
        // Arrange
        var staff = CreateTestStaff("STAFF00000000001", "山田太郎", "001");
        await _repository.InsertAsync(staff);

        // 更新後も名前が残っていることを確認
        staff.Number = null;
        await _repository.UpdateAsync(staff);

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
