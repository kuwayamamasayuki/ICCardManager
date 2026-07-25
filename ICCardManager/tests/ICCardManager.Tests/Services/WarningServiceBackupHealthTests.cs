using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// WarningService のバックアップ健全性警告の単体テスト（Issue #1689）
/// </summary>
/// <remarks>
/// 「最終成功からの経過日数」で判定する。連続失敗回数ではなく経過日数を使うのは、
/// 長期休暇などでアプリを起動しなかった期間は失敗回数が増えないまま
/// 古いバックアップだけが残る穴を塞ぐため。
/// </remarks>
public class WarningServiceBackupHealthTests
{
    private static readonly DateTime Now = new DateTime(2026, 7, 25, 9, 0, 0);

    private static WarningService CreateService(BackupHealthInfo health)
    {
        var backupHealthMock = new Mock<IBackupHealthService>();
        backupHealthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(health);

        return new WarningService(
            new Mock<ILedgerRepository>().Object,
            new Mock<IDatabaseInfo>().Object,
            updateNotificationService: null,
            backupHealthService: backupHealthMock.Object);
    }

    private static BackupHealthInfo HealthWithLastSuccessDaysAgo(int days) =>
        new BackupHealthInfo { LastSuccessAt = Now.Date.AddDays(-days).AddHours(8) };

    #region しきい値判定

    [Theory]
    [InlineData(0)]   // 本日成功
    [InlineData(1)]
    [InlineData(7)]   // しきい値ちょうど（AppConstants.BackupStaleWarningDays）は警告しない
    public async Task CheckBackupHealthWarningAsync_WithinThreshold_ReturnsNull(int daysAgo)
    {
        var service = CreateService(HealthWithLastSuccessDaysAgo(daysAgo));

        var warning = await service.CheckBackupHealthWarningAsync(Now);

        warning.Should().BeNull();
    }

    [Theory]
    [InlineData(8)]   // しきい値超過の最小値
    [InlineData(12)]
    [InlineData(60)]
    public async Task CheckBackupHealthWarningAsync_BeyondThreshold_ReturnsWarning(int daysAgo)
    {
        var service = CreateService(HealthWithLastSuccessDaysAgo(daysAgo));

        var warning = await service.CheckBackupHealthWarningAsync(Now);

        warning.Should().NotBeNull();
        warning.Type.Should().Be(WarningType.BackupStale);
        warning.DisplayText.Should().Contain($"{daysAgo}日間");
    }

    [Fact]
    public void BackupStaleWarningDays_IsSevenDays()
    {
        // しきい値を変更した場合に上記 Theory の境界値も見直す必要があることを明示する
        AppConstants.BackupStaleWarningDays.Should().Be(7);
    }

    #endregion

    #region 判断材料がない場合

    [Fact]
    public async Task CheckBackupHealthWarningAsync_WithNoSuccessRecord_ReturnsNull()
    {
        // Issue #1689 導入前からの既存環境は初回起動時点で必ず記録なし。
        // ここで警告を出すと正常な環境でも必ず警告が出て「オオカミ少年」になる。
        var service = CreateService(new BackupHealthInfo { LastSuccessAt = null });

        var warning = await service.CheckBackupHealthWarningAsync(Now);

        warning.Should().BeNull();
    }

    [Fact]
    public async Task CheckBackupHealthWarningAsync_WithoutBackupHealthService_ReturnsNull()
    {
        // DI 未注入（既存テストの構築コード互換）でも例外にせず警告なしとする
        var service = new WarningService(
            new Mock<ILedgerRepository>().Object,
            new Mock<IDatabaseInfo>().Object);

        var warning = await service.CheckBackupHealthWarningAsync(Now);

        warning.Should().BeNull();
    }

    #endregion

    #region エラーメッセージ品質（.claude/rules/error-messages.md）

    [Fact]
    public async Task BackupStaleWarning_SatisfiesErrorMessageQualityCriteria()
    {
        var service = CreateService(HealthWithLastSuccessDaysAgo(12));

        var warning = await service.CheckBackupHealthWarningAsync(Now);
        var text = warning.DisplayText;

        // 「何が」: 対象（自動バックアップ）と経過日数・最終成功日時が具体的に示されている
        text.Should().Contain("自動バックアップ");
        text.Should().Contain("12日間");
        text.Should().Contain(DisplayFormatters.FormatDateTime(Now.Date.AddDays(-12).AddHours(8)));

        // 「なぜ」: 何が問題で帳票／監査上どう困るのかの原因候補が示されている
        text.Should().MatchRegex("空き容量|アクセス権");

        // 「どうすれば」: 具体的な操作場所を示し、行動指示で終わる
        text.Should().Contain("システム管理画面（F6）");
        Regex.IsMatch(text, "してください。?$").Should().BeTrue("行動指示型で終わること");

        // 情報不足を防ぐ最低文字数
        text.Length.Should().BeGreaterThan(20);
    }

    #endregion
}
