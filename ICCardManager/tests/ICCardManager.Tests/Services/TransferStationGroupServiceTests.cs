using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// <see cref="TransferStationGroupService"/> のテスト（Issue #1905）
/// </summary>
/// <remarks>
/// <see cref="TransferStationGroupService.SaveGroupsAsync"/> は
/// <see cref="SummaryGenerator"/> の静的状態を書き換えるため、
/// <see cref="SummaryGeneratorCollection"/> に属する必要がある。
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class TransferStationGroupServiceTests : IDisposable
{
    private readonly Mock<ISettingsRepository> _settingsRepository = new();
    private readonly Mock<ILogger<TransferStationGroupService>> _logger = new();
    private readonly OrganizationOptions _organizationOptions = new();

    public TransferStationGroupServiceTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    private TransferStationGroupService CreateService() => new(
        _settingsRepository.Object,
        _organizationOptions,
        _logger.Object);

    private void ArrangeStoredValue(string value) =>
        _settingsRepository
            .Setup(r => r.GetAsync(SettingsRepository.KeyTransferStationGroups))
            .ReturnsAsync(value);

    #region GetGroupsAsync

    [Fact]
    public async Task GetGroupsAsync_DBに未保存_組織設定の初期値を返すこと()
    {
        // Arrange: 画面から一度も保存していない環境
        ArrangeStoredValue(null);

        // Act
        var groups = await CreateService().GetGroupsAsync();

        // Assert: appsettings.json（未指定なら C# 既定値）の 天神/西鉄福岡(天神)、千早/西鉄千早
        groups.Should().BeEquivalentTo(_organizationOptions.SummaryRules.TransferStationGroups);
    }

    [Fact]
    public async Task GetGroupsAsync_DBに保存済み_保存された値を返すこと()
    {
        // Arrange: 既定と異なる値を保存済みにする
        // （既定のままだとフォールバックした実装でも緑になるため。Issue #1818 の作法）
        ArrangeStoredValue(@"[[""天神日銀前"",""天神中央郵便局前""]]");

        // Act
        var groups = await CreateService().GetGroupsAsync();

        // Assert
        groups.Should().HaveCount(1);
        groups[0].Should().Equal("天神日銀前", "天神中央郵便局前");
    }

    [Theory]
    [InlineData("これはJSONではない")]
    [InlineData("{\"groups\": []}")]
    [InlineData("[null]")]
    [InlineData("[[\"天神\", null]]")]
    public async Task GetGroupsAsync_解釈できない値_初期値へ縮退し警告を残すこと(string stored)
    {
        // Arrange
        ArrangeStoredValue(stored);

        // Act
        var groups = await CreateService().GetGroupsAsync();

        // Assert: 初期値へ縮退する
        groups.Should().BeEquivalentTo(_organizationOptions.SummaryRules.TransferStationGroups);

        // Issue #1819: 縮退したことを本番ログ（Information 以上）へ残す。
        // LogDebug では appsettings.json の既定レベル（Information）で出力されない
        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetGroupsAsync_解釈できない値_保存済みの値を上書きしないこと()
    {
        // Arrange: 壊れた値は管理者が原因を追えるよう残す
        ArrangeStoredValue("これはJSONではない");

        // Act
        await CreateService().GetGroupsAsync();

        // Assert
        _settingsRepository.Verify(
            r => r.SetAsync(SettingsRepository.KeyTransferStationGroups, It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGroupsAsync_正常な値_警告を出さないこと()
    {
        // Arrange
        ArrangeStoredValue(@"[[""天神日銀前"",""天神中央郵便局前""]]");

        // Act
        await CreateService().GetGroupsAsync();

        // Assert: 常に警告を出す実装になっていないことを対で固定する
        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never);
    }

    #endregion

    #region SaveGroupsAsync

    [Fact]
    public async Task SaveGroupsAsync_保存成功_JSONで永続化されSummaryGeneratorへ反映されること()
    {
        // Arrange
        string savedJson = null;
        _settingsRepository
            .Setup(r => r.SetAsync(SettingsRepository.KeyTransferStationGroups, It.IsAny<string>()))
            .Callback<string, string>((_, value) => savedJson = value)
            .ReturnsAsync(true);

        // Act
        var result = await CreateService().SaveGroupsAsync(new[]
        {
            new[] { "天神日銀前", "天神中央郵便局前" }
        });

        // Assert
        result.Should().BeTrue();

        // System.Text.Json は既定で非 ASCII を \uXXXX へエスケープするため、
        // 保存文字列のリテラル一致ではなく「読み戻せること」で表明する
        TransferStationGroupService.TryDeserialize(savedJson, out var persisted).Should().BeTrue();
        persisted.Should().HaveCount(1);
        persisted[0].Should().Equal("天神日銀前", "天神中央郵便局前");

        // 再起動を待たずに実行中の摘要生成へ反映されること
        SummaryGenerator.GetTransferStationGroups().Should().HaveCount(1);
        SummaryGenerator.GetTransferStationGroups()[0].Should().Equal("天神日銀前", "天神中央郵便局前");
    }

    [Fact]
    public async Task SaveGroupsAsync_保存失敗_SummaryGeneratorへ反映しないこと()
    {
        // Arrange: 書き込めなかった（共有モードのロック等）
        _settingsRepository
            .Setup(r => r.SetAsync(SettingsRepository.KeyTransferStationGroups, It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        var result = await CreateService().SaveGroupsAsync(new[]
        {
            new[] { "天神日銀前", "天神中央郵便局前" }
        });

        // Assert: 「保存できませんでした」と案内しながら摘要生成だけ新しい値で動く食い違いを作らない
        result.Should().BeFalse();
        SummaryGenerator.GetTransferStationGroups()
            .Should().BeEquivalentTo(new OrganizationOptions().SummaryRules.TransferStationGroups);
    }

    [Fact]
    public async Task SaveGroupsAsync_正規化してから保存すること()
    {
        // Arrange
        string savedJson = null;
        _settingsRepository
            .Setup(r => r.SetAsync(SettingsRepository.KeyTransferStationGroups, It.IsAny<string>()))
            .Callback<string, string>((_, value) => savedJson = value)
            .ReturnsAsync(true);

        // Act: 前後空白・空要素・重複・1 件だけのグループを含む入力
        await CreateService().SaveGroupsAsync(new[]
        {
            new[] { "  天神日銀前  ", "", "天神中央郵便局前", "天神日銀前" },
            new[] { "単独" }
        });

        // Assert
        TransferStationGroupService.TryDeserialize(savedJson, out var restored).Should().BeTrue();
        restored.Should().HaveCount(1, "1 件だけのグループは同一視する相手が無いため捨てられる");
        restored[0].Should().Equal("天神日銀前", "天神中央郵便局前");
    }

    #endregion

    #region Normalize / TryDeserialize（純関数）

    [Fact]
    public void Normalize_空白のみの名前を除去すること()
    {
        var result = TransferStationGroupService.Normalize(new[]
        {
            new[] { "天神", "   ", "西鉄福岡(天神)" }
        });

        result.Should().HaveCount(1);
        result[0].Should().Equal("天神", "西鉄福岡(天神)");
    }

    [Fact]
    public void Normalize_nullを渡しても空リストを返すこと()
    {
        TransferStationGroupService.Normalize(null).Should().BeEmpty();
    }

    [Fact]
    public void TryDeserialize_保存した値をそのまま復元できること()
    {
        // Arrange: 実際の保存経路（Normalize → Serialize）を通した文字列を入力にする
        var original = TransferStationGroupService.Normalize(new[]
        {
            new[] { "天神日銀前", "天神中央郵便局前" },
            new[] { "天神", "西鉄福岡(天神)" }
        });
        var json = System.Text.Json.JsonSerializer.Serialize(original);

        // Act
        var ok = TransferStationGroupService.TryDeserialize(json, out var restored);

        // Assert
        ok.Should().BeTrue();
        restored.Should().BeEquivalentTo(original);
    }

    #endregion
}
