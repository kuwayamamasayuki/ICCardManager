using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
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

    public SettingsViewModelDatabasePathTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "ICCardManagerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_configDirectory);
    }

    public void Dispose()
    {
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
        return new SettingsViewModel(
            repoMock.Object,
            validatorMock.Object,
            new Mock<ISoundPlayer>().Object,
            dialogMock.Object,
            new SummaryGenerator(DepartmentType.MayorOffice),
            _configDirectory);
    }

    [Fact]
    public void ResetDatabasePathToDefault_UserConfirms_DeletesConfigAndShowsDefaultFolder()
    {
        File.WriteAllText(ConfigPath, @"C:\some\local\db\iccard.db");

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var vm = CreateVm(dialogMock);

        vm.ResetDatabasePathToDefaultCommand.Execute(null);

        File.Exists(ConfigPath).Should().BeFalse("確認後に config ファイルが削除される");
        vm.DatabasePath.Should().Be(SettingsViewModel.GetDefaultDatabaseFolder(),
            "UI上のパスはデフォルトフォルダ（C:\\ProgramData\\ICCardManager\\）を表示する");
        vm.IsDatabasePathChanged.Should().BeFalse(
            "config 削除済みのため SaveAsync 側の再保存処理は不要（変更フラグは立てない）");
        dialogMock.Verify(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ResetDatabasePathToDefault_UserCancels_KeepsConfig()
    {
        File.WriteAllText(ConfigPath, @"C:\some\local\db\iccard.db");

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var vm = CreateVm(dialogMock);
        vm.ResetDatabasePathToDefaultCommand.Execute(null);

        File.Exists(ConfigPath).Should().BeTrue("ユーザーがキャンセルした場合は削除しない");
        vm.IsDatabasePathChanged.Should().BeFalse("キャンセル時はフラグも立てない");
    }

    [Fact]
    public void ResetDatabasePathToDefault_ConfigFileNotExists_StillShowsDefaultFolder()
    {
        // 既に config ファイルが無い状態でも安全にコマンド実行できる
        File.Exists(ConfigPath).Should().BeFalse("前提: 一時フォルダーには config ファイルが無い");

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var vm = CreateVm(dialogMock);
        vm.ResetDatabasePathToDefaultCommand.Execute(null);

        vm.DatabasePath.Should().Be(SettingsViewModel.GetDefaultDatabaseFolder());
        vm.IsDatabasePathChanged.Should().BeFalse();
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

    // 保存（SaveAsync）が注入された置き場所へ database_config.txt / department_config.txt を書くことは、
    // 単体テストからは確かめられない。書き込みは保存成功後の App.ApplyFontSize（WPF の
    // Application.Current が必要）より後ろにあり、テストプロセスでは到達しない。
    // 実機での確認手順は Issue #2098 の PR に記載した。
}
