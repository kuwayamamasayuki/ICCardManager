using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1559: SettingsViewModel の「データベース保存先をデフォルトに戻す」コマンドの単体テスト。
/// </summary>
/// <remarks>
/// Issue #2098: 以前は <c>SettingsViewModel.GetDatabaseConfigPath()</c>（= 開発機の本物の
/// <c>C:\ProgramData\ICCardManager\database_config.txt</c>）を直接上書き・削除しており、
/// 共有モードを設定した PC ではテストを実行するたびに共有 DB のパス設定が消えていた。
/// 並列に走る他のテストクラスも同じファイルを読むため、実行順にも依存していた。
/// 設定ファイルの置き場所を注入できるコンストラクタで、テストごとの一時フォルダーを使う。
/// </remarks>
public class SettingsViewModelDatabasePathTests : IDisposable
{
    private const string ConfigFileName = "database_config.txt";

    private readonly string _configDirectory;

    /// <summary>
    /// Issue #2264: 「デフォルトに戻す」は職員証の認証を求め、操作ログ（現在の DB）に記録する。
    /// 記録は本物のサービスでインメモリ DB へ書く
    /// </summary>
    private readonly DbContext _dbContext;

    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();

    public SettingsViewModelDatabasePathTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "ICCardManagerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_configDirectory);

        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        // 既定: 認証は成功する（操作者の帰属は SettingsViewModelSecuritySettingsTests が表明する）
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "FFFF0000000000AA", StaffName = "認証 太郎" });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch
        {
            // 後片付けの失敗はテスト結果に影響させない（一時フォルダー配下）
        }
    }

    private string ConfigPath => Path.Combine(_configDirectory, ConfigFileName);

    private SettingsViewModel CreateVm(
        Mock<IDialogService> dialogMock,
        Mock<ISettingsRepository>? repoMock = null)
    {
        repoMock ??= new Mock<ISettingsRepository>();
        var validatorMock = new Mock<IValidationService>();
        validatorMock.Setup(v => v.ValidateCompanionCountInputTimeout(It.IsAny<int>()))
            .Returns(ValidationResult.Success());
        validatorMock.Setup(v => v.ValidateWarningBalance(It.IsAny<int>()))
            .Returns(ValidationResult.Success());
        validatorMock.Setup(v => v.ValidateLongTermUnreturnedDays(It.IsAny<int>()))
            .Returns(ValidationResult.Success());
        var settingsRepository = new SettingsRepository(
            _dbContext, new Mock<ICacheService>().Object, Options.Create(new CacheOptions()));
        var settingsChangeService = new SettingsChangeService(
            _dbContext,
            settingsRepository,
            new OperationLogger(new OperationLogRepository(_dbContext), new Mock<ICurrentOperatorContext>().Object),
            // 職員の登録状況（既定: 登録済みの職員がいる＝認証を求める。認証の代役は成功を返す）
            new StaffManagementService(
                _dbContext,
                Mock.Of<IStaffRepository>(r => r.HasActiveStaffAsync() == Task.FromResult(true)),
                new OperationLogger(new OperationLogRepository(_dbContext), new Mock<ICurrentOperatorContext>().Object),
                NullLogger<StaffManagementService>.Instance),
            new SystemClock(),
            NullLogger<SettingsChangeService>.Instance);
        return new SettingsViewModel(
            repoMock.Object,
            validatorMock.Object,
            new Mock<ISoundPlayer>().Object,
            dialogMock.Object,
            new SummaryGenerator(DepartmentType.MayorOffice),
            _staffAuthServiceMock.Object,
            settingsChangeService,
            _configDirectory);
    }

    [Fact]
    public async Task ResetDatabasePathToDefault_UserConfirms_DeletesConfigAndShowsDefaultFolder()
    {
        File.WriteAllText(ConfigPath, @"C:\some\local\db\iccard.db");

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var vm = CreateVm(dialogMock);

        await vm.ResetDatabasePathToDefaultAsync();

        File.Exists(ConfigPath).Should().BeFalse("確認後に config ファイルが削除される");
        vm.DatabasePath.Should().Be(SettingsViewModel.GetDefaultDatabaseFolder(),
            "UI上のパスはデフォルトフォルダ（C:\\ProgramData\\ICCardManager\\）を表示する");
        vm.IsDatabasePathChanged.Should().BeFalse(
            "config 削除済みのため SaveAsync 側の再保存処理は不要（変更フラグは立てない）");
        dialogMock.Verify(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ResetDatabasePathToDefault_UserCancels_KeepsConfig()
    {
        File.WriteAllText(ConfigPath, @"C:\some\local\db\iccard.db");

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var vm = CreateVm(dialogMock);
        await vm.ResetDatabasePathToDefaultAsync();

        File.Exists(ConfigPath).Should().BeTrue("ユーザーがキャンセルした場合は削除しない");
        vm.IsDatabasePathChanged.Should().BeFalse("キャンセル時はフラグも立てない");
    }

    [Fact]
    public async Task ResetDatabasePathToDefault_ConfigFileNotExists_StillShowsDefaultFolder()
    {
        // 既に config ファイルが無い状態でも安全にコマンド実行できる
        File.Exists(ConfigPath).Should().BeFalse("前提: 一時フォルダーには config ファイルが無い");

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var vm = CreateVm(dialogMock);
        await vm.ResetDatabasePathToDefaultAsync();

        vm.DatabasePath.Should().Be(SettingsViewModel.GetDefaultDatabaseFolder());
        vm.IsDatabasePathChanged.Should().BeFalse();
        // Issue #2264: 何も変わらない（既にデフォルト）ので認証を求めない
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Issue #2098: コンストラクタが、注入された置き場所の database_config.txt を読むこと。
    /// </summary>
    /// <remarks>
    /// 注入を受け取りながら本物の置き場所を読む実装だと、上のテストは「config が無い」前提で
    /// 動いてしまい、注入の効き目を何も表明しない。読み込み側でも注入が効いていることを固定する。
    /// </remarks>
    [Fact]
    public void Constructor_注入された置き場所の設定ファイルからDB保存先を読むこと()
    {
        File.WriteAllText(ConfigPath, @"\\server\share\iccard\iccard.db");

        var vm = CreateVm(new Mock<IDialogService>());

        vm.DatabasePath.Should().Be(@"\\server\share\iccard",
            "database_config.txt のファイルパスからフォルダ部分を表示する");
    }

    /// <summary>
    /// Issue #2098: 保存（SaveAsync）が、注入された置き場所へ設定ファイルを書き込むこと（静的検査）。
    /// Issue #2264 で書き込みは保存の本体（SaveCoreAsync）へ移り、database_config.txt は操作ログとともに
    /// SettingsChangeService が書く（置き場所は画面が渡す）。
    /// </summary>
    /// <remarks>
    /// 書き込みは保存成功後の <c>App.ApplyFontSize</c>（WPF の <c>Application.Current</c> が必要）より
    /// 後ろにあり、単体テストからは到達しない。挙動で表明できないため、本体が静的な置き場所
    /// （<c>AppConfigFiles.DatabaseConfigPath</c> 等 ＝ テストプロセスでは共有、本番では同じ）ではなく
    /// インスタンスの置き場所へ書いていることをソーステキストで固定する。
    /// 「禁止形の不在」と「正しい形の存在」を対で見る（書き込みごと消した実装でも緑にならないように）。
    /// </remarks>
    [Fact]
    public void SaveAsync_注入された置き場所へ設定ファイルを書き込むこと()
    {
        var source = File.ReadAllText(Path.Combine(
            TestPaths.GetProductionSourceRoot(), "ViewModels", "SettingsViewModel.cs"));
        var body = TestSourceInspection.ExtractMethodBody(
            TestSourceInspection.ToCodeOnly(source), "private async Task SaveCoreAsync(SavePlan plan)");

        body.Should().Contain("ChangeDatabaseConfigAsync(DatabaseConfigPath,",
            "DB 保存先は注入された置き場所の database_config.txt へ保存する");
        body.Should().Contain("DepartmentConfigPath,",
            "部署種別は注入された置き場所の department_config.txt へ保存する");
        body.Should().NotContain("AppConfigFiles.DatabaseConfigPath");
        body.Should().NotContain("AppConfigFiles.DepartmentConfigPath");
    }
}
