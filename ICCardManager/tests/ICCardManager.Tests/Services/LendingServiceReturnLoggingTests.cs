using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
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
/// Issue #1819: 返却時に台帳行が 1 行も作られなかったことを本番ログへ残すことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は「重複除外後、登録対象の履歴がありません」が <c>LogDebug</c> のみで、
/// <c>appsettings.json</c> の <c>Logging:LogLevel:Default = Information</c> により
/// 本番のログファイルには出力されなかった。「返却したのに履歴が増えない」という
/// 問い合わせの切り分けができない。
/// </para>
/// <para>
/// Issue #1730 の規約どおり既存の <c>LogDebug</c> は残したまま、
/// 返却成功かつ台帳行ゼロを Information 1 行に集約する。
/// </para>
/// </remarks>
public sealed class LendingServiceReturnLoggingTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LendingService _service;
    private readonly Mock<ILogger<LendingService>> _loggerMock = new();

    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";

    public LendingServiceReturnLoggingTests()
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
        var cardRepo = new CardRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        var staffRepo = new StaffRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        var settingsRepo = new SettingsRepository(_dbContext, cacheServiceMock.Object, cacheOptions);

        _service = new LendingService(
            _dbContext,
            cardRepo,
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

        cardRepo.InsertAsync(new IcCard
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

    [Fact]
    public async Task ReturnAsync_台帳行が作られなかった場合にInformationで件数を残すこと()
    {
        // Arrange: 貸出後、利用履歴が 1 件も無い状態で返却する
        var lendResult = await _service.LendAsync(TestStaffIdm, TestCardIdm, 5000);
        lendResult.Success.Should().BeTrue($"貸出が失敗（{lendResult.ErrorMessage}）");

        // Act
        var returnResult = await _service.ReturnAsync(
            TestStaffIdm, TestCardIdm, new List<LedgerDetail>(), skipDuplicateCheck: true);

        // Assert
        returnResult.Success.Should().BeTrue($"返却が失敗（{returnResult.ErrorMessage}）");
        returnResult.CreatedLedgers.Should().BeEmpty("利用履歴が無いため台帳行は作られない");

        // Issue #1819: LogDebug は本番のログファイルに出ないため、切り分けに必要な値を
        // Information 1 行に集約する（受け取った履歴件数・貸出後の抽出件数・重複チェック省略）
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("台帳行は作成されませんでした")
                                              && v.ToString().Contains("受け取った履歴件数=0")
                                              && v.ToString().Contains("貸出後の抽出件数=0")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "「返却したのに履歴が増えない」の切り分けに必要な件数を本番ログへ残す");
    }

    [Fact]
    public async Task ReturnAsync_台帳行が作られた場合はその情報を出さないこと()
    {
        // Arrange: 対のテスト。片側だけだと「常に出す」実装でも緑になる
        var lendResult = await _service.LendAsync(TestStaffIdm, TestCardIdm, 5000);
        lendResult.Success.Should().BeTrue($"貸出が失敗（{lendResult.ErrorMessage}）");

        var historyDetails = new List<LedgerDetail>
        {
            new()
            {
                UseDate = DateTime.Today,
                Amount = 200,
                Balance = 4800,
                IsBus = true,
                BusStops = "★",
            },
        };

        // Act
        var returnResult = await _service.ReturnAsync(
            TestStaffIdm, TestCardIdm, historyDetails, skipDuplicateCheck: true);

        // Assert
        returnResult.Success.Should().BeTrue($"返却が失敗（{returnResult.ErrorMessage}）");
        returnResult.CreatedLedgers.Should().NotBeEmpty("利用履歴があるため台帳行が作られる");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("台帳行は作成されませんでした")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            "正常に台帳行が作られた返却では出力しない（正常運用でのログ肥大化を防ぐ）");
    }

    [Fact]
    public async Task ReturnAsync_台帳行ゼロのログにIDmを生のまま載せないこと()
    {
        // Arrange
        await _service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        // Act
        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>(), skipDuplicateCheck: true);

        // Assert: 既存の他ログと同様 IdmMasker でマスクする
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("台帳行は作成されませんでした")
                                              && !v.ToString().Contains(TestCardIdm)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "カード IDm はログへ生のまま残さない（既存ログと同じ IdmMasker.Mask を通す）");
    }
}
