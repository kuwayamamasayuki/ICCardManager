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
        viewModel.IsSettingsLoadFailed.Should().BeTrue("保存できない理由を、状態欄とは別の警告で出し続ける（レビュー 4 回目）");
        viewModel.SettingsLoadFailedNotice.Should().Be(SettingsViewModel.SettingsLoadFailedMessage);

        // ボタンを経ずに保存を呼んでも（Enter キー等）、既定値で上書きしない
        viewModel.WarningBalance = 5000;
        await viewModel.SaveAsync();

        repository.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never,
            "読み込みに失敗した画面の既定値（部署種別は市長事務部局など）を保存しない");
        viewModel.IsSaved.Should().BeFalse("ダイアログを閉じない");
        viewModel.IsSettingsLoadFailed.Should().BeTrue();
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

    /// <summary>
    /// レビュー 4 回目: 読み込みが終わる前（画面を開いて読み込みを待っている間）に保存しても、既定値で上書きしないこと。
    /// </summary>
    /// <remarks>
    /// 読み込み中は処理中オーバーレイが出るがマウスしか遮らず、既定ボタンの Enter や Alt+S は届く。
    /// 失敗だけを止める形では、この間の保存が既定値（部署種別は市長事務部局など）で全項目を書いていた。
    /// </remarks>
    [Fact]
    public async Task 読み込み前に保存してもSaveAppSettingsAsyncを呼ばないこと()
    {
        var repository = new Mock<ISettingsRepository>();
        var viewModel = CreateViewModel(repository.Object);

        viewModel.CanSaveSettings.Should().BeFalse("読み込みが成功して終わるまで保存ボタンを押せない");

        viewModel.WarningBalance = 5000;
        await viewModel.SaveAsync();

        repository.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never,
            "読み込み前の既定値で全項目を書かない");
        viewModel.IsSaved.Should().BeFalse("ダイアログを閉じない");
        viewModel.StatusMessage.Should().Be(SettingsViewModel.SettingsNotLoadedMessage);
        viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// レビュー 4 回目: 読み込みに失敗した後、読み込み直して成功したら、失敗の警告を消して保存できること。
    /// </summary>
    /// <remarks>
    /// 読み込みの最初で失敗の印を戻す行が働いていることを固定する（消すと、保存できるのに「保存できません」の警告が残る）。
    /// </remarks>
    [Fact]
    public async Task 読み込みに失敗した後で読み込み直して成功したら警告を消して保存できること()
    {
        var repository = new Mock<ISettingsRepository>();
        repository.SetupSequence(r => r.GetAppSettingsAsync())
            .ThrowsAsync(new InvalidOperationException("共有フォルダーに接続できない（テスト用）"))
            .ReturnsAsync(new AppSettings { DepartmentType = DepartmentType.EnterpriseAccount });
        var viewModel = CreateViewModel(repository.Object);

        await viewModel.Invoking(v => v.LoadSettingsAsync()).Should().ThrowAsync<InvalidOperationException>();
        viewModel.IsSettingsLoadFailed.Should().BeTrue("前提: 1 回目は失敗している");

        await viewModel.LoadSettingsAsync();

        viewModel.IsSettingsLoadFailed.Should().BeFalse("読み込みに成功したら失敗の警告を消す");
        viewModel.CanSaveSettings.Should().BeTrue();
    }

    /// <summary>
    /// レビュー 4 回目: 読み込みに成功した後、読み込み直して失敗したら（または読み込み中は）保存できないこと。
    /// </summary>
    /// <remarks>
    /// 読み込みの最初で「読み込み済み」の印を戻す行が働いていることを固定する（消すと、前回の成功の印が残り、
    /// 読み込み直しの途中や失敗した後でも既定値・中途半端な値のまま保存できてしまう）。
    /// </remarks>
    [Fact]
    public async Task 読み込みに成功した後で読み込み直して失敗したら保存できないこと()
    {
        var repository = new Mock<ISettingsRepository>();
        repository.SetupSequence(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { DepartmentType = DepartmentType.EnterpriseAccount })
            .ThrowsAsync(new InvalidOperationException("共有フォルダーに接続できない（テスト用）"));
        var viewModel = CreateViewModel(repository.Object);

        await viewModel.LoadSettingsAsync();
        viewModel.CanSaveSettings.Should().BeTrue("前提: 1 回目は成功している");

        await viewModel.Invoking(v => v.LoadSettingsAsync()).Should().ThrowAsync<InvalidOperationException>();

        viewModel.CanSaveSettings.Should().BeFalse("前回の成功の印を残さない");
        await viewModel.SaveAsync();
        repository.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
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
