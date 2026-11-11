#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Issue #2389 で appsettings.json の DatabaseOptions:Path は廃止し、入力は database_config.txt だけになった
/// （旧版の値の移行は <c>LegacyDatabasePathMigrationTests</c>）。
/// </remarks>
public class DatabaseLocationResolverTests
{
    private const string UncPath = @"\\server\share\ICCardManager\iccard.db";
    private const string LocalPath = @"C:\data\iccard.db";
    private const string RelativePath = @"data\iccard.db";

    [Fact]
    public void Register_database_config_txtで共有フォルダーを指すと接続もTTLも共有モードになること()
    {
        var (db, cache) = BuildOptions(configFile: UncPath);

        db.Path.Should().Be(UncPath);
        cache.CardListSeconds.Should().Be(SharedModeCacheOptions().CardListSeconds,
            "接続が共有モードなら、TTL も共有モードの値へ短縮する（カード系の最長 TTL＝ヘルスチェック間隔 15 秒）");
        cache.LentCardsSeconds.Should().Be(SharedModeCacheOptions().LentCardsSeconds);
    }

    [Fact]
    public void Register_database_config_txtがローカルならTTLを短縮しないこと()
    {
        var (db, cache) = BuildOptions(configFile: LocalPath);

        db.Path.Should().Be(LocalPath);
        cache.CardListSeconds.Should().Be(new CacheOptions().CardListSeconds, "TTL の判定も実際に開くパス（ローカル）で行う");
    }

    [Fact]
    public void Register_database_config_txtが空なら既定のローカルDBでTTLを短縮しないこと()
    {
        var (db, cache) = BuildOptions(configFile: null);

        db.Path.Should().BeEmpty("空は既定のローカル DB");
        cache.CardListSeconds.Should().Be(new CacheOptions().CardListSeconds);
    }

    [Fact]
    public void Register_database_config_txtの値が形式不正なら使わず既定のDBで起動すること()
    {
        ResolvedDatabasePath? resolved = null;
        var (db, _) = BuildOptions(configFile: RelativePath, r => resolved = r);

        db.Path.Should().BeEmpty("形式不正の値をそのまま DbContext へ渡さない（#1599）");
        resolved.Should().NotBeNull();
        resolved!.Rejected.Should().ContainSingle()
            .Which.Should().Match<RejectedDatabasePath>(r =>
                r.Source == DatabaseLocationResolver.ConfigFileSource && r.Value == RelativePath);
    }

