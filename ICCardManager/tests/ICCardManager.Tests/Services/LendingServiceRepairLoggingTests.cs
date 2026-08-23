using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1852: 貸出状態の整合性修復ログ（Issue #790）が交通系ICカードの IDm を
/// マスクして出力することを検証する。
/// </summary>
/// <remarks>
/// <para>
/// <c>RepairLentStatusConsistencyAsync</c> は<b>起動時に毎回</b>実行され、共有モードでは
/// 他 PC の返却が反映されていない状態で不整合が起きやすい。ログファイルは業務 PC 上の
/// 平文ファイルであり（インストーラが <c>users-full</c> ACL を付与）、IDm を生で残すことは
/// CWE-532 に当たる（Issue #1704 で <c>IdmMasker</c> を導入した理由）。
/// </para>
/// <para>
/// 静的検査（<c>IdmLoggingMaskConventionTests</c>）は「ソース上でマスクを通しているか」を
/// 見るが、実際に出力される文字列が生の IDm を含まないことは本テストで表明する。
/// </para>
/// </remarks>
public sealed class LendingServiceRepairLoggingTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly CardRepository _cardRepository;
    private readonly LendingService _service;
    private readonly Mock<ILogger<LendingService>> _loggerMock = new();

    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";

    public LendingServiceRepairLoggingTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<AppSettings>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<AppSettings>> factory, TimeSpan _) => factory());

        var cacheOptions = Options.Create(new CacheOptions());

        var ledgerRepo = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        var staffRepo = new StaffRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        var settingsRepo = new SettingsRepository(_dbContext, cacheServiceMock.Object, cacheOptions);

        _service = new LendingService(
            _dbContext,
            _cardRepository,
            staffRepo,
            ledgerRepo,
            settingsRepo,
            new SummaryGenerator(DepartmentType.MayorOffice),
            new CardLockManager(NullLogger<CardLockManager>.Instance),
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            _loggerMock.Object);

        staffRepo.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = "テスト職員",
            Number = "001",
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        _cardRepository.InsertAsync(new IcCard
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

    /// <summary>
    /// 貸出中レコードがあるのに is_lent=0（0→1 の修復）のログが IDm をマスクすること
    /// </summary>
    [Fact]
    public async Task 修復ログ_貸出中へ戻す経路でIDmをマスクすること()
    {
        // Arrange: 貸出後に is_lent だけ 0 へ落とし、貸出中レコードとの不整合を作る
        var lendResult = await _service.LendAsync(TestStaffIdm, TestCardIdm, 5000);
        lendResult.Success.Should().BeTrue($"貸出が失敗（{lendResult.ErrorMessage}）");
        await _cardRepository.UpdateLentStatusAsync(TestCardIdm, false, null, null);

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(1, "貸出中レコードがあるのに is_lent=0 の 1 件が修復される");
        VerifyRepairLogIsMasked("is_lent: 0→1");
    }

    /// <summary>
    /// 貸出中レコードがないのに is_lent=1（1→0 の修復）のログが IDm をマスクすること
    /// </summary>
    [Fact]
    public async Task 修復ログ_返却済みへ戻す経路でIDmをマスクすること()
    {
        // Arrange: 貸出中レコードを作らずに is_lent だけ 1 にする
        await _cardRepository.UpdateLentStatusAsync(TestCardIdm, true, DateTime.Now, TestStaffIdm);

        // Act
        var repairCount = await _service.RepairLentStatusConsistencyAsync();

        // Assert
        repairCount.Should().Be(1, "貸出中レコードがないのに is_lent=1 の 1 件が修復される");
        VerifyRepairLogIsMasked("is_lent: 1→0");
    }

    /// <summary>
    /// 修復ログが「生の IDm を含まず」「マスク済みの値を含む」ことを対で表明する。
    /// </summary>
    /// <remarks>
    /// 「生の IDm を含まない」だけを見ると、IDm ごとログから落とした実装でも緑になる。
    /// 相関のための先頭 4 文字（<c>IdmMasker.VisiblePrefixLength</c>）は残す必要がある。
    /// </remarks>
    private void VerifyRepairLogIsMasked(string direction)
    {
        var masked = IdmMasker.Mask(TestCardIdm);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(direction)
                                              && v.ToString().Contains(masked)
                                              && !v.ToString().Contains(TestCardIdm)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            $"整合性修復ログ（{direction}）は IdmMasker を通した値だけを残すこと（Issue #1704 / #1852）");
    }
}
