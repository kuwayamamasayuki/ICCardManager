using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ICCardManager.Tests.Services;

/// <summary>
/// OrganizationOptions のテスト
/// </summary>
public class OrganizationOptionsTests : IDisposable
{
    public OrganizationOptionsTests()
    {
        // テスト間の静的状態汚染を防止
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
    }

    #region デフォルト値テスト

    [Fact]
    public void デフォルト値が既存のハードコード値と一致する()
    {
        var options = new OrganizationOptions();

        // 摘要テキスト
        options.SummaryText.ChargeSummaryMayorOffice.Should().Be("役務費によりチャージ");
        options.SummaryText.ChargeSummaryEnterprise.Should().Be("旅費によりチャージ");
        options.SummaryText.PointRedemption.Should().Be("ポイント還元");
        options.SummaryText.RefundSummary.Should().Be("払戻しによる払出");
        options.SummaryText.LendingSummary.Should().Be("（貸出中）");
        options.SummaryText.CarryoverFromPreviousYear.Should().Be("前年度より繰越");
        options.SummaryText.CarryoverToNextYear.Should().Be("次年度へ繰越");
        options.SummaryText.CumulativeSummary.Should().Be("累計");
        options.SummaryText.RailwayLabel.Should().Be("鉄道");
        options.SummaryText.BusLabel.Should().Be("バス");
        options.SummaryText.BusPlaceholder.Should().Be("★");
        options.SummaryText.RoundTripSuffix.Should().Be(" 往復");

        // 摘要ルール
        options.SummaryRules.EnableRoundTripDetection.Should().BeTrue();
        options.SummaryRules.EnableTransferConsolidation.Should().BeTrue();
        options.SummaryRules.TransferStationGroups.Should().HaveCount(2);

        // エリア優先順位
        options.AreaPriority.DefaultPriority.Should().Equal(3, 0, 1, 2);
        options.AreaPriority.CardTypePriorities.Should().BeEmpty();

        // 帳票レイアウト
        options.ReportLayout.TitleText.Should().Be("物品出納簿");
        options.ReportLayout.ClassificationText.Should().Be("雑品（金券類）");
        options.ReportLayout.UnitText.Should().Be("円");

        // テンプレートマッピング
        options.TemplateMapping.DataStartRow.Should().Be(5);
        options.TemplateMapping.RowsPerPage.Should().Be(12);
        options.TemplateMapping.TotalColumns.Should().Be(12);
    }

    #endregion

    #region JSONバインディングテスト

    [Fact]
    public void JSON設定からバインドできる()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["OrganizationOptions:SummaryText:ChargeSummaryMayorOffice"] = "需用費によりチャージ",
                ["OrganizationOptions:SummaryText:RailwayLabel"] = "電車",
                ["OrganizationOptions:SummaryRules:EnableRoundTripDetection"] = "false",
                ["OrganizationOptions:ReportLayout:TitleText"] = "交通費精算書",
                ["OrganizationOptions:ReportLayout:ClassificationText"] = "交通費",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<OrganizationOptions>(config.GetSection("OrganizationOptions"));
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<OrganizationOptions>>().Value;

        // カスタマイズした値
        options.SummaryText.ChargeSummaryMayorOffice.Should().Be("需用費によりチャージ");
        options.SummaryText.RailwayLabel.Should().Be("電車");
        options.SummaryRules.EnableRoundTripDetection.Should().BeFalse();
        options.ReportLayout.TitleText.Should().Be("交通費精算書");
        options.ReportLayout.ClassificationText.Should().Be("交通費");

