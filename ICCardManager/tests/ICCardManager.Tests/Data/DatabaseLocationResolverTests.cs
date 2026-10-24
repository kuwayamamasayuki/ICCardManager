#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Infrastructure.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #2328: DB 接続が開くパスと、共有モードのキャッシュ TTL 短縮の判定に渡すパスが、
/// 同じ解決結果であること。
/// </summary>
/// <remarks>
/// 旧実装は判定のメソッド（<c>DbContext.IsSharedModePath</c>）だけを共有し、入力を別々に読んでいた
/// （接続＝database_config.txt または appsettings.json、TTL＝database_config.txt のみ）。
/// そのため appsettings.json で共有フォルダーを指すと、接続は共有モードなのに TTL が短縮されなかった。
/// 本番と同じ登録（<see cref="DatabaseLocationResolver.Register"/>）を通して、両方の Options を表明する。
/// </remarks>
public class DatabaseLocationResolverTests
{
    private const string UncPath = @"\\server\share\ICCardManager\iccard.db";
    private const string LocalPath = @"C:\data\iccard.db";
    private const string RelativePath = @"data\iccard.db";

    [Fact]
    public void Register_appsettingsだけで共有フォルダーを指すと接続もTTLも共有モードになること()
    {
        var (db, cache) = BuildOptions(configFile: null, appSettingsPath: UncPath);

        db.Path.Should().Be(UncPath, "DbContext は appsettings.json の値を開く");
        cache.CardListSeconds.Should().Be(SharedModeCacheOptions().CardListSeconds,
            "接続が共有モードなら、TTL も共有モードの値へ短縮する（カード系の最長 TTL＝ヘルスチェック間隔 15 秒）");
        cache.LentCardsSeconds.Should().Be(SharedModeCacheOptions().LentCardsSeconds);
    }

    [Fact]
    public void Register_database_config_txtで共有フォルダーを指すと接続もTTLも共有モードになること()
    {
        var (db, cache) = BuildOptions(configFile: UncPath, appSettingsPath: "");

        db.Path.Should().Be(UncPath);
        cache.CardListSeconds.Should().Be(SharedModeCacheOptions().CardListSeconds, "従来の経路（#1597）も短縮される");
    }

    [Fact]
    public void Register_database_config_txtがappsettingsより優先され_TTLもそのパスで判定すること()
    {
        // appsettings.json は共有フォルダー、database_config.txt はローカル → 開くのはローカルなので短縮しない
        var (db, cache) = BuildOptions(configFile: LocalPath, appSettingsPath: UncPath);

        db.Path.Should().Be(LocalPath, "database_config.txt が優先される");
        cache.CardListSeconds.Should().Be(new CacheOptions().CardListSeconds,
            "TTL の判定も実際に開くパス（ローカル）で行い、使われない appsettings.json の値を見ない");
    }

    [Fact]
    public void Register_どちらも空なら既定のローカルDBでTTLを短縮しないこと()
    {
        var (db, cache) = BuildOptions(configFile: null, appSettingsPath: "");

        db.Path.Should().BeEmpty("空は既定のローカル DB");
        cache.CardListSeconds.Should().Be(new CacheOptions().CardListSeconds);
    }

    [Fact]
    public void Register_appsettingsの値が形式不正なら使わず既定のDBで起動すること()
    {
        ResolvedDatabasePath? resolved = null;
        var (db, _) = BuildOptions(configFile: null, appSettingsPath: RelativePath, r => resolved = r);

        db.Path.Should().BeEmpty("形式不正の appsettings.json の値をそのまま DbContext へ渡さない（#1599 と同じ扱い）");
        resolved.Should().NotBeNull();
        resolved!.Rejected.Should().ContainSingle()
            .Which.Should().Match<RejectedDatabasePath>(r =>
                r.Source == DatabaseLocationResolver.AppSettingsSource && r.Value == RelativePath);
    }

    [Fact]
    public void Resolve_database_config_txtが形式不正ならappsettingsの値で起動し_両方を区別して記録すること()
    {
        var result = DatabaseLocationResolver.Resolve(RelativePath, LocalPath);

        result.Path.Should().Be(LocalPath);
        result.Rejected.Should().ContainSingle().Which.Source.Should().Be(DatabaseLocationResolver.ConfigFileSource);
    }

    [Fact]
    public void Resolve_形式が正しい値は棄却を記録しないこと()
    {
        var result = DatabaseLocationResolver.Resolve(UncPath, LocalPath);

        result.Path.Should().Be(UncPath);
        result.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void Register_解決はOptionsを何度読んでも1度だけ行うこと()
    {
        var calls = 0;
        var services = new ServiceCollection();
        DatabaseLocationResolver.Register(services, BuildConfiguration(""), () =>
        {
            calls++;
            return UncPath;
        }, onResolved: null);
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        calls.Should().Be(1, "接続と TTL で別々に読み直すと、読んだ時点の違いで食い違い得る");
    }

    [Fact]
    public void BuildRejectedWarningMessage_棄却した設定ファイルと実際に使った保存先を示すこと()
    {
        var viaAppSettings = DatabaseLocationResolver.Resolve(RelativePath, LocalPath);
        var viaDefault = DatabaseLocationResolver.Resolve(RelativePath, null);

        DatabaseLocationResolver.BuildRejectedWarningMessage(viaAppSettings)
            .Should().Contain(DatabaseLocationResolver.ConfigFileSource)
            .And.Contain(RelativePath)
            .And.Contain($"保存先「{LocalPath}」で起動しました", "既定ではない保存先で起動したことを偽らない")
            .And.EndWith("してください。");
        DatabaseLocationResolver.BuildRejectedWarningMessage(viaDefault)
            .Should().Contain("既定の保存先で起動しました");
    }

    [Fact]
    public void AppXaml_DBパスの解決をDatabasePathResolverへ委ね_TTL判定を自前で書かないこと()
    {
        // 対の表明: 委譲していること／App.xaml.cs が database_config.txt を自前で読んで共有モードを判定し直していないこと
        var code = TestSourceInspection.ToCodeOnly(
            File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs")));

        code.Should().Contain("DatabaseLocationResolver.Register(");
        code.Should().NotContain("IsSharedModePath(", "共有モードの TTL 判定は DatabaseLocationResolver が実際に開くパスで行う");
        code.Should().NotContain("PostConfigure<CacheOptions>");
    }

    private static CacheOptions SharedModeCacheOptions()
    {
        var options = new CacheOptions();
        options.ApplySharedModeTtl();
        return options;
    }

    private static IConfiguration BuildConfiguration(string appSettingsPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DatabaseOptions:Path"] = appSettingsPath })
            .Build();

    private static (DatabaseOptions Db, CacheOptions Cache) BuildOptions(
        string? configFile, string appSettingsPath, Action<ResolvedDatabasePath>? onResolved = null)
    {
        var services = new ServiceCollection();
        services.Configure<CacheOptions>(_ => { });
        DatabaseLocationResolver.Register(services, BuildConfiguration(appSettingsPath), () => configFile, onResolved);
        using var provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<IOptions<DatabaseOptions>>().Value,
            provider.GetRequiredService<IOptions<CacheOptions>>().Value);
    }
}
