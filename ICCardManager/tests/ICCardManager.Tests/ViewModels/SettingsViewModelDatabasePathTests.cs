using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1559: SettingsViewModel の「データベース保存先をデフォルトに戻す」コマンドの単体テスト。
/// </summary>
public class SettingsViewModelDatabasePathTests
{
    private static SettingsViewModel CreateVm(
        Mock<IDialogService> dialogMock,
        Mock<ISettingsRepository>? repoMock = null,
        Mock<IValidationService>? validatorMock = null,
        Mock<ISoundPlayer>? soundMock = null)
    {
        repoMock ??= new Mock<ISettingsRepository>();
        validatorMock ??= new Mock<IValidationService>();
        validatorMock.Setup(v => v.ValidateWarningBalance(It.IsAny<int>()))
            .Returns(ValidationResult.Success());
        soundMock ??= new Mock<ISoundPlayer>();
        var options = Options.Create(new DatabaseOptions());
        return new SettingsViewModel(
            repoMock.Object,
            validatorMock.Object,
            soundMock.Object,
            options,
            dialogMock.Object);
    }

    [Fact]
    public void ResetDatabasePathToDefault_UserConfirms_DeletesConfigAndClearsPath()
    {
        var configPath = SettingsViewModel.GetDatabaseConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, @"C:\some\local\db\iccard.db");

        try
        {
            var dialogMock = new Mock<IDialogService>();
            dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            var vm = CreateVm(dialogMock);

            vm.ResetDatabasePathToDefaultCommand.Execute(null);

            File.Exists(configPath).Should().BeFalse("確認後に config ファイルが削除される");
            vm.DatabasePath.Should().BeEmpty("UI上のパスは空欄に");
            vm.IsDatabasePathChanged.Should().BeTrue("再起動案内を表示するため変更フラグを立てる");
            dialogMock.Verify(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public void ResetDatabasePathToDefault_UserCancels_KeepsConfig()
    {
        var configPath = SettingsViewModel.GetDatabaseConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, @"C:\some\local\db\iccard.db");

        try
        {
            var dialogMock = new Mock<IDialogService>();
            dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

            var vm = CreateVm(dialogMock);
            vm.ResetDatabasePathToDefaultCommand.Execute(null);

            File.Exists(configPath).Should().BeTrue("ユーザーがキャンセルした場合は削除しない");
            vm.IsDatabasePathChanged.Should().BeFalse("キャンセル時はフラグも立てない");
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public void ResetDatabasePathToDefault_ConfigFileNotExists_StillClearsPath()
    {
        // 既に config ファイルが無い状態でも安全にコマンド実行できる
        var configPath = SettingsViewModel.GetDatabaseConfigPath();
        if (File.Exists(configPath)) File.Delete(configPath);

        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var vm = CreateVm(dialogMock);
        vm.ResetDatabasePathToDefaultCommand.Execute(null);

        vm.DatabasePath.Should().BeEmpty();
        vm.IsDatabasePathChanged.Should().BeTrue();
    }
}
