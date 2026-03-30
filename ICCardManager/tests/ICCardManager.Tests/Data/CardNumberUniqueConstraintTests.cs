using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1106: カード種別＋管理番号のユニーク制約テスト
/// 共有フォルダモードで複数PCから同時にカード登録した際の番号重複を防止する。
/// </summary>
public class CardNumberUniqueConstraintTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly CardRepository _repository;
    private readonly Mock<ICacheService> _cacheServiceMock;

    public CardNumberUniqueConstraintTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheServiceMock = new Mock<ICacheService>();
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());

        _repository = new CardRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region UNIQUE制約の基本動作テスト

    /// <summary>
    /// 同一種別・同一番号のカード登録でDuplicateCardNumberExceptionがスローされることを確認
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InsertAsync_DuplicateCardTypeAndNumber_ThrowsDuplicateCardNumberException()
    {
        // Arrange
        var card1 = CreateTestCard("CARD000000000001", "はやかけん", "1");
        var card2 = CreateTestCard("CARD000000000002", "はやかけん", "1");

        await _repository.InsertAsync(card1);

        // Act & Assert
        var act = async () => await _repository.InsertAsync(card2);

        var ex = await act.Should().ThrowAsync<DuplicateCardNumberException>();
        ex.Which.CardType.Should().Be("はやかけん");
        ex.Which.CardNumber.Should().Be("1");
    }

    /// <summary>
    /// 異なる種別なら同一番号でも登録できることを確認
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InsertAsync_SameNumberDifferentType_Succeeds()
    {
        // Arrange
        var card1 = CreateTestCard("CARD000000000001", "はやかけん", "1");
        var card2 = CreateTestCard("CARD000000000002", "nimoca", "1");

        // Act
        var result1 = await _repository.InsertAsync(card1);
        var result2 = await _repository.InsertAsync(card2);

        // Assert
        result1.Should().BeTrue();
        result2.Should().BeTrue();
    }

    /// <summary>
    /// 同一種別でも異なる番号なら登録できることを確認
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InsertAsync_DifferentNumberSameType_Succeeds()
    {
        // Arrange
        var card1 = CreateTestCard("CARD000000000001", "はやかけん", "1");
        var card2 = CreateTestCard("CARD000000000002", "はやかけん", "2");

        // Act
        var result1 = await _repository.InsertAsync(card1);
        var result2 = await _repository.InsertAsync(card2);

        // Assert
        result1.Should().BeTrue();
        result2.Should().BeTrue();
    }

    /// <summary>
    /// 削除済みカードと同じ種別・番号のカードを登録できることを確認
    /// （部分ユニークインデックスはis_deleted = 0のみ対象）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task InsertAsync_SameNumberAsDeletedCard_Succeeds()
    {
        // Arrange
        var card1 = CreateTestCard("CARD000000000001", "はやかけん", "1");
        await _repository.InsertAsync(card1);
        await _repository.DeleteAsync("CARD000000000001");

        var card2 = CreateTestCard("CARD000000000002", "はやかけん", "1");

        // Act
        var result = await _repository.InsertAsync(card2);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region マイグレーション テスト

    /// <summary>
    /// マイグレーション008でユニークインデックスが作成されることを確認
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Migration008_CreatesUniqueIndex()
    {
        // Assert: インデックスの存在を確認
        var connection = _dbContext.GetConnection();
        IndexShouldExist(connection, "idx_card_type_number_active");
    }

    /// <summary>
    /// マイグレーション008が既存の重複データを解消することを確認
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Migration008_ResolvesDuplicatesBeforeCreatingIndex()
    {
        // Arrange: インメモリDBを直接作成し、重複状態にする
        using var connection = new SQLiteConnection("Data Source=:memory:");
        connection.Open();

        // ic_cardテーブルを作成（ユニーク制約なし）
        SetupSchemaWithoutMigration008(connection);

        // 重複データを挿入
        ExecuteNonQuery(connection, "INSERT INTO ic_card (card_idm, card_type, card_number, is_deleted) VALUES ('IDM001', 'はやかけん', '1', 0)");
        ExecuteNonQuery(connection, "INSERT INTO ic_card (card_idm, card_type, card_number, is_deleted) VALUES ('IDM002', 'はやかけん', '1', 0)");

        // Act: マイグレーション008を適用
        var migration = new ICCardManager.Data.Migrations.Migration_008_AddCardTypeNumberUniqueIndex();
        using var transaction = connection.BeginTransaction();
        migration.Up(connection, transaction);
        transaction.Commit();

        // Assert: 重複が解消されていること
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT card_number FROM ic_card WHERE card_idm = 'IDM001' AND is_deleted = 0";
        var number1 = cmd.ExecuteScalar()?.ToString();

        cmd.CommandText = "SELECT card_number FROM ic_card WHERE card_idm = 'IDM002' AND is_deleted = 0";
        var number2 = cmd.ExecuteScalar()?.ToString();

        number1.Should().NotBe(number2, "重複が解消され、異なる番号が割り当てられているべき");
    }

    #endregion

    #region GetNextCardNumberAsync + Insert 競合シミュレーション

    /// <summary>
    /// 同じ番号を2つのカードに割り当てようとした場合、UNIQUE制約で防止されることを確認
    /// （共有フォルダモードでの競合状態のシミュレーション）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentRegistration_SameAutoNumber_SecondInsertThrowsException()
    {
        // Arrange: 初期カードを登録
        var existingCard = CreateTestCard("CARD000000000001", "はやかけん", "1");
        await _repository.InsertAsync(existingCard);

        // Act: 2つの「PC」が同時に次の番号を取得（両方とも "2" を取得）
        var nextNumber1 = await _repository.GetNextCardNumberAsync("はやかけん");
        var nextNumber2 = await _repository.GetNextCardNumberAsync("はやかけん");

        nextNumber1.Should().Be("2");
        nextNumber2.Should().Be("2", "同時に取得すると同じ番号になる");

        // PC-Aが先にINSERT成功
        var cardA = CreateTestCard("CARD000000000002", "はやかけん", nextNumber1);
        var resultA = await _repository.InsertAsync(cardA);
        resultA.Should().BeTrue();

        // PC-BのINSERTは番号重複でDuplicateCardNumberExceptionがスロー
        var cardB = CreateTestCard("CARD000000000003", "はやかけん", nextNumber2);
        var act = async () => await _repository.InsertAsync(cardB);
        await act.Should().ThrowAsync<DuplicateCardNumberException>();

        // PC-Bが再採番してリトライ
        var retryNumber = await _repository.GetNextCardNumberAsync("はやかけん");
        retryNumber.Should().Be("3", "PC-Aの登録後は3が採番される");

        cardB.CardNumber = retryNumber;
        var resultB = await _repository.InsertAsync(cardB);
        resultB.Should().BeTrue();

        // Assert: 最終的に2枚のカードが異なる番号で登録されている
        var allCards = await _repository.GetAllAsync();
        var hayakakenCards = allCards.Where(c => c.CardType == "はやかけん").ToList();
        hayakakenCards.Should().HaveCount(3); // 元の1枚 + 新規2枚
        hayakakenCards.Select(c => c.CardNumber).Should().OnlyHaveUniqueItems();
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

    private static void IndexShouldExist(SQLiteConnection connection, string indexName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name=@name";
        cmd.Parameters.AddWithValue("@name", indexName);
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull($"インデックス '{indexName}' が存在するべき");
    }

    private static void SetupSchemaWithoutMigration008(SQLiteConnection connection)
    {
        ExecuteNonQuery(connection, @"CREATE TABLE IF NOT EXISTS ic_card (
    card_idm        TEXT PRIMARY KEY,
    card_type       TEXT NOT NULL,
    card_number     TEXT NOT NULL,
    note            TEXT,
    is_deleted      INTEGER DEFAULT 0,
    deleted_at      TEXT,
    is_lent         INTEGER DEFAULT 0,
    last_lent_at    TEXT,
    last_lent_staff TEXT,
    starting_page_number INTEGER DEFAULT 1,
    is_refunded     INTEGER DEFAULT 0,
    refunded_at     TEXT
)");
    }

    private static void ExecuteNonQuery(SQLiteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    #endregion
}
