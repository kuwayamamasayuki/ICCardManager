using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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
/// Issue #2388: 設定画面（F5）の保存は、画面を開いたときから変えた項目だけを書き、他の PC が変えた項目を巻き戻さないこと。
/// </summary>
/// <remarks>
/// <para>
/// 共有モードの 2 台の PC は、同じ DB（実 SQLite）を指す 2 つのリポジトリ（キャッシュはそれぞれ別）で表す。
/// 画面はこの PC のリポジトリで読み書きする。
/// </para>
/// <para>
/// 保存は実際の DB へ書いたうえで、画面には「失敗」（false）を返す。成功の後処理（<c>App.ApplyFontSize</c>）は WPF の
/// <c>Application.Current</c> を要し、単体テストからは到達できないため（<c>SettingsViewModelTests</c> と同じ理由）。
/// 書いた内容は DB を読み直して確かめる。
/// </para>
/// </remarks>
public sealed class SettingsViewModelChangedKeysTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheOfThisPc;
    private readonly CacheService _cacheOfOtherPc;
    private readonly SettingsRepository _thisPc;
    private readonly SettingsRepository _otherPc;
    private readonly string _configDirectory;
    private readonly List<IReadOnlyCollection<string>> _savedKeys = new();

    public SettingsViewModelChangedKeysTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheOfThisPc = new CacheService(NullLogger<CacheService>.Instance);
        _cacheOfOtherPc = new CacheService(NullLogger<CacheService>.Instance);
        _thisPc = new SettingsRepository(_dbContext, _cacheOfThisPc, Options.Create(new CacheOptions()));
        _otherPc = new SettingsRepository(_dbContext, _cacheOfOtherPc, Options.Create(new CacheOptions()));
        _configDirectory = Path.Combine(Path.GetTempPath(), "ICCardManagerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_configDirectory);
    }

    public void Dispose()
    {
        _cacheOfThisPc.Dispose();
        _cacheOfOtherPc.Dispose();
        _dbContext.Dispose();
        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch
        {
            // 後片付けの失敗はテスト結果に影響させない（一時フォルダー配下）
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 欠陥を突く側: 画面を開いた後で他の PC が部署種別を変え、この画面で文字サイズだけを変えて保存しても、部署種別を書き戻さないこと。
    /// </summary>
    [Fact]
    public async Task 開いた後に他のPCが部署種別を変えても_文字サイズだけを変えた保存で書き戻さないこと()
    {
        var viewModel = await OpenAsync();
        viewModel.SelectedDepartmentTypeItem!.Value.Should().Be(DepartmentType.MayorOffice, "前提: 開いたときは市長事務部局");

        // 画面を開いている間に、他の PC が部署種別を変える
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();

        viewModel.SelectedFontSizeItem = viewModel.FontSizeOptions.Single(x => x.Value == FontSizeOption.Large);
        await viewModel.SaveAsync();

        (await _thisPc.GetAsync("font_size")).Should().Be("large", "変えた項目は保存する");
        (await _thisPc.GetAsync("department_type")).Should().Be(
            "enterprise_account",
            "変えていない部署種別を画面の古い値（市長事務部局）で書き戻すと、他の PC の変更が黙って失われる（Issue #2388）");
        _savedKeys.Should().ContainSingle().Which.Should().Equal("font_size");
    }

    /// <summary>
    /// 正当な動作を塞いでいない側: 他の PC の変更が無ければ、変えた項目は従来どおり保存されること。
    /// </summary>
    [Fact]
    public async Task 変えた項目は保存されること()
    {
        var viewModel = await OpenAsync();

        viewModel.WarningBalance = 3000;
        viewModel.SelectedDepartmentTypeItem = viewModel.DepartmentTypeOptions.Single(x => x.Value == DepartmentType.EnterpriseAccount);
        viewModel.ShowHistoryOnReturn = false;
        await viewModel.SaveAsync();

        var saved = await _otherPc.GetAppSettingsAsync();
        saved.WarningBalance.Should().Be(3000);
        saved.DepartmentType.Should().Be(DepartmentType.EnterpriseAccount);
        saved.ShowHistoryOnReturn.Should().BeFalse();
        _savedKeys.Should().ContainSingle().Which.Should().BeEquivalentTo(
            "warning_balance", "department_type", "show_history_on_return");
    }

    /// <summary>
    /// 同じ項目を両方の PC が変えたときは、後に保存した方が残る（競合の検出はしない。Issue #2388 で選んだ方針）。
    /// </summary>
    [Fact]
    public async Task 同じ項目を他のPCも変えていたら後に保存した方が残ること()
    {
        var viewModel = await OpenAsync();
        (await _otherPc.SetAsync("warning_balance", "5000")).Should().BeTrue();

        viewModel.WarningBalance = 3000;
        await viewModel.SaveAsync();

        (await _thisPc.GetAsync("warning_balance")).Should().Be("3000");
    }

    /// <summary>
    /// 何も変えずに保存したら、DB へは何も書かないこと（他の PC の変更もそのまま残る）。
    /// </summary>
    [Fact]
    public async Task 何も変えずに保存したらDBへ何も書かないこと()
    {
        var viewModel = await OpenAsync();
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();
        (await _otherPc.SetAsync("warning_balance", "5000")).Should().BeTrue();

        await viewModel.SaveAsync();

        _savedKeys.Should().ContainSingle().Which.Should().BeEmpty("変えた項目が無い");
        (await _thisPc.GetAsync("department_type")).Should().Be("enterprise_account");
        (await _thisPc.GetAsync("warning_balance")).Should().Be("5000");
    }

    /// <summary>
    /// 一度変えて元に戻した項目は「変えた項目」に数えないこと。変更の印（<see cref="SettingsViewModel.HasChanges"/>）は
    /// 値が一度でも変わると立ったままになるので、書くキーの判定には使わず、開いたときの値と比べる。
    /// </summary>
    [Fact]
    public async Task 変えてから元に戻した項目は書かないこと()
    {
        var viewModel = await OpenAsync();
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();

        viewModel.SelectedDepartmentTypeItem = viewModel.DepartmentTypeOptions.Single(x => x.Value == DepartmentType.EnterpriseAccount);
        viewModel.SelectedDepartmentTypeItem = viewModel.DepartmentTypeOptions.Single(x => x.Value == DepartmentType.MayorOffice);
        viewModel.HasChanges.Should().BeTrue("前提: 変更の印は立ったまま");
        await viewModel.SaveAsync();

        _savedKeys.Should().ContainSingle().Which.Should().BeEmpty();
        (await _thisPc.GetAsync("department_type")).Should().Be("enterprise_account");
    }

    /// <summary>
    /// DB に選択肢に無い値（古い版・手作業の値）があっても、画面がそれを既定の選択肢へ寄せたことは「変えた項目」に数えないこと。
    /// </summary>
    [Fact]
    public async Task 画面が既定の選択肢へ寄せた値は変えた項目に数えないこと()
    {
        (await _otherPc.SetAsync("sound_mode", "unknown_mode")).Should().BeTrue();
        var viewModel = await OpenAsync();
        viewModel.SelectedSoundModeItem!.Value.Should().Be(SoundMode.Beep, "前提: 認識できない値は効果音として表示する");

        viewModel.WarningBalance = 3000;
        await viewModel.SaveAsync();

        _savedKeys.Should().ContainSingle().Which.Should().Equal("warning_balance");
        (await _thisPc.GetAsync("sound_mode")).Should().Be("unknown_mode", "職員が変えていない項目は書かない");
    }

    private async Task<SettingsViewModel> OpenAsync()
    {
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(r => r.InvalidateCache()).Callback(() => _thisPc.InvalidateCache());
        repository.Setup(r => r.GetAppSettingsAsync()).Returns(() => _thisPc.GetAppSettingsAsync());
        repository
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(async (AppSettings settings, IReadOnlyCollection<string> keys) =>
            {
                _savedKeys.Add(keys.ToList());
                (await _thisPc.SaveAppSettingsAsync(settings, keys)).Should().BeTrue();
                return false; // 成功の後処理（WPF 依存）へ進ませない（クラスの remarks）
            });

        var validation = new Mock<IValidationService>();
        validation.Setup(v => v.ValidateWarningBalance(It.IsAny<int>())).Returns(ValidationResult.Success());
        validation.Setup(v => v.ValidateCompanionCountInputTimeout(It.IsAny<int>())).Returns(ValidationResult.Success());
        validation.Setup(v => v.ValidateLongTermUnreturnedDays(It.IsAny<int>())).Returns(ValidationResult.Success());

        var viewModel = new SettingsViewModel(
            repository.Object,
            validation.Object,
            new Mock<ISoundPlayer>().Object,
            new Mock<IDialogService>().Object,
            new SummaryGenerator(DepartmentType.MayorOffice),
            new Mock<IStaffAuthService>().Object,
            new Mock<ISettingsChangeService>().Object,
            _configDirectory);
        await viewModel.LoadSettingsAsync();
        return viewModel;
    }
}
