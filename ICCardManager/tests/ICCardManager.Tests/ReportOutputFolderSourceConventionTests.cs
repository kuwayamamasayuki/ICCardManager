using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2266: 帳票の出力先を共有 DB の値から読まないことの静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 帳票の出力先は共有 DB の <c>settings.report_output_folder</c>（<c>AppSettings.ReportOutputFolder</c>）から
/// 帳票作成画面の既定値として採用され、画面を開いた直後の出力状況の自動走査と管理者ダッシュボードの走査に
/// 使われていた。共有フォルダーモードでは共有 DB を全 PC が書けるため、値を UNC（<c>\\別ホスト\share</c>）へ
/// 書き換えられると、画面を開いただけでその PC から SMB 接続が起き Windows の認証が走る。
/// 出力先はこの PC の設定ファイル（<c>IReportOutputFolderStore</c>）から読む形へ改めた。
/// </para>
/// <para>
/// 共有 DB の値は旧版との互換のため <c>AppSettings</c> と <c>SettingsRepository</c> に残っているので、
/// <b>それ以外の場所から参照される形</b>を検出する（参照が再び現れれば、同じ経路が開き直る）。
/// 走査対象は本番ソース全体から導出する（ファイル名で列挙すると、新しい消費側が静かに漏れる。#1786）。
/// 対の表明として、帳票作成画面と管理者ダッシュボードが実際にこの PC の設定から読んでいることを置く —
/// 不在の表明だけだと、出力先を読む処理ごと消した実装でも緑になる。
/// </para>
/// </remarks>
public class ReportOutputFolderSourceConventionTests
{
    /// <summary>共有 DB の値を参照してよいファイル（設定の入出力そのもの）</summary>
    private static readonly string[] AllowedRelativePaths =
    {
        "Models/AppSettings.cs",
        "Data/Repositories/SettingsRepository.cs",
    };

    /// <summary>
    /// 共有 DB の出力先を指す識別子。<c>IReportOutputFolderStore</c> のような別の識別子の一部には一致しない
    /// （語境界で区切る）。
    /// </summary>
    private static readonly Regex SharedDbReferencePattern = new Regex(
        @"\bReportOutputFolder\b|\bKeyReportOutputFolder\b", RegexOptions.Compiled);

    /// <summary>
    /// コメント・文字列リテラルを除いたソースから、共有 DB の出力先を参照している箇所を返す。
    /// </summary>
    internal static IReadOnlyList<string> FindSharedDbReferences(string codeOnlySource)
        => SharedDbReferencePattern.Matches(codeOnlySource).Cast<Match>().Select(m => m.Value).ToList();

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

    [Fact]
    public void 設定の入出力以外は共有DBの帳票出力先を参照しないこと()
    {
        var violations = ProductionSourceFiles.CSharp
            .Where(f => !AllowedRelativePaths.Contains(Normalize(f.RelativePath)))
            .Where(f => FindSharedDbReferences(f.CodeOnly).Count > 0)
            .Select(f => Normalize(f.RelativePath))
            .ToList();

        violations.Should().BeEmpty(
            "帳票の出力先はこの PC の設定（IReportOutputFolderStore）から読む。共有 DB の値を既定値や走査に使うと、" +
            "DB を書き換えた者が指定したフォルダー（UNC なら別ホスト）へ画面を開いただけで接続する（Issue #2266）");
    }

    [Fact]
    public void 帳票作成画面と管理者ダッシュボードはこのPCの設定から出力先を読むこと()
    {
        var byPath = ProductionSourceFiles.CSharp.ToDictionary(f => Normalize(f.RelativePath), f => f.CodeOnly);

        byPath.Should().ContainKey("ViewModels/ReportViewModel.cs");
        byPath["ViewModels/ReportViewModel.cs"].Should().MatchRegex(@"_outputFolderStore\s*\.\s*Load\s*\(\s*\)",
            "帳票作成画面の既定値はこの PC の設定から読むこと");

        byPath.Should().ContainKey("Services/AdminDashboardService.cs");
        byPath["Services/AdminDashboardService.cs"].Should().MatchRegex(@"_reportOutputFolderStore\s*\.\s*Load\s*\(\s*\)",
            "管理者ダッシュボードが走査する出力先はこの PC の設定から読むこと");

        // 互換のため残した側が検査の対象外になっていること自体も固定する（許可したファイルが実在しないと、
        // 許可リストが空振りしていても気付けない）
        foreach (var allowed in AllowedRelativePaths)
        {
            byPath.Should().ContainKey(allowed);
            FindSharedDbReferences(byPath[allowed]).Should().NotBeEmpty(
                $"{allowed} は互換のため共有 DB の値を読み書きしている（許可リストの根拠）");
        }
    }

    [Theory]
    [InlineData("var folder = settings.ReportOutputFolder;", 1)]
    [InlineData("await repo.SetAsync(SettingsRepository.KeyReportOutputFolder, value);", 1)]
    [InlineData("new AppSettings { ReportOutputFolder = x }", 1)]
    [InlineData("private readonly IReportOutputFolderStore _reportOutputFolderStore;", 0)]
    [InlineData("var folder = _outputFolderStore.Load();", 0)]
    public void FindSharedDbReferences_共有DBの出力先の参照だけを拾うこと(string code, int expected)
    {
        FindSharedDbReferences(code).Should().HaveCount(expected);
    }
}
