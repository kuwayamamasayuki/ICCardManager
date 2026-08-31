using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1955: 明細 CSV 取込の摘要再生成が、DB に保存された部署種別に従うことを表明する。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装は <c>CsvImportService.Detail.cs</c> と <c>NewLedgerFromSegmentsBuilder</c> の 2 経路が
/// <c>new SummaryGenerator()</c>（既定 ＝ <see cref="DepartmentType.MayorOffice"/>）で摘要を作り直して
/// いたため、企業会計部局に設定した組織でもチャージ行が「役務費によりチャージ」で
/// 6 年保存の台帳へ書き込まれ、そのまま物品出納簿に印字されていた。
/// 他の経路（<c>App.xaml.cs</c> の DI ファクトリ / <c>BusStopInputViewModel</c>）は設定を注入して
/// いたので、<b>設定が効く経路と効かない経路が混在する</b>という最も紛らわしい状態だった。
/// </para>
/// <para>
/// テストは<b>既定と異なる部署種別を設定してから</b>呼ぶ（既定のままだと修正前のコードでも緑になる。
/// <c>.claude/rules/development-conventions.md</c> #1818）。あわせて<b>対の表明</b>として
/// 市長事務部局のケースも置く — 企業会計部局側だけだと「常に旅費によりチャージ」と
/// 決め打ちした実装でも緑になる。
/// </para>
/// </remarks>
public class CsvImportServiceDepartmentTypeTests : IDisposable
{
    private const string TestCardIdm = "0123456789ABCDEF";

    /// <summary>UTF-8 with BOM（<c>ReadCsvFileAsync</c> の文字コード判別を通すため）。</summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private const string Header =
        "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";

    private readonly string _testDirectory;

    public CsvImportServiceDepartmentTypeTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportDeptType_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 既存の利用履歴へ明細を差し替える経路（<c>CsvImportService.Detail.cs</c>）。
    /// </summary>
    [Theory]
    [InlineData(DepartmentType.EnterpriseAccount, "旅費によりチャージ")]
    [InlineData(DepartmentType.MayorOffice, "役務費によりチャージ")]
    public async Task 既存履歴の摘要再生成は部署種別に従うこと(
        DepartmentType departmentType, string expectedSummary)
    {
        // Arrange
        var csvPath = CreateCsv($"1,2026-08-01 09:00:00,{TestCardIdm},001,,,,-3000,3000,1,0,0,");

        var ledgerRepositoryMock = new Mock<ILedgerRepository>();
        ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(() => new Ledger
            {
                Id = 1,
                CardIdm = TestCardIdm,
                Date = new DateTime(2026, 8, 1),
                Summary = "取込前の摘要",
                Details = new List<LedgerDetail>()
            });
        ledgerRepositoryMock
            .Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        Ledger? updatedLedger = null;
        ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => updatedLedger = l)
            .ReturnsAsync(true);

        var service = CreateService(ledgerRepositoryMock, departmentType);

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeTrue(
            string.Join(" / ", result.Errors.ConvertAll(e => e.Message)));
        updatedLedger.Should().NotBeNull();
        updatedLedger!.Summary.Should().Be(expectedSummary,
            "摘要の再生成は DB に保存された部署種別に従う（Issue #1955）");
    }

    /// <summary>
    /// 利用履歴 ID 空欄から新規の利用履歴を作る経路（<c>NewLedgerFromSegmentsBuilder</c>）。
    /// </summary>
    [Theory]
    [InlineData(DepartmentType.EnterpriseAccount, "旅費によりチャージ")]
    [InlineData(DepartmentType.MayorOffice, "役務費によりチャージ")]
    public async Task 新規履歴の摘要生成は部署種別に従うこと(
        DepartmentType departmentType, string expectedSummary)
    {
        // Arrange（利用履歴ID を空欄にすると Ledger ごと自動生成される）
        var csvPath = CreateCsv($",2026-08-01 09:00:00,{TestCardIdm},001,,,,-3000,3000,1,0,0,");

        var ledgerRepositoryMock = new Mock<ILedgerRepository>();

        Ledger? insertedLedger = null;
        ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => insertedLedger = l)
            .ReturnsAsync(42);
        ledgerRepositoryMock
            .Setup(x => x.InsertDetailsAsync(42, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        var service = CreateService(ledgerRepositoryMock, departmentType);

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeTrue(
            string.Join(" / ", result.Errors.ConvertAll(e => e.Message)));
        insertedLedger.Should().NotBeNull();
        insertedLedger!.Summary.Should().Be(expectedSummary,
            "新規作成の摘要も DB に保存された部署種別に従う（Issue #1955）");
    }

    private string CreateCsv(string dataLine)
    {
        var path = Path.Combine(_testDirectory, $"details_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, $"{Header}\n{dataLine}\n", CsvEncoding);
        return path;
    }

    private static CsvImportService CreateService(
        Mock<ILedgerRepository> ledgerRepositoryMock, DepartmentType departmentType)
    {
        var cardRepositoryMock = new Mock<ICardRepository>();
        cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "001" });

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { DepartmentType = departmentType });

        return new CsvImportService(
            cardRepositoryMock.Object,
            new Mock<IStaffRepository>().Object,
            ledgerRepositoryMock.Object,
            new Mock<IValidationService>().Object,
            new Mock<DbContext>().Object,
            new Mock<ICacheService>().Object,
            settingsRepositoryMock.Object);
    }
}
