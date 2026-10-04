using System;
using System.Collections.Generic;
using System.Linq;
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
                new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
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
        // FilterUsageToRecordOnReturn（Issue #2237: 下限は導入行の日付。導入行が無ければ貸出日の 7 日前）
        // ============================================================

        [Fact]
        public void FilterUsageToRecordOnReturn_導入行なし_DetailsBeforeSevenDays_Excluded()
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

            var result = LendingService.FilterUsageToRecordOnReturn(
                details, lentRecord, now, introductionDate: null, latestLedgerDateWithoutDetails: null);

            result.Should().HaveCount(3);
            result.Should().NotContain(d => d.UseDate == new DateTime(2026, 4, 7));
        }

        [Fact]
        public void FilterUsageToRecordOnReturn_導入行なし_NullUseDate_Included()
        {
            var now = new DateTime(2026, 4, 19);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 15) };
            var details = new List<LedgerDetail>
            {
                new() { UseDate = null },
                new() { UseDate = new DateTime(2026, 4, 1) },  // 除外
            };

            var result = LendingService.FilterUsageToRecordOnReturn(
                details, lentRecord, now, introductionDate: null, latestLedgerDateWithoutDetails: null);

            result.Should().HaveCount(1);
            result[0].UseDate.Should().BeNull();
        }

        [Fact]
        public void FilterUsageToRecordOnReturn_導入行なし_LentAtNull_UsesYesterday()
        {
            var now = new DateTime(2026, 4, 19);
            var lentRecord = new Ledger { LentAt = null };  // fallback: now - 1 day = 2026-04-18
            // フィルタ開始日 = 2026-04-18 - 7 = 2026-04-11
            var details = new List<LedgerDetail>
            {
                new() { UseDate = new DateTime(2026, 4, 10) },  // 除外
                new() { UseDate = new DateTime(2026, 4, 11) },  // 境界値（含む）
            };

            var result = LendingService.FilterUsageToRecordOnReturn(
                details, lentRecord, now, introductionDate: null, latestLedgerDateWithoutDetails: null);

            result.Should().HaveCount(1);
            result[0].UseDate.Should().Be(new DateTime(2026, 4, 11));
        }

        [Fact]
        public void FilterUsageToRecordOnReturn_導入行あり_貸出日の7日より前でも導入日以降の利用を含むこと()
        {
            // 欠陥側: 修正前は貸出日（4/20）の 7 日前＝4/13 より古い 4/5 を捨てていた
            var now = new DateTime(2026, 4, 20, 10, 0, 0);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 20, 9, 0, 0) };
            var details = new List<LedgerDetail>
            {
                new() { UseDate = new DateTime(2026, 4, 20) },
                new() { UseDate = new DateTime(2026, 4, 5) },
                new() { UseDate = new DateTime(2026, 4, 1) },   // 導入日当日（境界・含む）
                new() { UseDate = new DateTime(2026, 3, 31) },  // 導入前（含まない）
            };

            var result = LendingService.FilterUsageToRecordOnReturn(
                details, lentRecord, now, introductionDate: new DateTime(2026, 4, 1), latestLedgerDateWithoutDetails: null);

            result.Select(d => d.UseDate).Should().Equal(
                new DateTime(2026, 4, 20), new DateTime(2026, 4, 5), new DateTime(2026, 4, 1));
        }

        [Fact]
        public void FilterUsageToRecordOnReturn_導入行が貸出日の7日以内なら導入前の利用は含まないこと()
        {
            // 修正前は貸出日の 7 日前（4/13）以降をすべて含め、導入行（4/18）の残高に含まれる 4/15 を二重に取り込んでいた
            var now = new DateTime(2026, 4, 20, 10, 0, 0);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 20, 9, 0, 0) };
            var details = new List<LedgerDetail>
            {
                new() { UseDate = new DateTime(2026, 4, 18) },
                new() { UseDate = new DateTime(2026, 4, 15) },
                new() { UseDate = null },                        // 日付なしは下限で判定できないので含める
            };

            var result = LendingService.FilterUsageToRecordOnReturn(
                details, lentRecord, now, introductionDate: new DateTime(2026, 4, 18, 13, 30, 0), latestLedgerDateWithoutDetails: null);

            result.Select(d => d.UseDate).Should().Equal(new DateTime(2026, 4, 18), null);
        }

        [Fact]
        public void ResolveUsageLowerBound_導入行の有無で下限の根拠が切り替わること()
        {
            var now = new DateTime(2026, 4, 20, 10, 0, 0);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 20, 9, 0, 0) };

            LendingService.ResolveUsageLowerBound(lentRecord, now, new DateTime(2026, 1, 10, 15, 0, 0), latestLedgerDateWithoutDetails: null)
                .Should().Be(new DateTime(2026, 1, 10), "導入行があればその日付（時刻は切り捨て）。貸出日には寄せない");
            LendingService.ResolveUsageLowerBound(lentRecord, now, introductionDate: null, latestLedgerDateWithoutDetails: null)
                .Should().Be(new DateTime(2026, 4, 13), "導入行が無ければ従来どおり貸出日の 7 日前");
        }

        [Fact]
        public void ResolveUsageLowerBound_明細を持たない行があれば_その翌日と導入日の遅いほうを下限にすること()
        {
            // 手で追加した行（明細なし）は照合のキーを持たないので、その日以前の履歴を記録すると二重になる
            var now = new DateTime(2026, 4, 20, 10, 0, 0);
            var lentRecord = new Ledger { LentAt = new DateTime(2026, 4, 20, 9, 0, 0) };

            LendingService.ResolveUsageLowerBound(lentRecord, now,
                    introductionDate: new DateTime(2026, 4, 1), latestLedgerDateWithoutDetails: new DateTime(2026, 4, 5, 0, 0, 0))
                .Should().Be(new DateTime(2026, 4, 6), "明細なしの行（4/5）が導入日より後なら、その翌日");
            LendingService.ResolveUsageLowerBound(lentRecord, now,
                    introductionDate: new DateTime(2026, 4, 10), latestLedgerDateWithoutDetails: new DateTime(2026, 4, 5))
                .Should().Be(new DateTime(2026, 4, 10), "明細なしの行が導入日より前なら導入日のまま（下限を早めない）");
            LendingService.ResolveUsageLowerBound(lentRecord, now,
                    introductionDate: null, latestLedgerDateWithoutDetails: new DateTime(2026, 4, 15))
                .Should().Be(new DateTime(2026, 4, 16), "導入行が無いときも、貸出日の 7 日前（4/13）より遅ければ翌日へ寄せる");
            LendingService.ResolveUsageLowerBound(lentRecord, now,
                    introductionDate: null, latestLedgerDateWithoutDetails: new DateTime(2026, 4, 1))
                .Should().Be(new DateTime(2026, 4, 13), "明細なしの行が貸出日の 7 日前より古ければ従来の下限のまま");
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

        /// <summary>
        /// Issue #1998: 境界は「以下」。しきい値ちょうどの残額も警告対象に含める。
        /// </summary>
        /// <remarks>
        /// 本テストは以前 <c>BalanceEqualThreshold_NotLow</c> として厳密な <c>&lt;</c> を固定していたが、
        /// 設計書（04 §7.1 の設定値表・03 画面設計書）・管理者マニュアル・Excel 出力見出し、
        /// および <c>DashboardService</c> / <c>AdminDashboardService</c> / <c>WarningService</c> の
        /// 3 か所はいずれも「以下」であり、テスト側が仕様を後追いで固定していた。
        /// </remarks>
        [Fact]
        public async Task ApplyBalanceWarningAsync_BalanceEqualThreshold_IsLow()
        {
            _mockSettingsRepo.Setup(r => r.GetAppSettingsAsync())
                .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

            var service = CreateService();
            var result = new LendingResult { Balance = 1000 };
            await service.ApplyBalanceWarningAsync(result);

            result.IsLowBalance.Should().BeTrue();
        }
    }
}
