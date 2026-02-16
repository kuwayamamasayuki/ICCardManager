using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// 部署種別（DepartmentType）に関連する機能の単体テスト（Issue #659）
/// </summary>
public class DepartmentTypeTests
{
    #region SummaryGenerator - GetChargeSummary

    [Fact]
    public void GetChargeSummary_MayorOffice_Returns役務費()
    {
        // Act
        var result = SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice);

        // Assert
        result.Should().Be("役務費によりチャージ");
    }

    [Fact]
    public void GetChargeSummary_EnterpriseAccount_Returns旅費()
    {
        // Act
        var result = SummaryGenerator.GetChargeSummary(DepartmentType.EnterpriseAccount);

        // Assert
        result.Should().Be("旅費によりチャージ");
    }

    [Fact]
    public void GetChargeSummary_NoParameter_DefaultsToMayorOffice()
    {
        // Act
        var result = SummaryGenerator.GetChargeSummary();

        // Assert
        result.Should().Be("役務費によりチャージ");
    }

    #endregion

    #region SummaryGenerator - Generate with DepartmentType

    [Fact]
    public void Generate_ChargeOnly_MayorOffice_Returns役務費()
    {
        // Arrange
        var generator = new SummaryGenerator(DepartmentType.MayorOffice);
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsCharge = true, Amount = 3000 }
        };

        // Act
        var result = generator.Generate(details);

        // Assert
        result.Should().Be("役務費によりチャージ");
    }

    [Fact]
    public void Generate_ChargeOnly_EnterpriseAccount_Returns旅費()
    {
        // Arrange
        var generator = new SummaryGenerator(DepartmentType.EnterpriseAccount);
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsCharge = true, Amount = 3000 }
        };

        // Act
        var result = generator.Generate(details);

        // Assert
        result.Should().Be("旅費によりチャージ");
    }

    [Fact]
    public void Generate_DefaultConstructor_ChargeOnly_Returns役務費()
    {
        // Arrange - デフォルトコンストラクタは市長事務部局
        var generator = new SummaryGenerator();
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsCharge = true, Amount = 3000 }
        };

        // Act
        var result = generator.Generate(details);

        // Assert
        result.Should().Be("役務費によりチャージ");
    }

    [Fact]
    public void Generate_MixedChargeAndRailway_EnterpriseAccount_DoesNotIncludeChargeSummary()
    {
        // Arrange - チャージと利用が混在する場合、Generate()は利用の摘要のみ返す
        var generator = new SummaryGenerator(DepartmentType.EnterpriseAccount);
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsCharge = true, Amount = 3000 },
            new LedgerDetail { EntryStation = "博多", ExitStation = "天神", IsBus = false }
        };

        // Act
        var result = generator.Generate(details);

        // Assert - チャージと利用混在の場合、利用部分だけが返される
        result.Should().Contain("博多");
        result.Should().Contain("天神");
        result.Should().NotContain("チャージ");
    }

    #endregion

    #region SummaryGenerator - GenerateByDate with DepartmentType

    [Fact]
    public void GenerateByDate_ChargeEntry_MayorOffice_Returns役務費()
    {
        // Arrange
        var generator = new SummaryGenerator(DepartmentType.MayorOffice);
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                IsCharge = true,
                Amount = 3000,
                UseDate = new DateTime(2025, 4, 1, 10, 0, 0)
            }
        };

        // Act
        var result = generator.GenerateByDate(details);

        // Assert
        result.Should().HaveCount(1);
        result[0].Summary.Should().Be("役務費によりチャージ");
        result[0].IsCharge.Should().BeTrue();
    }

    [Fact]
    public void GenerateByDate_ChargeEntry_EnterpriseAccount_Returns旅費()
    {
        // Arrange
        var generator = new SummaryGenerator(DepartmentType.EnterpriseAccount);
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                IsCharge = true,
                Amount = 3000,
                UseDate = new DateTime(2025, 4, 1, 10, 0, 0)
            }
        };

        // Act
        var result = generator.GenerateByDate(details);

        // Assert
        result.Should().HaveCount(1);
        result[0].Summary.Should().Be("旅費によりチャージ");
        result[0].IsCharge.Should().BeTrue();
    }

    #endregion

    #region SettingsRepository - Parse/Serialize

    [Theory]
    [InlineData("mayor_office", DepartmentType.MayorOffice)]
    [InlineData("enterprise_account", DepartmentType.EnterpriseAccount)]
    [InlineData("MAYOR_OFFICE", DepartmentType.MayorOffice)]
    [InlineData("ENTERPRISE_ACCOUNT", DepartmentType.EnterpriseAccount)]
    [InlineData("Mayor_Office", DepartmentType.MayorOffice)]
    public void ParseDepartmentType_ValidValues_ReturnsCorrectEnum(string value, DepartmentType expected)
    {
        // Act
        var result = SettingsRepository.ParseDepartmentType(value);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("invalid_value")]
    public void ParseDepartmentType_InvalidValues_DefaultsToMayorOffice(string value)
    {
        // Act
        var result = SettingsRepository.ParseDepartmentType(value);

        // Assert
        result.Should().Be(DepartmentType.MayorOffice);
    }

    [Theory]
    [InlineData(DepartmentType.MayorOffice, "mayor_office")]
    [InlineData(DepartmentType.EnterpriseAccount, "enterprise_account")]
    public void DepartmentTypeToString_ReturnsCorrectString(DepartmentType value, string expected)
    {
        // Act
        var result = SettingsRepository.DepartmentTypeToString(value);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(DepartmentType.MayorOffice)]
    [InlineData(DepartmentType.EnterpriseAccount)]
    public void ParseDepartmentType_RoundTrip_PreservesValue(DepartmentType original)
    {
        // Act - シリアライズしてパースし直す
        var serialized = SettingsRepository.DepartmentTypeToString(original);
        var deserialized = SettingsRepository.ParseDepartmentType(serialized);

        // Assert
        deserialized.Should().Be(original);
    }

    #endregion

    #region TemplateResolver - DepartmentType

    [Fact]
    public void TemplateExists_MayorOffice_ReturnsTrue()
    {
        // Act - 埋め込みリソースとして存在するはず
        var exists = TemplateResolver.TemplateExists(DepartmentType.MayorOffice);

        // Assert
        exists.Should().BeTrue("市長事務部局テンプレートが埋め込みリソースとして存在するため");
    }

    [Fact]
    public void TemplateExists_EnterpriseAccount_ReturnsTrue()
    {
        // Act - 埋め込みリソースとして存在するはず
        var exists = TemplateResolver.TemplateExists(DepartmentType.EnterpriseAccount);

        // Assert
        exists.Should().BeTrue("企業会計部局テンプレートが埋め込みリソースとして存在するため");
    }

    [Fact]
    public void ResolveTemplatePath_MayorOffice_ReturnsValidPath()
    {
        // Act
        var path = TemplateResolver.ResolveTemplatePath(DepartmentType.MayorOffice);

        // Assert
        path.Should().NotBeNullOrEmpty();
        path.Should().EndWith(".xlsx");
        path.Should().Contain("市長事務部局");
    }

    [Fact]
    public void ResolveTemplatePath_EnterpriseAccount_ReturnsValidPath()
    {
        // Act
        var path = TemplateResolver.ResolveTemplatePath(DepartmentType.EnterpriseAccount);

        // Assert
        path.Should().NotBeNullOrEmpty();
        path.Should().EndWith(".xlsx");
        path.Should().Contain("企業会計部局");
    }

    [Fact]
    public void ResolveTemplatePath_DifferentDepartments_ReturnDifferentPaths()
    {
        // Act
        var mayorPath = TemplateResolver.ResolveTemplatePath(DepartmentType.MayorOffice);
        var enterprisePath = TemplateResolver.ResolveTemplatePath(DepartmentType.EnterpriseAccount);

        // Assert
        mayorPath.Should().NotBe(enterprisePath, "部署種別ごとに異なるテンプレートを使用するため");
    }

    [Fact]
    public void ResolveTemplatePath_NoParameter_DefaultsToMayorOffice()
    {
        // Act
        var defaultPath = TemplateResolver.ResolveTemplatePath();
        var mayorPath = TemplateResolver.ResolveTemplatePath(DepartmentType.MayorOffice);

        // Assert
        defaultPath.Should().Be(mayorPath, "パラメータなしは市長事務部局と同じ結果を返すべき");
    }

    #endregion

    #region AppSettings - DepartmentType デフォルト値

    [Fact]
    public void AppSettings_DefaultDepartmentType_IsMayorOffice()
    {
        // Act
        var settings = new AppSettings();

        // Assert
        settings.DepartmentType.Should().Be(DepartmentType.MayorOffice);
    }

    #endregion
}
