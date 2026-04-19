using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services
{
    /// <summary>
    /// Issue #1283: LendAsync/ReturnAsync から抽出した internal ヘルパーの単体テスト。
    /// </summary>
    public class LendingServiceHelperTests : IDisposable
    {
        private readonly Mock<ICardRepository> _mockCardRepo = new();
        private readonly Mock<IStaffRepository> _mockStaffRepo = new();
        private readonly Mock<ILedgerRepository> _mockLedgerRepo = new();
        private readonly Mock<ISettingsRepository> _mockSettingsRepo = new();
        private readonly DbContext _dbContext;
        private readonly CardLockManager _lockManager;

        public LendingServiceHelperTests()
        {
            _dbContext = new DbContext(":memory:");
            _dbContext.InitializeDatabase();
            _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        }

        private LendingService CreateService()
        {
            return new LendingService(
                _dbContext,
                _mockCardRepo.Object,
                _mockStaffRepo.Object,
                _mockLedgerRepo.Object,
                _mockSettingsRepo.Object,
                new SummaryGenerator(),
                _lockManager,
                Options.Create(new AppOptions()),
                NullLogger<LendingService>.Instance);
        }

        public void Dispose()
        {
            _lockManager.Dispose();
            _dbContext.Dispose();
            GC.SuppressFinalize(this);
        }

        // ============================================================
        // ValidateLendPreconditionsAsync
        // ============================================================

        [Fact]
        public async Task ValidateLendPreconditionsAsync_CardNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync((IcCard)null);

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().BeNull();
            staff.Should().BeNull();
            error.Should().Be("カードが登録されていません。");
        }

        [Fact]
        public async Task ValidateLendPreconditionsAsync_CardAlreadyLent_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new IcCard { CardIdm = "CARD01", IsLent = true });

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            staff.Should().BeNull();
            error.Should().Be("このカードは既に貸出中です。");
        }

        [Fact]
        public async Task ValidateLendPreconditionsAsync_StaffNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new IcCard { CardIdm = "CARD01", IsLent = false });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync((Staff)null);

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            staff.Should().BeNull();
            error.Should().Be("職員証が登録されていません。");
        }

        [Fact]
        public async Task ValidateLendPreconditionsAsync_AllValid_ReturnsNullError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new IcCard { CardIdm = "CARD01", IsLent = false });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync(new Staff { StaffIdm = "STAFF01", Name = "テスト職員" });

            var service = CreateService();
            var (card, staff, error) = await service.ValidateLendPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            staff.Should().NotBeNull();
            staff.Name.Should().Be("テスト職員");
            error.Should().BeNull();
        }

        // ============================================================
        // ValidateReturnPreconditionsAsync
        // ============================================================

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_CardNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync((IcCard)null);

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().BeNull();
            returner.Should().BeNull();
            error.Should().Be("カードが登録されていません。");
        }

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_NotLent_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new IcCard { CardIdm = "CARD01", IsLent = false });

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            returner.Should().BeNull();
            error.Should().Be("このカードは貸出されていません。");
        }

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_StaffNotFound_ReturnsError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new IcCard { CardIdm = "CARD01", IsLent = true });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync((Staff)null);

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            returner.Should().BeNull();
            error.Should().Be("職員証が登録されていません。");
        }

        [Fact]
        public async Task ValidateReturnPreconditionsAsync_AllValid_ReturnsNullError()
        {
            _mockCardRepo.Setup(r => r.GetByIdmAsync("CARD01", false))
                .ReturnsAsync(new IcCard { CardIdm = "CARD01", IsLent = true });
            _mockStaffRepo.Setup(r => r.GetByIdmAsync("STAFF01", false))
                .ReturnsAsync(new Staff { StaffIdm = "STAFF01", Name = "返却者" });

            var service = CreateService();
            var (card, returner, error) = await service.ValidateReturnPreconditionsAsync("STAFF01", "CARD01");

            card.Should().NotBeNull();
            returner.Should().NotBeNull();
            returner.Name.Should().Be("返却者");
            error.Should().BeNull();
        }

        // ============================================================
        // ResolveLentRecordAsync
        // ============================================================

        [Fact]
        public async Task ResolveLentRecordAsync_NotFound_ReturnsError()
        {
            _mockLedgerRepo.Setup(r => r.GetLentRecordAsync("CARD01"))
                .ReturnsAsync((Ledger)null);

            var service = CreateService();
            var (lentRecord, error) = await service.ResolveLentRecordAsync("CARD01");

            lentRecord.Should().BeNull();
            error.Should().Be("貸出レコードが見つかりません。");
        }

        [Fact]
        public async Task ResolveLentRecordAsync_Found_ReturnsRecord()
        {
            var record = new Ledger { CardIdm = "CARD01", IsLentRecord = true, StaffName = "職員A" };
            _mockLedgerRepo.Setup(r => r.GetLentRecordAsync("CARD01"))
                .ReturnsAsync(record);

            var service = CreateService();
            var (lentRecord, error) = await service.ResolveLentRecordAsync("CARD01");

            lentRecord.Should().NotBeNull();
            lentRecord.StaffName.Should().Be("職員A");
            error.Should().BeNull();
        }

        // ============================================================
        // ResolveInitialBalanceAsync
        // ============================================================

        [Fact]
        public async Task ResolveInitialBalanceAsync_BalanceProvided_ReturnsGivenValue()
        {
            var service = CreateService();
            var result = await service.ResolveInitialBalanceAsync("CARD01", 1500);
            result.Should().Be(1500);
        }

        [Fact]
        public async Task ResolveInitialBalanceAsync_NullWithLedger_ReturnsLedgerBalance()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync(new Ledger { Balance = 880 });

            var service = CreateService();
            var result = await service.ResolveInitialBalanceAsync("CARD01", null);
            result.Should().Be(880);
        }

        [Fact]
        public async Task ResolveInitialBalanceAsync_NullWithoutLedger_ReturnsZero()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync((Ledger)null);

            var service = CreateService();
            var result = await service.ResolveInitialBalanceAsync("CARD01", null);
            result.Should().Be(0);
        }

        // ============================================================
        // FilterUsageSinceLent
        // ============================================================

        [Fact]
        public void FilterUsageSinceLent_DetailsBeforeSevenDays_Excluded()
        {
            var now = new DateTime(2026, 4, 19, 10, 0, 0);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 15) };
            // フィルタ開始日 = 2026-04-15 - 7日 = 2026-04-08
            var details = new List<LedgerDetail>
            {
                new() { UseDate = new DateTime(2026, 4, 7) },   // 除外
                new() { UseDate = new DateTime(2026, 4, 8) },   // 含まれる（境界値）
                new() { UseDate = new DateTime(2026, 4, 15) },  // 含まれる
                new() { UseDate = new DateTime(2026, 4, 18) },  // 含まれる
            };

            var result = LendingService.FilterUsageSinceLent(details, lentRecord, now);

            result.Should().HaveCount(3);
            result.Should().NotContain(d => d.UseDate == new DateTime(2026, 4, 7));
        }

        [Fact]
        public void FilterUsageSinceLent_NullUseDate_Included()
        {
            var now = new DateTime(2026, 4, 19);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 15) };
            var details = new List<LedgerDetail>
            {
                new() { UseDate = null },
                new() { UseDate = new DateTime(2026, 4, 1) },  // 除外
            };

            var result = LendingService.FilterUsageSinceLent(details, lentRecord, now);

            result.Should().HaveCount(1);
            result[0].UseDate.Should().BeNull();
        }

        [Fact]
        public void FilterUsageSinceLent_LentAtNull_UsesYesterday()
        {
            var now = new DateTime(2026, 4, 19);
            var lentRecord = new Ledger { LentAt = null };  // fallback: now - 1 day = 2026-04-18
            // フィルタ開始日 = 2026-04-18 - 7 = 2026-04-11
            var details = new List<LedgerDetail>
            {
                new() { UseDate = new DateTime(2026, 4, 10) },  // 除外
                new() { UseDate = new DateTime(2026, 4, 11) },  // 境界値（含む）
            };

            var result = LendingService.FilterUsageSinceLent(details, lentRecord, now);

            result.Should().HaveCount(1);
            result[0].UseDate.Should().Be(new DateTime(2026, 4, 11));
        }

        // ============================================================
        // ResolveReturnBalanceAsync
        // ============================================================

        [Fact]
        public async Task ResolveReturnBalanceAsync_CardBalancePresent_ReturnsCardBalance()
        {
            var details = new List<LedgerDetail>
            {
                new() { Balance = 2500 },  // 先頭=最新
                new() { Balance = 3000 },
            };
            var createdLedgers = new List<Ledger> { new() { Balance = 999 } };

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(details, createdLedgers, "CARD01");

            result.Should().Be(2500);
        }

        [Fact]
        public async Task ResolveReturnBalanceAsync_NoCardBalance_ReturnsLedgerBalance()
        {
            var details = new List<LedgerDetail>
            {
                new() { Balance = null }
            };
            var createdLedgers = new List<Ledger>
            {
                new() { Balance = 100 },
                new() { Balance = 200 }  // 末尾
            };

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(details, createdLedgers, "CARD01");

            result.Should().Be(200);
        }

        [Fact]
        public async Task ResolveReturnBalanceAsync_NoDetailNoLedger_UsesDbFallback()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync(new Ledger { Balance = 777 });

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(
                new List<LedgerDetail>(), new List<Ledger>(), "CARD01");

            result.Should().Be(777);
        }

        [Fact]
        public async Task ResolveReturnBalanceAsync_NoDetailNoLedgerNoDb_ReturnsZero()
        {
            _mockLedgerRepo.Setup(r => r.GetLatestLedgerAsync("CARD01"))
                .ReturnsAsync((Ledger)null);

            var service = CreateService();
            var result = await service.ResolveReturnBalanceAsync(
                new List<LedgerDetail>(), new List<Ledger>(), "CARD01");

            result.Should().Be(0);
        }

        // ============================================================
        // ApplyBalanceWarningAsync
        // ============================================================

        [Fact]
        public async Task ApplyBalanceWarningAsync_BalanceBelowThreshold_SetsIsLowBalance()
        {
            _mockSettingsRepo.Setup(r => r.GetAppSettingsAsync())
                .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

            var service = CreateService();
            var result = new LendingResult { Balance = 500 };
            await service.ApplyBalanceWarningAsync(result);

            result.WarningBalance.Should().Be(1000);
            result.IsLowBalance.Should().BeTrue();
        }

        [Fact]
        public async Task ApplyBalanceWarningAsync_BalanceAboveThreshold_NotLow()
        {
            _mockSettingsRepo.Setup(r => r.GetAppSettingsAsync())
                .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

            var service = CreateService();
            var result = new LendingResult { Balance = 1500 };
            await service.ApplyBalanceWarningAsync(result);

            result.IsLowBalance.Should().BeFalse();
        }

        [Fact]
        public async Task ApplyBalanceWarningAsync_BalanceEqualThreshold_NotLow()
        {
            _mockSettingsRepo.Setup(r => r.GetAppSettingsAsync())
                .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

            var service = CreateService();
            var result = new LendingResult { Balance = 1000 };
            await service.ApplyBalanceWarningAsync(result);

            result.IsLowBalance.Should().BeFalse();
        }
    }
}
