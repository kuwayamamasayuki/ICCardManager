using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2322: 払戻済の交通系ICカードが、どの経路を通っても貸出中にならないことを実 DB（インメモリ SQLite）で検証する。
/// </summary>
/// <remarks>
/// <para>
/// 以前は払戻済の判定が呼び出し元（メイン画面の交通系ICカードタッチ待ち・貸出記録の作成ダイアログを開く前）に
/// 配られており、<see cref="LendingService.LendAsync"/> の事前検証も <c>UpdateLentStatusAsync</c> の WHERE も
/// <c>is_refunded</c> を見なかった。そのため職員証タッチ待ちでの再タッチ（30秒ルールの逆処理）と、
/// 貸出記録の作成ダイアログを開いた後に他 PC が払い戻した場合に、払戻済のカードが貸出中として記録された。
/// </para>
/// <para>
/// 「欠陥を突く側」（事前検証・書き込みの直前の払い戻し・起動時の修復）と
/// 「正当な操作を塞いでいない側」（払戻済でないカードの貸出・払戻済のカードを返却済みにする更新）を対で置く。
/// </para>
/// </remarks>
public sealed class LendingServiceRefundedCardTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";

    private readonly DbContext _dbContext;
    private readonly CardRepository _realCardRepository;
    private readonly LedgerRepository _ledgerRepository;
    private readonly StaffRepository _staffRepository;
    private readonly SettingsRepository _settingsRepository;

    public LendingServiceRefundedCardTests()
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

    #region 欠陥を突く側

    /// <summary>
    /// 払戻済のカードは貸出の事前検証で拒まれ、台帳にも is_lent にも何も残らないこと
    /// </summary>
    /// <remarks>
    /// メイン画面の 30秒ルールの逆処理（職員証タッチ待ち）と貸出記録の作成ダイアログの保存は、
    /// どちらもこの <see cref="LendingService.LendAsync"/> を直接呼ぶ。
    /// </remarks>
    [Fact]
    public async Task LendAsync_払戻済のカード_貸出せず理由を示すこと()
    {
        // Arrange
        await MarkRefundedAsync();
        var service = CreateService(_realCardRepository);

        // Act
        var result = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        // Assert
        result.Success.Should().BeFalse("払戻済のカードは貸出対象外（Issue #530）");
        result.ErrorMessage.Should().Contain("払い戻し済み", "「なぜ」を名指しする");
        result.ErrorMessage.Should().EndWith("ください。", "「どうすれば」で終わる");
        result.ErrorMessage!.Length.Should().BeLessOrEqualTo(48, "トーストの上限（LendingServiceLentStatusConflictTests と同じ）");
        (await CountLedgersAsync()).Should().Be(0, "払戻済のカードに貸出中レコードを作らない");
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsLent.Should().BeFalse();
        service.IsRetouchWithinTimeout(TestCardIdm).Should().BeFalse("貸出は成立していないので30秒ルールを武装しない");
    }

    /// <summary>
    /// 事前検証の読み取りの後、書き込みの前に他 PC が払い戻した場合も、貸出中レコードごと巻き戻すこと
    /// </summary>
    /// <remarks>
    /// 事前検証には払い戻し前のカード（スナップショット）を返し、DB では払戻済にしておく。
    /// 書き込み側（<c>UpdateLentStatusAsync</c> の WHERE の <c>is_refunded = 0</c>）だけが検出できる状態。
    /// </remarks>
    [Fact]
    public async Task LendAsync_事前検証の後に払い戻された_貸出中レコードごと巻き戻すこと()
    {
        // Arrange
        var beforeRefund = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        await MarkRefundedAsync();
        var cardRepository = new Mock<ICardRepository>();
        cardRepository.Setup(r => r.GetByIdmAsync(TestCardIdm, It.IsAny<bool>())).ReturnsAsync(beforeRefund);
        cardRepository.Setup(r => r.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .Returns<string, bool, DateTime?, string>((idm, isLent, lentAt, staffIdm)
                => _realCardRepository.UpdateLentStatusAsync(idm, isLent, lentAt, staffIdm));
        var service = CreateService(cardRepository.Object);

        // Act
        var result = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("払戻済", "競合の案内が払い戻しの可能性を名指しする");
        (await CountLedgersAsync()).Should().Be(0, "貸出中レコードだけが残ると、払戻済のカードが長期未返却として残り続ける");
        var card = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        card.IsLent.Should().BeFalse();
        card.IsRefunded.Should().BeTrue();
    }

    /// <summary>
    /// 貸出中にする更新は、払戻済のカードに一致しないこと（影響行数 0 ＝競合）
    /// </summary>
    [Fact]
    public async Task UpdateLentStatusAsync_払戻済のカードを貸出中にする更新_0行になること()
    {
        await MarkRefundedAsync();

        var updated = await _realCardRepository.UpdateLentStatusAsync(TestCardIdm, true, DateTime.Now, TestStaffIdm);

        updated.Should().BeFalse();
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsLent.Should().BeFalse();
    }

    /// <summary>
    /// 起動時の整合性修復は、貸出中レコードが残る払戻済のカードを貸出中へ戻さず、原因を取り違えずに記録すること
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_払戻済のカード_貸出中へ戻さず別の文言で記録すること()
    {
        // Arrange: 貸出中レコードがあるのに is_lent = 0 の払戻済カード
        await InsertLentRecordAsync();
        await MarkRefundedAsync();
        var logger = new RecordingLogger<LendingService>();
        var service = CreateService(_realCardRepository, logger);

        // Act
        var repaired = await service.RepairLentStatusConsistencyAsync();

        // Assert
        repaired.Should().Be(0);
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsLent.Should().BeFalse("払戻済のカードを貸出中にしない");
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("払戻済");
        warning.Message.Should().NotContain("削除された可能性", "他 PC の削除という別の原因として記録しない");
        warning.Message.Should().NotContain(TestCardIdm, "IDm はマスクしてログへ出す（#1852）");
    }

    #endregion

    #region 正当な操作を塞いでいない側

    /// <summary>
    /// 払戻済でないカードは従来どおり貸し出せること
    /// </summary>
    [Fact]
    public async Task LendAsync_払戻済でないカード_従来どおり貸出が成立すること()
    {
        var service = CreateService(_realCardRepository);

        var result = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        result.Success.Should().BeTrue($"正当な貸出を塞いでいないこと（{result.ErrorMessage}）");
        (await CountLedgersAsync()).Should().Be(1);
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsLent.Should().BeTrue();
    }

    /// <summary>
    /// 返却済みにする更新（isLent = false）は、払戻済のカードにも従来どおり一致すること
    /// </summary>
    /// <remarks>
    /// 起動時の修復（貸出中レコードが無いのに is_lent = 1）や返却は、払戻済かどうかに関わらず
    /// 貸出中を解く向きなので塞がない。WHERE を isLent によらず is_refunded = 0 にした実装はここで赤になる。
    /// </remarks>
    [Fact]
    public async Task UpdateLentStatusAsync_払戻済のカードを返却済みにする更新_従来どおり成功すること()
    {
        await MarkRefundedAsync(isLent: true);

        var updated = await _realCardRepository.UpdateLentStatusAsync(TestCardIdm, false, null, null);

        updated.Should().BeTrue();
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsLent.Should().BeFalse();
    }

    /// <summary>
    /// 払戻済でないカードの修復（is_lent: 0→1）は従来どおり行うこと
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_払戻済でないカード_従来どおり修復すること()
    {
        await InsertLentRecordAsync();
        var service = CreateService(_realCardRepository);

        var repaired = await service.RepairLentStatusConsistencyAsync();

        repaired.Should().Be(1);
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsLent.Should().BeTrue();
    }

    #endregion

    #region ヘルパー

    private LendingService CreateService(ICardRepository cardRepository, ILogger<LendingService>? logger = null)
        => new LendingService(
            _dbContext,
            cardRepository,
            _staffRepository,
            _ledgerRepository,
            _settingsRepository,
            new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
            new SummaryGenerator(DepartmentType.MayorOffice),
            new CardLockManager(NullLogger<CardLockManager>.Instance),
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            logger ?? NullLogger<LendingService>.Instance);

    /// <summary>
    /// 他 PC が払い戻した状態を作る（払戻台帳は本テストの関心外なので作らない）
    /// </summary>
    private async Task MarkRefundedAsync(bool isLent = false)
    {
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE ic_card SET is_refunded = 1, refunded_at = '2025-06-15 10:00:00', is_lent = @isLent WHERE card_idm = @idm";
            command.Parameters.AddWithValue("@idm", TestCardIdm);
            command.Parameters.AddWithValue("@isLent", isLent ? 1 : 0);
            await command.ExecuteNonQueryAsync();
        }

        _realCardRepository.InvalidateCache();
    }

    private async Task InsertLentRecordAsync()
    {
        var now = new DateTime(2025, 6, 10, 9, 0, 0);
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            LenderIdm = TestStaffIdm,
            Date = now,
            Summary = SummaryGenerator.GetLendingSummary(),
            Balance = 1000,
            StaffName = "テスト職員",
            LentAt = now,
            IsLentRecord = true,
        });
    }

    private async Task<int> CountLedgersAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE card_idm = @idm";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static ICacheService CreatePassThroughCacheService()
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
        return mock.Object;
    }

    #endregion
}