        // 未指定はデフォルト値のまま
        options.SummaryText.BusLabel.Should().Be("バス");
        options.SummaryText.PointRedemption.Should().Be("ポイント還元");
        options.SummaryRules.EnableTransferConsolidation.Should().BeTrue();
    }

    [Fact]
    public void セクションが存在しない場合はデフォルト値が使用される()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();

        var services = new ServiceCollection();
        services.Configure<OrganizationOptions>(config.GetSection("OrganizationOptions"));
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<OrganizationOptions>>().Value;

        // 全てデフォルト値
        options.SummaryText.ChargeSummaryMayorOffice.Should().Be("役務費によりチャージ");
        options.SummaryRules.EnableRoundTripDetection.Should().BeTrue();
        options.AreaPriority.DefaultPriority.Should().Equal(3, 0, 1, 2);
    }

    #endregion

    #region SummaryGenerator.Configure テスト

    [Fact]
    public void Configure_摘要テキストが変更される()
    {
        var options = new OrganizationOptions();
        options.SummaryText.ChargeSummaryMayorOffice = "需用費によりチャージ";
        options.SummaryText.PointRedemption = "ボーナスポイント還元";
        options.SummaryText.LendingSummary = "〔使用中〕";

        SummaryGenerator.Configure(options);

        SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice).Should().Be("需用費によりチャージ");
        SummaryGenerator.GetPointRedemptionSummary().Should().Be("ボーナスポイント還元");
        SummaryGenerator.GetLendingSummary().Should().Be("〔使用中〕");
    }

    [Fact]
    public void Configure_繰越テキストのフォーマットが変更される()
    {
        var options = new OrganizationOptions();
        options.SummaryText.CarryoverFromMonthFormat = "{0}月からの繰越金";
        options.SummaryText.MonthlySummaryFormat = "{0}月合計";

        SummaryGenerator.Configure(options);

        SummaryGenerator.GetCarryoverFromPreviousMonthSummary(5).Should().Be("5月からの繰越金");
        SummaryGenerator.GetMonthlySummary(12).Should().Be("12月合計");
    }

    [Fact]
    public void Configure_不足額備考のフォーマットが変更される()
    {
        var options = new OrganizationOptions();
        options.SummaryText.InsufficientBalanceNoteFormat = "運賃{0}円中{1}円は現金払い";

        SummaryGenerator.Configure(options);

        SummaryGenerator.GetInsufficientBalanceNote(210, 140).Should().Be("運賃210円中140円は現金払い");
    }

    [Fact]
    public void Configure_往復検出を無効にすると個別表示される()
    {
        var options = new OrganizationOptions();
        options.SummaryRules.EnableRoundTripDetection = false;

        SummaryGenerator.Configure(options);

        var generator = new SummaryGenerator();
        var details = new List<LedgerDetail>
        {
            new() { EntryStation = "天神", ExitStation = "博多", IsBus = false, Amount = 260, Balance = 740, SequenceNumber = 1 },
            new() { EntryStation = "博多", ExitStation = "天神", IsBus = false, Amount = 260, Balance = 1000, SequenceNumber = 2 }
        };

        var result = generator.Generate(details);
        result.Should().Be("鉄道（博多～天神、天神～博多）");
    }

    [Fact]
    public void Configure_乗継統合を無効にすると個別表示される()
    {
        var options = new OrganizationOptions();
        options.SummaryRules.EnableTransferConsolidation = false;

        SummaryGenerator.Configure(options);

        var generator = new SummaryGenerator();
        var details = new List<LedgerDetail>
        {
            new() { EntryStation = "中洲川端", ExitStation = "箱崎宮前", IsBus = false, Amount = 260, Balance = 740, SequenceNumber = 1 },
            new() { EntryStation = "天神", ExitStation = "中洲川端", IsBus = false, Amount = 210, Balance = 1000, SequenceNumber = 2 }
        };

        var result = generator.Generate(details);
        result.Should().Be("鉄道（天神～中洲川端、中洲川端～箱崎宮前）");
    }

    [Fact]
    public void Configure_乗り継ぎ駅グループをカスタマイズできる()
    {
        var options = new OrganizationOptions();
        options.SummaryRules.TransferStationGroups = new List<List<string>>
        {
            new List<string> { "梅田", "大阪梅田" },
            new List<string> { "難波", "なんば" }
        };

        SummaryGenerator.Configure(options);

        var generator = new SummaryGenerator();
        // 梅田/大阪梅田グループで乗り継ぎ統合されることを確認
        var details = new List<LedgerDetail>
        {
            new() { EntryStation = "大阪梅田", ExitStation = "難波", IsBus = false, Amount = 260, Balance = 740, SequenceNumber = 1 },
            new() { EntryStation = "三宮", ExitStation = "梅田", IsBus = false, Amount = 400, Balance = 1000, SequenceNumber = 2 }
        };

        var result = generator.Generate(details);
        // 梅田と大阪梅田が同一視されるので統合される
        result.Should().Be("鉄道（三宮～難波）");
    }

    [Fact]
    public void Configure_ラベルをカスタマイズすると摘要に反映される()
    {
        var options = new OrganizationOptions();
        options.SummaryText.RailwayLabel = "電車";
        options.SummaryText.BusLabel = "路線バス";

        SummaryGenerator.Configure(options);

        var generator = new SummaryGenerator();
        var details = new List<LedgerDetail>
        {
            new() { EntryStation = "博多", ExitStation = "天神", IsBus = false, Amount = 260, Balance = 740, SequenceNumber = 1 }
        };

        var result = generator.Generate(details);
        result.Should().Be("電車（博多～天神）");
    }

    [Fact]
    public void ResetToDefaults_デフォルト値に戻る()
    {
        var options = new OrganizationOptions();
        options.SummaryText.ChargeSummaryMayorOffice = "カスタム";
        SummaryGenerator.Configure(options);

        SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice).Should().Be("カスタム");

        SummaryGenerator.ResetToDefaults();

        SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice).Should().Be("役務費によりチャージ");
    }

    #endregion
}
