using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
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

    #region 保存後の摘要生成（Issue #2107）

    // 上の SaveGroupsAsync のテストは SummaryGenerator.GetTransferStationGroups()（設定の観測点）しか見ておらず、
    // 保存した値が摘要生成の判定に実際に使われることまでは表明していなかった。観測点だけが新しい値を返し、
    // 生成が古い世代を見続ける形（#1919 の世代の差し替え漏れ）でも緑になる。

    /// <summary>報告事例（#1905）: 天神日銀前→下原中央、下原中央→天神中央郵便局前（履歴は新しい順）。</summary>
    private static List<LedgerDetail> TenjinRoundTripByBus() => new()
    {
        new LedgerDetail { UseDate = new DateTime(2024, 12, 9), Amount = 230, Balance = 4330, IsBus = true, BusStops = "下原中央～天神中央郵便局前" },
        new LedgerDetail { UseDate = new DateTime(2024, 12, 9), Amount = 230, Balance = 4560, IsBus = true, BusStops = "天神日銀前～下原中央" },
    };

    /// <summary>
    /// 保存に成功したら、既に動いている摘要生成（Singleton）の次の生成から、新しいグループで往復を検出すること
    /// </summary>
    /// <remarks>
    /// 生成器は保存の<b>前</b>に作る。本番の <see cref="SummaryGenerator"/> は Singleton で起動時に作られるため、
    /// 生成器の構築時に設定を取り込む実装では、画面から保存しても再起動まで反映されない。
    /// </remarks>
    [Fact]
    public async Task SaveGroupsAsync_保存成功_以後の摘要生成が新しいグループで往復を検出すること()
    {
        var generator = new SummaryGenerator();
        _settingsRepository
            .Setup(r => r.SetAsync(SettingsRepository.KeyTransferStationGroups, It.IsAny<string>()))
            .ReturnsAsync(true);

        await CreateService().SaveGroupsAsync(new[] { new[] { "天神日銀前", "天神中央郵便局前" } });

        generator.GenerateByDate(TenjinRoundTripByBus()).Should().ContainSingle()
            .Which.Summary.Should().Be("バス（天神日銀前（天神中央郵便局前）～下原中央 往復）",
                "同一視を登録したので往復として認識され、目的地（下原中央）が残るべき");
    }

    /// <summary>
    /// 対の表明: 保存に失敗したら、摘要生成は従来のグループのまま（目的地が乗継として省略される）であること
    /// </summary>
    /// <remarks>
    /// これが無いと、保存の成否にかかわらず同じ摘要になる入力（グループが効かない入力）でも上のテストが緑になり得る。
    /// </remarks>
    [Fact]
    public async Task SaveGroupsAsync_保存失敗_摘要生成は従来のグループのままであること()
    {
        var generator = new SummaryGenerator();
        _settingsRepository
            .Setup(r => r.SetAsync(SettingsRepository.KeyTransferStationGroups, It.IsAny<string>()))
            .ReturnsAsync(false);

        await CreateService().SaveGroupsAsync(new[] { new[] { "天神日銀前", "天神中央郵便局前" } });

        generator.GenerateByDate(TenjinRoundTripByBus()).Should().ContainSingle()
            .Which.Summary.Should().Be("バス（天神日銀前～天神中央郵便局前）",
                "保存できなかったグループを摘要生成に使わないべき");
    }

    #endregion
}
