using System;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2305（レビュー 2 回目）: 設定画面（F5）を開くと、設定のキャッシュに古い値があっても DB の値を表示すること。
/// </summary>
/// <remarks>
/// <para>
/// 設定画面の保存（一括保存）は画面の全項目を書く。初期値を TTL キャッシュ（既定 5 分・共有モード 3 分）から読むと、
/// 共有モードで PC B が起動時に読んだ後に PC A が部署種別を変え、PC B が 3 分以内に F5 で残額警告だけ変えて保存したとき、
/// 部署種別が古い値へ戻る。開くたびに DB から読み直して、この窓を「画面を開いている間」に縮める。
/// </para>
/// <para>
/// 2 台の PC は、同じ DB を指す 2 つのリポジトリ（キャッシュはそれぞれ別の実物の <see cref="CacheService"/>）で表す。
/// 故障の起点（画面を開く PC のキャッシュが古いこと）を表明してから画面を開く。
/// </para>
/// </remarks>
public class SettingsViewModelFreshLoadTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheOfThisPc;
    private readonly CacheService _cacheOfOtherPc;
    private readonly SettingsRepository _thisPc;
    private readonly SettingsRepository _otherPc;

    public SettingsViewModelFreshLoadTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheOfThisPc = new CacheService(NullLogger<CacheService>.Instance);
        _cacheOfOtherPc = new CacheService(NullLogger<CacheService>.Instance);
        _thisPc = new SettingsRepository(_dbContext, _cacheOfThisPc, Options.Create(new CacheOptions()));
        _otherPc = new SettingsRepository(_dbContext, _cacheOfOtherPc, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _cacheOfThisPc.Dispose();
        _cacheOfOtherPc.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 設定画面を開くとキャッシュに古い値があってもDBの値を表示すること()
    {
        // Arrange: この PC が起動時に設定を読んでキャッシュしている（既定値: 市長事務部局・10,000 円）
        (await _thisPc.GetAppSettingsAsync()).DepartmentType.Should().Be(DepartmentType.MayorOffice, "前提: 変更前は既定の部署種別");

        // 他の PC が設定画面で部署種別と残額警告を変える
        (await _otherPc.SaveAppSettingsAsync(new AppSettings
        {
            DepartmentType = DepartmentType.EnterpriseAccount,
            WarningBalance = 3000,
        })).Should().BeTrue();

        (await _thisPc.GetAppSettingsAsync()).DepartmentType.Should().Be(
            DepartmentType.MayorOffice,
            "前提: この PC のキャッシュは他の PC の変更より古い（成立しないとテストは空振りする）");

        // Act: この PC で設定画面を開く
        var viewModel = CreateViewModel(_thisPc);
        await viewModel.LoadSettingsAsync();

        // Assert: DB の値を表示している（古い値を初期値にすると、別の項目だけ変えて保存したときに巻き戻す）
        viewModel.SelectedDepartmentTypeItem!.Value.Should().Be(
            DepartmentType.EnterpriseAccount,
            "画面の初期値をキャッシュの古い値にすると、保存で部署種別を巻き戻す（Issue #2305）");
        viewModel.WarningBalance.Should().Be(3000);
    }

    private static SettingsViewModel CreateViewModel(ISettingsRepository repository)
    {
        var validation = new Mock<IValidationService>();
        validation.Setup(v => v.ValidateWarningBalance(It.IsAny<int>())).Returns(ValidationResult.Success());

        return new SettingsViewModel(
            repository,
            validation.Object,
            new Mock<ISoundPlayer>().Object,
            Options.Create(new DatabaseOptions()),
            new Mock<IDialogService>().Object,
            new SummaryGenerator(DepartmentType.MayorOffice),
            new Mock<IStaffAuthService>().Object,
            new Mock<ISettingsChangeService>().Object);
    }
}
