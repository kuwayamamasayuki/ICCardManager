#nullable enable
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2328: 配布する appsettings.json の Logging セクションに、どこからも読まれない項目を置かないこと。
/// </summary>
/// <remarks>
/// <c>Logging:Debug:Enabled</c> は IT担当者ガイドで「デバッグ出力の有効/無効」と案内されていたが、
/// <c>Logging:Debug</c> は DebugLoggerProvider の別名で <c>Enabled</c> というオプションを持たず、
/// <c>builder.AddDebug()</c> は無条件に呼ばれていた（値を変えても何も変わらない設定）。
/// 対の表明として、実際に読まれている File の設定（<c>FileLoggerOptions.Enabled</c>）が同じファイルから
/// 読めることを置く（読み込みに失敗して全キーが null になった状態でも緑にならないため）。
/// </remarks>
public class AppSettingsLoggingSectionTests
{
    [Fact]
    public void 配布するappsettingsにLogging_Debug_Enabledを置かないこと()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestPaths.GetProductionSourceRoot(), "appsettings.json"), optional: false)
            .Build();

        configuration["Logging:File:Enabled"].Should().NotBeNull("前提: 同じファイルの読まれている項目は読める");
        configuration.GetSection("Logging:Debug").Exists().Should().BeFalse(
            "Logging:Debug:Enabled はどこからも読まれず、変えてもデバッグ出力は止まらない");
    }
}
