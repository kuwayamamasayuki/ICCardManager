using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1953: <c>CardRepository.UpdateLentStatusAsync</c> の影響行数 0（競合）を
/// 貸出・返却・整合性修復の各経路が握りつぶさないことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// <c>UPDATE ic_card … WHERE card_idm = @cardIdm AND is_deleted = 0</c> が 0 行になるのは
/// 「他 PC がこのカードを論理削除した」場合だけで（Issue #1753 の影響行数による競合検出）、
/// 原因を特定できる<b>競合</b>である。戻り値を捨てると:
/// </para>
/// <list type="bullet">
/// <item><description>貸出: <c>ledger</c> に貸出中レコードだけが入り <c>is_lent = 0</c> のままコミットされ、
/// 手元に無いカードが次のタッチで<b>新規貸出として再記録される</b></description></item>
/// <item><description>返却: <c>is_lent = 1</c> が残り、返却済みカードが<b>長期未返却として督促され続ける</b></description></item>
/// <item><description>整合性修復: DB が変わっていないのに <c>repairCount</c> が増え「修復しました」と報告する</description></item>
/// </list>
/// <para>
/// <c>.claude/rules/business-logic.md</c> が「<c>ic_card.is_lent</c> と貸出中レコードは<b>一時的に</b>ずれる」
/// と記録している状態が、恒久的に発生する。
/// </para>
/// <para>
/// 「欠陥を突く側」と「正当な貸出・返却を塞いでいない側」を対で置く。後者が無いと、
/// 貸出・返却を無条件に失敗させる実装でも緑になる。
/// </para>
/// </remarks>
public sealed class LendingServiceLentStatusConflictTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";

    private readonly DbContext _dbContext;
    private readonly CardRepository _realCardRepository;
    private readonly LedgerRepository _ledgerRepository;
    private readonly StaffRepository _staffRepository;
    private readonly SettingsRepository _settingsRepository;

    public LendingServiceLentStatusConflictTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        var cacheOptions = Options.Create(new CacheOptions());
        var cacheService = CreatePassThroughCacheService();

        _ledgerRepository = new LedgerRepository(_dbContext);
        _realCardRepository = new CardRepository(_dbContext, cacheService, cacheOptions, NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(_dbContext, cacheService, cacheOptions, NullLogger<StaffRepository>.Instance);
        _settingsRepository = new SettingsRepository(_dbContext, cacheService, cacheOptions);

        _staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = "テスト職員",
            Number = "001",
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        _realCardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 貸出

    /// <summary>
    /// 貸出中に <c>is_lent</c> 更新が 0 行なら、貸出中レコードごと巻き戻すこと
    /// </summary>
    [Fact]
    public async Task LendAsync_貸出状態の更新が0行_貸出中レコードごと巻き戻すこと()
    {
        // Arrange: is_lent=true への更新だけを「0 行」（＝他 PC がカードを削除）にする
        var service = CreateService(CreateCardRepositoryMock(conflictOn: isLent => isLent).Object);

        // Act
        var result = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        // Assert
        result.Success.Should().BeFalse("影響行数 0 は競合であり、貸出は成立していない");
        (await CountLentRecordsAsync()).Should().Be(0,
            "貸出中レコードだけが残ると、手元に無いカードが次のタッチで新規貸出として再記録される");
    }

    /// <summary>
    /// 対の表明: 更新が 1 行なら従来どおり貸出が成立すること
    /// </summary>
    [Fact]
    public async Task LendAsync_貸出状態の更新が成功_従来どおり貸出が成立すること()
    {
        var service = CreateService(CreateCardRepositoryMock(conflictOn: _ => false).Object);

        var result = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        result.Success.Should().BeTrue($"正当な貸出を塞いでいないこと（{result.ErrorMessage}）");
        (await CountLentRecordsAsync()).Should().Be(1);
    }

    /// <summary>
    /// 競合の案内が「なぜ」「どうすれば」を含み、生の英語文言を出さないこと（#1614 / #1817）
    /// </summary>
    [Fact]
    public async Task LendAsync_貸出状態の更新が0行_原因と回復手段を示す文言を返すこと()
    {
        var service = CreateService(CreateCardRepositoryMock(conflictOn: isLent => isLent).Object);

        var result = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("削除", "「なぜ」＝カードが削除された可能性を述べること");
        result.ErrorMessage.Should().Contain("カード管理", "「どうすれば」＝状態を確認できる画面へ誘導すること");
        result.ErrorMessage.Should().NotContain("もう一度タッチ",
            "再タッチしても同じ競合が続くため、既定分岐の汎用文言へ落ちていないこと");

        // トーストは幅上限（520px 固定）で折り返しつつ高さ上限で切られるため、長文は末尾＝
        // 「どうすれば」が失われる。LendingService.GetUserFriendlyErrorMessage は同じ理由で
        // ExceptionMessageFormatter.ToUserMessage の 58 文字の文言を退けている。
        // 上限は本番の定数から読まずリテラルで書く（定数から読むと期待値が実装と一緒に動き、
        // 表明が自己充足して常に緑になる。Issue #1884）。
        result.ErrorMessage.Length.Should().BeLessOrEqualTo(48,
            "トースト通知に収まる長さに保つこと（error-messages.md のトースト節）");
    }

    #endregion

    #region 返却

    /// <summary>
    /// 返却中に <c>is_lent</c> 更新が 0 行なら、履歴の記録ごと巻き戻すこと
    /// </summary>
    [Fact]
    public async Task ReturnAsync_貸出状態の更新が0行_返却を巻き戻すこと()
    {
        // Arrange: 貸出は成立させ、返却時（is_lent=false）の更新だけを 0 行にする
        var conflictOnReturn = false;
        var service = CreateService(
            CreateCardRepositoryMock(conflictOn: isLent => conflictOnReturn && !isLent).Object);

        var lendResult = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);
        lendResult.Success.Should().BeTrue($"前提の貸出が失敗（{lendResult.ErrorMessage}）");

        conflictOnReturn = true;

        // Act
        var result = await service.ReturnAsync(
            TestStaffIdm, TestCardIdm, new List<LedgerDetail>(), skipDuplicateCheck: true);

        // Assert
        result.Success.Should().BeFalse("影響行数 0 は競合であり、返却は成立していない");
        (await CountLentRecordsAsync()).Should().Be(1,
            "貸出中レコードだけが消えると、返却済みカードが長期未返却として督促され続ける");
    }

    /// <summary>
    /// 対の表明: 更新が 1 行なら従来どおり返却が成立すること
    /// </summary>
    [Fact]
    public async Task ReturnAsync_貸出状態の更新が成功_従来どおり返却が成立すること()
    {
        var service = CreateService(CreateCardRepositoryMock(conflictOn: _ => false).Object);

        var lendResult = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);
        lendResult.Success.Should().BeTrue($"前提の貸出が失敗（{lendResult.ErrorMessage}）");

        var result = await service.ReturnAsync(
            TestStaffIdm, TestCardIdm, new List<LedgerDetail>(), skipDuplicateCheck: true);

        result.Success.Should().BeTrue($"正当な返却を塞いでいないこと（{result.ErrorMessage}）");
        (await CountLentRecordsAsync()).Should().Be(0);
    }

    #endregion

    #region 整合性修復

    /// <summary>
    /// 修復の <c>UPDATE</c> が 0 行なら修復件数に数えないこと
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_更新が0行_修復件数に数えないこと()
    {
        var service = CreateService(
            CreateRepairCardRepositoryMock(updateSucceeds: false).Object,
            CreateEmptyLentRecordsRepositoryMock().Object);

        var repaired = await service.RepairLentStatusConsistencyAsync();

        repaired.Should().Be(0,
            "DB が変わっていないのに「修復しました」と報告すると、不整合が残ったまま解決済みに見える");
    }

    /// <summary>
    /// 対の表明: 更新が 1 行なら従来どおり修復件数に数えること
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_更新が成功_従来どおり修復件数に数えること()
    {
        var service = CreateService(
            CreateRepairCardRepositoryMock(updateSucceeds: true).Object,
            CreateEmptyLentRecordsRepositoryMock().Object);

        var repaired = await service.RepairLentStatusConsistencyAsync();

        repaired.Should().Be(1, "正当な修復を数えなくなっていないこと");
    }

    #endregion

    #region キャッシュ無効化（Issue #1759）

    /// <summary>
    /// 影響行数 0（削除済みカード）でもカードキャッシュを破棄すること
    /// </summary>
    /// <remarks>
    /// 0 行は「手元のカード一覧が古い」と確定した瞬間であり、書き込みが成功したときより
    /// 無効化の根拠が強い（Issue #1759）。破棄しないと、競合を検出した画面が一覧を
    /// 再読込しても削除済みのカードを含む古い一覧が返る。
    /// </remarks>
    [Fact]
    public async Task UpdateLentStatusAsync_影響行数0_カードキャッシュを破棄すること()
    {
        // Arrange: 実 DB 上でカードを論理削除し、WHERE is_deleted = 0 に一致しない状態を作る
        var cacheServiceMock = CreatePassThroughCacheServiceMock();
        var repository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        await DeleteCardAsync();
        cacheServiceMock.Invocations.Clear();

        // Act
        var updated = await repository.UpdateLentStatusAsync(TestCardIdm, true, DateTime.Now, TestStaffIdm);

        // Assert
        updated.Should().BeFalse("削除済みカードは WHERE is_deleted = 0 に一致しない");
        cacheServiceMock.Verify(
            c => c.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation), Times.Once,
            "0 行は一覧が古いと確定した瞬間であり、ここで破棄しないと再読込が事実にならない");
    }

    /// <summary>
    /// 対の表明: 更新が成功したときも従来どおり破棄すること
    /// </summary>
    [Fact]
    public async Task UpdateLentStatusAsync_影響行数1_カードキャッシュを破棄すること()
    {
        var cacheServiceMock = CreatePassThroughCacheServiceMock();
        var repository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        cacheServiceMock.Invocations.Clear();

        var updated = await repository.UpdateLentStatusAsync(TestCardIdm, true, DateTime.Now, TestStaffIdm);

        updated.Should().BeTrue();
        cacheServiceMock.Verify(
            c => c.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation), Times.Once);
    }

    #endregion

    #region ヘルパー

    private LendingService CreateService(
        ICardRepository cardRepository, ILedgerRepository ledgerRepository = null)
        => new LendingService(
            _dbContext,
            cardRepository,
            _staffRepository,
            ledgerRepository ?? _ledgerRepository,
            _settingsRepository,
            new SummaryGenerator(DepartmentType.MayorOffice),
            new CardLockManager(NullLogger<CardLockManager>.Instance),
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            NullLogger<LendingService>.Instance);

    /// <summary>
    /// <c>UpdateLentStatusAsync</c> だけを差し替え、他は実リポジトリへ委譲するモック。
    /// </summary>
    /// <param name="conflictOn">
    /// 引数は更新後の <c>isLent</c>。true を返した呼び出しでは DB を変更せず
    /// <c>false</c>（影響行数 0）を返す（＝他 PC がカードを論理削除した状態）。
    /// </param>
    private Mock<ICardRepository> CreateCardRepositoryMock(Func<bool, bool> conflictOn)
    {
        var mock = new Mock<ICardRepository>();

        mock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((idm, includeDeleted)
                => _realCardRepository.GetByIdmAsync(idm, includeDeleted));
        mock.Setup(r => r.GetAllAsync())
            .Returns(() => _realCardRepository.GetAllAsync());
        mock.Setup(r => r.InvalidateCache())
            .Callback(() => _realCardRepository.InvalidateCache());

        mock.Setup(r => r.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .Returns<string, bool, DateTime?, string>((idm, isLent, lentAt, lenderIdm)
                => conflictOn(isLent)
                    ? Task.FromResult(false)
                    : _realCardRepository.UpdateLentStatusAsync(idm, isLent, lentAt, lenderIdm));

        return mock;
    }

    /// <summary>
    /// 整合性修復用: 「貸出中レコードが無いのに is_lent=1」のカード 1 枚を返すモック
    /// </summary>
    private static Mock<ICardRepository> CreateRepairCardRepositoryMock(bool updateSucceeds)
    {
        var mock = new Mock<ICardRepository>();
        mock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>
        {
            new IcCard
            {
                CardIdm = TestCardIdm,
                CardType = "はやかけん",
                CardNumber = "H-001",
                IsLent = true,
            },
        });
        mock.Setup(r => r.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .ReturnsAsync(updateSucceeds);
        return mock;
    }

    private static Mock<ILedgerRepository> CreateEmptyLentRecordsRepositoryMock()
    {
        var mock = new Mock<ILedgerRepository>();
        mock.Setup(r => r.GetAllLentRecordsAsync()).ReturnsAsync(new List<Ledger>());
        return mock;
    }

    private async Task DeleteCardAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "UPDATE ic_card SET is_deleted = 1 WHERE card_idm = @idm";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountLentRecordsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE card_idm = @idm AND is_lent_record = 1";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static ICacheService CreatePassThroughCacheService()
        => CreatePassThroughCacheServiceMock().Object;

    private static Mock<ICacheService> CreatePassThroughCacheServiceMock()
    {
        var mock = new Mock<ICacheService>();
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<AppSettings>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<AppSettings>> factory, TimeSpan _) => factory());
        return mock;
    }

    #endregion
}
