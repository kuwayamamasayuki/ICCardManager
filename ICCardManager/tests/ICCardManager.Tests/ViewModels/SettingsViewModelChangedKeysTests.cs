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
    private readonly SummaryGenerator _summaryGenerator = new(DepartmentType.MayorOffice);

    /// <summary>保存の結果を画面へそのまま返すか（false なら、書いたうえで「失敗」を返す。クラスの remarks）</summary>
    private bool _reportSaveResult;

    /// <summary>設定の読み取りを失敗させるか（保存後の読み直しの失敗を再現する）</summary>
    private bool _failSettingsRead;

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

    /// <summary>
    /// レビュー 1 回目: 部署種別を変えずに保存したら、摘要生成器と department_config.txt へは画面の古い値ではなく DB の値を反映すること。
    /// </summary>
    /// <remarks>
    /// department_config.txt は起動のたびに DB へ無条件に適用される（<c>App.ApplyDepartmentConfigFromFileAsync</c>）。画面の古い値
    /// （市長事務部局）を書くと、この PC の再起動で、他の PC が変えた共有 DB の部署種別が戻る（DB へ書かなくした巻き戻りが先送りされるだけ）。
    /// </remarks>
    [Fact]
    public async Task 部署種別を変えずに保存したら_他のPCが変えた部署種別をファイルと摘要生成器へ反映すること()
    {
        var viewModel = await OpenAsync();
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();

        viewModel.SelectedFontSizeItem = viewModel.FontSizeOptions.Single(x => x.Value == FontSizeOption.Large);
        await SaveReportingSuccessAsync(viewModel);

        _savedKeys.Should().ContainSingle().Which.Should().Equal("font_size");
        File.ReadAllText(Path.Combine(_configDirectory, "department_config.txt")).Trim().Should().Be(
            "enterprise_account",
            "画面の古い値を書くと、この PC の再起動で共有 DB の部署種別が戻る");
        _summaryGenerator.Generate(ChargeOnlyDetails()).Should().Be(
            new SummaryGenerator(DepartmentType.EnterpriseAccount).Generate(ChargeOnlyDetails()),
            "摘要生成器も DB の部署種別で動かす");
    }

    /// <summary>
    /// レビュー 2 回目: 何も変えずに保存したときも、摘要生成器と department_config.txt へは DB の現在の値を反映すること。
    /// </summary>
    /// <remarks>
    /// 何も変えない保存は DB へ書かず、設定のキャッシュも破棄しない。読み直しの前にキャッシュを破棄しないと、開いたときに
    /// キャッシュした部署種別（共有モードで TTL 3 分）を読み、古い値をファイルへ書いて、この PC の再起動で共有 DB を戻す。
    /// 上の文字サイズのテストでは保存そのものがキャッシュを破棄するため、破棄の有無を区別できない。
    /// </remarks>
    [Fact]
    public async Task 何も変えずに保存しても_他のPCが変えた部署種別をファイルと摘要生成器へ反映すること()
    {
        var viewModel = await OpenAsync();
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();

        await SaveReportingSuccessAsync(viewModel);

        _savedKeys.Should().ContainSingle().Which.Should().BeEmpty("前提: 何も書かない保存（キャッシュも破棄されない）");
        File.ReadAllText(Path.Combine(_configDirectory, "department_config.txt")).Trim().Should().Be(
            "enterprise_account",
            "開いたときのキャッシュの値（市長事務部局）を書くと、この PC の再起動で共有 DB の部署種別が戻る");
        _summaryGenerator.Generate(ChargeOnlyDetails()).Should().Be(
            new SummaryGenerator(DepartmentType.EnterpriseAccount).Generate(ChargeOnlyDetails()));
    }

    /// <summary>
    /// レビュー 2 回目: 保存後の部署種別の読み直しに失敗したら、摘要生成器と department_config.txt のどちらへも反映しないこと。
    /// </summary>
    /// <remarks>
    /// 画面の値は開いている間に他の PC が変えた部署種別より古いかもしれないので、それで書くと再起動で共有 DB を戻し得る。
    /// 保存は確定済みなので、前の状態のまま残す。
    /// </remarks>
    [Fact]
    public async Task 保存後の部署種別の読み直しに失敗したらファイルにも摘要生成器にも反映しないこと()
    {
        // 画面には企業会計部局を表示させる（摘要生成器は市長事務部局のまま。画面の値で反映する実装を区別するため）
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();
        var viewModel = await OpenAsync();
        viewModel.SelectedFontSizeItem = viewModel.FontSizeOptions.Single(x => x.Value == FontSizeOption.Large);
        _failSettingsRead = true;

        await SaveReportingSuccessAsync(viewModel);

        (await _thisPc.GetAsync("font_size")).Should().Be("large", "前提: 保存そのものは成功している");
        File.Exists(Path.Combine(_configDirectory, "department_config.txt")).Should().BeFalse("読み直せない部署種別は書かない");
        _summaryGenerator.Generate(ChargeOnlyDetails()).Should().Be(
            new SummaryGenerator(DepartmentType.MayorOffice).Generate(ChargeOnlyDetails()),
            "摘要生成器は元の部署種別のまま");
        new SummaryGenerator(DepartmentType.MayorOffice).Generate(ChargeOnlyDetails()).Should().NotBe(
            new SummaryGenerator(DepartmentType.EnterpriseAccount).Generate(ChargeOnlyDetails()),
            "前提: 部署種別で摘要が変わる");
    }

    /// <summary>
    /// 対の表明: 部署種別を変えて保存したら、変えた値をファイルと摘要生成器へ反映すること。
    /// </summary>
    [Fact]
    public async Task 部署種別を変えて保存したら変えた値をファイルと摘要生成器へ反映すること()
    {
        var viewModel = await OpenAsync();

        viewModel.SelectedDepartmentTypeItem = viewModel.DepartmentTypeOptions.Single(x => x.Value == DepartmentType.EnterpriseAccount);
        await SaveReportingSuccessAsync(viewModel);

        File.ReadAllText(Path.Combine(_configDirectory, "department_config.txt")).Trim().Should().Be("enterprise_account");
        _summaryGenerator.Generate(ChargeOnlyDetails()).Should().Be(
            new SummaryGenerator(DepartmentType.EnterpriseAccount).Generate(ChargeOnlyDetails()));
    }

    /// <summary>
    /// レビュー 1 回目: 保存に成功したら、その値を次の保存の基準にすること。
    /// </summary>
    /// <remarks>
    /// 基準を開いたときの値のまま残すと、A → B を保存した後に B → A を保存したとき「変えた項目なし」と判定し、
    /// DB は B のまま「設定を保存しました」と案内する（データベース保存先を変えたときは画面を閉じないので、続けて保存できる）。
    /// </remarks>
    [Fact]
    public async Task 保存に成功したら次の保存は保存した値と比べること()
    {
        var viewModel = await OpenAsync();
        viewModel.WarningBalance.Should().Be(10000, "前提: 開いたときは既定値");

        viewModel.WarningBalance = 3000;
        await SaveReportingSuccessAsync(viewModel);
        viewModel.WarningBalance = 10000;
        await SaveReportingSuccessAsync(viewModel);

        _savedKeys.Should().HaveCount(2);
        _savedKeys[1].Should().Equal(new[] { "warning_balance" }, "1 回目に保存した 3,000 円から戻したので変えた項目");
        (await _thisPc.GetAsync("warning_balance")).Should().Be("10000");
    }

    /// <summary>
    /// 保存の成功を画面へ返して、成功の後処理を通す。WPF 依存の反映（<c>App.ApplyFontSize</c>）だけを読み飛ばす
    /// （<c>SettingsViewModelTests.SaveIgnoringWpfOnlyReflectionAsync</c> と同じ。部署種別の反映と基準の更新はこれより前にある）。
    /// </summary>
    private async Task SaveReportingSuccessAsync(SettingsViewModel viewModel)
    {
        _reportSaveResult = true;
        try
        {
            await viewModel.SaveAsync();
        }
        catch (NullReferenceException ex) when (ex.StackTrace?.Contains("ApplyFontSize") == true)
        {
            // WPF アプリケーション未起動によるもの（App.ApplyFontSize が Application.Current を参照する）。
            // 発生元で絞り、それより後ろの処理の NullReferenceException は隠さない
        }
    }

    /// <summary>チャージのみの明細（部署種別で摘要が変わる）</summary>
    private static List<LedgerDetail> ChargeOnlyDetails() => new()
    {
        new LedgerDetail { UseDate = new DateTime(2026, 4, 1), Amount = -3000, Balance = 5000, IsCharge = true },
    };

    private async Task<SettingsViewModel> OpenAsync()
    {
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(r => r.InvalidateCache()).Callback(() => _thisPc.InvalidateCache());
        repository.Setup(r => r.GetAppSettingsAsync()).Returns(() => _failSettingsRead
            ? Task.FromException<AppSettings>(new InvalidOperationException("共有フォルダーに接続できない（テスト用）"))
            : _thisPc.GetAppSettingsAsync());
        repository
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(async (AppSettings settings, IReadOnlyCollection<string> keys) =>
            {
                _savedKeys.Add(keys.ToList());
                (await _thisPc.SaveAppSettingsAsync(settings, keys)).Should().BeTrue();
                return _reportSaveResult; // 既定は false: 成功の後処理（WPF 依存）へ進ませない（クラスの remarks）
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
            _summaryGenerator,
            new Mock<IStaffAuthService>().Object,
            new Mock<ISettingsChangeService>().Object,
            _configDirectory);
        await viewModel.LoadSettingsAsync();
        return viewModel;
    }
}
