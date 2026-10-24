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

    /// <summary>
    /// レビュー 3 回目: 設定の読み込みに失敗した画面からは保存できないこと。
    /// </summary>
    /// <remarks>
    /// 設定の読み込みは再試行を通らないため、共有モードで一時的に DB へ接続できないだけでも失敗する。失敗すると画面の項目は既定値
    /// （部署種別は市長事務部局、残額警告は 0 など）のまま残り、保存は全項目を書くので、別の項目だけを変えて保存すると
    /// 変えていない設定まで既定値で上書きする。開くたびに DB を読むようにした（本 Issue）ことで、この経路を踏む頻度が上がる。
    /// </remarks>
    [Fact]
    public async Task 設定の読み込みに失敗したら保存できないこと()
    {
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(r => r.GetAppSettingsAsync())
            .ThrowsAsync(new InvalidOperationException("共有フォルダーに接続できない（テスト用）"));
        var viewModel = CreateViewModel(repository.Object);

        await viewModel.Invoking(v => v.LoadSettingsAsync()).Should().ThrowAsync<InvalidOperationException>(
            "例外は呼び出し元（SettingsDialog）へ伝え、エラーを表示させる");

        viewModel.CanSaveSettings.Should().BeFalse("保存ボタンを押せないようにする");
        viewModel.StatusMessage.Should().Be(SettingsViewModel.SettingsLoadFailedMessage);
        viewModel.IsStatusError.Should().BeTrue();

        // ボタンを経ずに保存を呼んでも（Enter キー等）、既定値で上書きしない
        viewModel.WarningBalance = 5000;
        await viewModel.SaveAsync();

        repository.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never,
            "読み込みに失敗した画面の既定値（部署種別は市長事務部局など）を保存しない");
        viewModel.IsSaved.Should().BeFalse("ダイアログを閉じない");
        viewModel.StatusMessage.Should().Be(SettingsViewModel.SettingsLoadFailedMessage);
    }

    /// <summary>
    /// 対の表明: 読み込みに成功した画面からは従来どおり保存できること。
    /// これが無いと、保存を常に止める実装でも上のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task 設定の読み込みに成功したら保存できること()
    {
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(r => r.GetAppSettingsAsync()).ReturnsAsync(new AppSettings
        {
            DepartmentType = DepartmentType.EnterpriseAccount,
            WarningBalance = 3000,
        });
        // WPF 依存の文字サイズの反映を避けるため false を返す（SettingsViewModelTests と同じ）
        repository.Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>())).ReturnsAsync(false);
        var viewModel = CreateViewModel(repository.Object);

        await viewModel.LoadSettingsAsync();
        viewModel.CanSaveSettings.Should().BeTrue();

        viewModel.WarningBalance = 5000;
        await viewModel.SaveAsync();

        repository.Verify(r => r.SaveAppSettingsAsync(It.Is<AppSettings>(s =>
            s.WarningBalance == 5000 && s.DepartmentType == DepartmentType.EnterpriseAccount)), Times.Once,
            "読み込んだ部署種別を保ったまま、変えた残額警告を保存する");
    }

    private static SettingsViewModel CreateViewModel(ISettingsRepository repository)
    {
        var validation = new Mock<IValidationService>();
        validation.Setup(v => v.ValidateWarningBalance(It.IsAny<int>())).Returns(ValidationResult.Success());
        validation.Setup(v => v.ValidateCompanionCountInputTimeout(It.IsAny<int>())).Returns(ValidationResult.Success());
        validation.Setup(v => v.ValidateLongTermUnreturnedDays(It.IsAny<int>())).Returns(ValidationResult.Success());

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
