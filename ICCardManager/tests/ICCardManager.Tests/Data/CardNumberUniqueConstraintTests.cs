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
using Microsoft.Extensions.Logging.Abstractions;

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

        _repository = new CardRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
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
    /// 既存カードの管理番号を、同一種別の別カードが使用中の番号へ更新すると
    /// <see cref="DuplicateCardNumberException"/> がスローされることを確認（Issue #1757）
    /// </summary>
    /// <remarks>
    /// 登録経路（<c>InsertAsync</c>）は UNIQUE 制約違反を捕捉して本例外へ変換するが、
    /// 更新経路には同等の catch が無く、生の <see cref="SQLiteException"/> が
    /// <c>App.OnDispatcherUnhandledException</c> まで抜けて
    /// 「予期しないエラーが発生しました。／エラーコード: SYS999」という
    /// 原因も回復手段も示さないダイアログになっていた。
    /// 同じ操作が登録では親切に案内され編集では原因不明、という非対称を解消する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateAsync_DuplicateCardTypeAndNumber_ThrowsDuplicateCardNumberException()
    {
        // Arrange: 同一種別で別番号の2枚を登録し、片方を他方の番号へ変更する
        await _repository.InsertAsync(CreateTestCard("CARD000000000001", "nimoca", "N-001"));
        await _repository.InsertAsync(CreateTestCard("CARD000000000002", "nimoca", "N-002"));

        var edited = CreateTestCard("CARD000000000002", "nimoca", "N-001");

        // Act
        var act = async () => await _repository.UpdateAsync(edited);

        // Assert: 生の SQLiteException ではなく、UI が案内文へ変換できる例外であること
        var ex = await act.Should().ThrowAsync<DuplicateCardNumberException>();
        ex.Which.CardType.Should().Be("nimoca");
        ex.Which.CardNumber.Should().Be("N-001");
    }

    /// <summary>
    /// 削除済みカードが使っていた番号へは更新できることを確認（Issue #1757）
    /// </summary>
    /// <remarks>
    /// 部分ユニークインデックス <c>idx_card_type_number_active</c> は
    /// <c>WHERE is_deleted = 0</c> のため、削除済みカードの番号は再利用できる。
    /// 例外変換を追加したことで正当な更新まで塞いでいないことを表明する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateAsync_SameNumberAsDeletedCard_Succeeds()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestCard("CARD000000000001", "nimoca", "N-001"));
        await _repository.DeleteAsync("CARD000000000001");
        await _repository.InsertAsync(CreateTestCard("CARD000000000002", "nimoca", "N-002"));

        var edited = CreateTestCard("CARD000000000002", "nimoca", "N-001");

        // Act
        var result = await _repository.UpdateAsync(edited);

        // Assert
        result.Should().BeTrue();
        var updated = await _repository.GetByIdmAsync("CARD000000000002");
        updated.CardNumber.Should().Be("N-001");
    }

    /// <summary>
    /// 自分自身の番号を変えない更新（備考だけの修正等）が成功することを確認（Issue #1757）
    /// </summary>
    /// <remarks>
    /// UPDATE は自分の行を書き換えるため UNIQUE 制約に抵触しない。例外変換の追加で
    /// この最も一般的な編集操作が壊れていないことを固定する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateAsync_UnchangedCardNumber_Succeeds()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestCard("CARD000000000001", "nimoca", "N-001"));

        var edited = CreateTestCard("CARD000000000001", "nimoca", "N-001");
        edited.Note = "備考を修正";

        // Act
        var result = await _repository.UpdateAsync(edited);

        // Assert
        result.Should().BeTrue();
        var updated = await _repository.GetByIdmAsync("CARD000000000001");
        updated.Note.Should().Be("備考を修正");
    }

    /// <summary>
    /// 削除済みカードの番号を別のカードが使っている状態で復元すると
    /// <see cref="DuplicateCardNumberException"/> がスローされることを確認（Issue #1757）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 部分ユニークインデックスが <c>WHERE is_deleted = 0</c> であることの帰結として、
    /// <b>復元（<c>is_deleted</c> を 1→0 に戻す UPDATE）も UNIQUE 制約に触れる</b>。
    /// 「削除 → 同じ番号で別カードを登録（仕様上可能）→ 元のカードを復元」で必ず違反する。
    /// 変換しないと生の <see cref="SQLiteException"/> が抜け、カード管理画面では
    /// 「予期しないエラー（SYS999）」、CSVインポートでは行番号の無い一般エラーになる。
    /// </para>
    /// <para>
    /// 案内文言は登録・更新と同じにしない。復元には管理番号の入力欄が無く
    /// 「別の番号を指定してください」は<b>実行できない指示</b>になるため、
    /// 「使用中カードの番号を変更してから復元する」という取れる行動を示す。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreAsync_NumberTakenByActiveCard_ThrowsDuplicateCardNumberException()
    {
        // Arrange: 削除 → 同じ種別・番号で別カードを登録（部分ユニークのため成功する）
        await _repository.InsertAsync(CreateTestCard("CARD000000000001", "nimoca", "N-001"));
        await _repository.DeleteAsync("CARD000000000001");
        await _repository.InsertAsync(CreateTestCard("CARD000000000002", "nimoca", "N-001"));

        // Act
        var act = async () => await _repository.RestoreAsync("CARD000000000001");

        // Assert
        var ex = await act.Should().ThrowAsync<DuplicateCardNumberException>();
        ex.Which.CardType.Should().Be("nimoca", "IDm しか持たない復元経路でも種別を読み直して報告する");
        ex.Which.CardNumber.Should().Be("N-001");
        ex.Which.UserFriendlyMessage.Should().Contain("N-001");
        ex.Which.UserFriendlyMessage.Should().NotContain("別の番号を指定してください",
            "復元経路には管理番号の入力欄が無く、実行できない指示になるため");
        ex.Which.UserFriendlyMessage.Should().EndWith("もう一度復元してください。");
    }

    /// <summary>
    /// 番号が競合しない削除済みカードは従来どおり復元できることを確認（Issue #1757）
    /// </summary>
    /// <remarks>
    /// 例外変換の追加で、最も一般的な復元操作（誤って削除したカードの復旧）を
    /// 塞いでいないことを対で固定する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreAsync_NumberNotTaken_Succeeds()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestCard("CARD000000000001", "nimoca", "N-001"));
        await _repository.DeleteAsync("CARD000000000001");

        // Act
        var result = await _repository.RestoreAsync("CARD000000000001");

        // Assert
        result.Should().BeTrue();
        var restored = await _repository.GetByIdmAsync("CARD000000000001");
        restored.Should().NotBeNull();
        restored.CardNumber.Should().Be("N-001");
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
        // Issue #1988: 生の接続を返す GetConnection() は削除済み。リースで受けて using で解放する
        // （解放しないと _activeAsyncLeaseCount / セマフォが残り、以後の SuspendConnections が止まる）。
        using var lease = _dbContext.LeaseConnection();
        IndexShouldExist(lease.Connection, "idx_card_type_number_active");
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