    [Fact]
    public void Resolve_形式が正しい値は棄却を記録しないこと()
    {
        var result = DatabaseLocationResolver.Resolve(UncPath);

        result.Path.Should().Be(UncPath);
        result.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void Register_解決はOptionsを何度読んでも1度だけ行うこと()
    {
        var calls = 0;
        var services = new ServiceCollection();
        DatabaseLocationResolver.Register(services, () =>
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
    public void BuildRejectedWarningMessage_棄却した設定ファイルと既定の保存先で起動したことを示すこと()
    {
        var resolution = DatabaseLocationResolver.Resolve(RelativePath);

        DatabaseLocationResolver.BuildRejectedWarningMessage(resolution)
            .Should().Contain(DatabaseLocationResolver.ConfigFileSource)
            .And.Contain(RelativePath)
            .And.Contain("既定の保存先で起動しました")
            .And.EndWith("してください。");
    }

    [Fact]
    public void AppXaml_DBパスの解決をDatabaseLocationResolverへ委ね_TTL判定を自前で書かないこと()
    {
        // 対の表明: 委譲していること／App.xaml.cs が database_config.txt を自前で読んで共有モードを判定し直していないこと
        var code = TestSourceInspection.ToCodeOnly(
            File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs")));

        code.Should().Contain("DatabaseLocationResolver.Register(");
        code.Should().NotContain("IsSharedModePath(", "共有モードの TTL 判定は DatabaseLocationResolver が実際に開くパスで行う");
        code.Should().NotContain("PostConfigure<CacheOptions>");
        code.Should().Contain("DatabaseLocationResolver.DescribePathForStartupError(",
            "起動失敗のダイアログも、接続と同じ解決結果のパスを表示する");
    }

    /// <summary>
    /// Issue #2389: appsettings.json の DatabaseOptions:Path は廃止し、起動時の移行（LegacyDatabasePathMigration）でだけ読む。
    /// </summary>
    /// <remarks>
    /// 対の表明: 起動処理が移行を経由していること／移行以外のファイルが appsettings.json の DatabaseOptions を読んでいないこと
    /// （直接読む経路が残ると、設定画面の欄・「デフォルトに戻す」と実際に開く DB の食い違いが戻る）。
    /// 走査対象は本体のソース全体から導出する。キーは文字列リテラルなので、コメントだけを除いてリテラルを残した形で照合する
    /// （「DatabaseOptions:Path を廃止した」と説明するコメントを違反と数えない）。
    /// </remarks>
    [Fact]
    public void appsettingsのDBパスは移行を通してだけ読むこと()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var app = TestSourceInspection.ToCodeOnly(File.ReadAllText(Path.Combine(root, "App.xaml.cs")));
        app.Should().Contain("LegacyDatabasePathMigration.Run(");
        app.Should().Contain("Configuration[LegacyDatabasePathMigration.AppSettingsKey]");

        var readers = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(f)).Contains("\"DatabaseOptions"))
            .Select(Path.GetFileName)
            .ToList();
        readers.Should().Equal(new[] { "LegacyDatabasePathMigration.cs" },
            "appsettings.json の DatabaseOptions を読むのは移行だけ（キーの定義もそこにある）");

        var code = File.ReadAllText(Path.Combine(root, "Data", "DatabaseLocationResolver.cs"));
        TestSourceInspection.ToCodeOnly(code).Should().NotContain("IConfiguration",
            "解決は appsettings.json を受け取らない（database_config.txt だけで決める）");
    }

    [Fact]
    public void 配布するappsettingsにDatabaseOptionsを置かないこと()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestPaths.GetProductionSourceRoot(), "appsettings.json"), optional: false)
            .Build();

        configuration["Logging:File:Enabled"].Should().NotBeNull("前提: 同じファイルの読まれている項目は読める");
        configuration.GetSection("DatabaseOptions").Exists().Should().BeFalse(
            "廃止した項目を配布物に残すと、IT 担当者が保存先をそこへ書けると誤解する");
    }

    /// <summary>
    /// 起動失敗のダイアログに添えるパスは、接続と同じ解決結果であること（Issue #2328 のレビューで検出）。
    /// </summary>
    /// <remarks>
    /// 旧実装は database_config.txt を直接表示しており、形式不正で棄却された構成では実際には開いていないパスを示していた。
    /// </remarks>
    [Theory]
    [InlineData(UncPath, UncPath)]
    [InlineData(LocalPath, LocalPath)]
    public void DescribePathForStartupError_接続と同じ解決結果のパスを示すこと(string configFile, string expected)
    {
        DatabaseLocationResolver.DescribePathForStartupError(configFile).Should().Be(expected);
    }

    [Theory]
    [InlineData(RelativePath)]
    [InlineData(null)]
    public void DescribePathForStartupError_既定のときは既定のパスを示すこと(string? configFile)
    {
        var description = DatabaseLocationResolver.DescribePathForStartupError(configFile);

        description.Should().Contain(DbContext.DatabaseFileName).And.EndWith("（既定）",
            "空欄だと何を開こうとしたか分からない。棄却した値も示さない");
        description.Should().NotContain(RelativePath);
    }

    private static CacheOptions SharedModeCacheOptions()
    {
        var options = new CacheOptions();
        options.ApplySharedModeTtl();
        return options;
    }

    private static (DatabaseOptions Db, CacheOptions Cache) BuildOptions(
        string? configFile, Action<ResolvedDatabasePath>? onResolved = null)
    {
        var services = new ServiceCollection();
        services.Configure<CacheOptions>(_ => { });
        DatabaseLocationResolver.Register(services, () => configFile, onResolved);
        using var provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<IOptions<DatabaseOptions>>().Value,
            provider.GetRequiredService<IOptions<CacheOptions>>().Value);
    }
}
