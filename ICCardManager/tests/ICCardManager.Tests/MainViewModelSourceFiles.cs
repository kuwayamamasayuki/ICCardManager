using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ICCardManager.Tests;

/// <summary>
/// <c>MainViewModel</c> を構成する partial ファイル群（Issue #2158）。
/// </summary>
/// <remarks>
/// <para>
/// <c>MainViewModel</c> は本体 <c>ViewModels/MainViewModel.cs</c> と <c>ViewModels/Main/MainViewModel.*.cs</c> へ
/// 責務ごとに分割されている。分割前は静的検査が <c>MainViewModel.cs</c> を<b>ファイル名で</b>読んでいたため、
/// メソッドを別の partial ファイルへ移すだけで検査対象から外れ、<b>緑のまま無力化</b>する
/// （#1786「ガードを書くときは経路を列挙する」）。
/// </para>
/// <para>
/// 構成ファイルは<b>ファイル名ではなく宣言から導出する</b>。本番ソースのうち
/// <c>partial class MainViewModel</c> を宣言しているファイルがすべて対象になるため、
/// partial ファイルを足した日・別のフォルダーへ置いた日にも検査から漏れない。
/// </para>
/// <para>
/// メソッド単位の検査は <see cref="FindDeclaringFile"/> で「そのメソッドを宣言しているファイル」を引く。
/// 宣言がどのファイルへ移っても追随し、見つからなければ（リネーム・削除）赤になる。
/// </para>
/// </remarks>
internal static class MainViewModelSourceFiles
{
    /// <summary><c>partial class MainViewModel</c> の宣言（コメント・文字列を除いた本文で照合する）。</summary>
    internal static readonly Regex PartialClassDeclarationPattern =
        new Regex(@"\bpartial\s+class\s+MainViewModel\b", RegexOptions.Compiled);

    /// <summary>
    /// <c>MainViewModel</c> を構成する本番ソース（相対パスの順）。
    /// </summary>
    public static IReadOnlyList<ProductionSourceFiles.SourceFile> All
        => ProductionSourceFiles.CSharp
            .Where(f => PartialClassDeclarationPattern.IsMatch(f.CodeOnly))
            .ToList();

    /// <summary>
    /// 構成ファイルの <see cref="ProductionSourceFiles.SourceFile.CodeOnly"/> を連結した本文。
    /// 呼び出し関係を辿る検査（メソッド本体がどのファイルにあってもよいもの）で使う。
    /// </summary>
    public static string ConcatenatedCodeOnly
        => string.Join(Environment.NewLine, All.Select(f => f.CodeOnly));

    /// <summary>
    /// <paramref name="declarationFragment"/>（例: <c>"public void Exit()"</c>）を含む構成ファイルを 1 つ返す。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 該当が 0 件または 2 件以上のとき。検査の前提（宣言が 1 か所にあること）が崩れているので、
    /// 黙って空振りさせず赤にする。
    /// </exception>
    public static ProductionSourceFiles.SourceFile FindDeclaringFile(string declarationFragment)
    {
        var matches = All.Where(f => f.CodeOnly.Contains(declarationFragment)).ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"MainViewModel の構成ファイルで「{declarationFragment}」を含むものが {matches.Count} 件だった（1 件を期待）: " +
                string.Join(", ", matches.Select(f => f.RelativePath)));
        }

        return matches[0];
    }
}

/// <summary>
/// 履歴パネル <c>HistoryPanelViewModel</c> を構成するファイル群（Issue #2159）。
/// </summary>
/// <remarks>
/// 履歴パネルは <c>MainViewModel</c> の partial ファイルから子の ViewModel へ抽出された。抽出前は
/// <see cref="MainViewModelSourceFiles"/> の走査範囲に入っていたため、<c>MainViewModel</c> を読む検査のうち
/// 履歴パネルの記述も見るべきもの（トースト位置の断定表現・撮影対応表）は、こちらも併せて走査する。
/// 構成ファイルは <see cref="MainViewModelSourceFiles"/> と同じく宣言から導出する。
/// </remarks>
internal static class HistoryPanelViewModelSourceFiles
{
    /// <summary><c>partial class HistoryPanelViewModel</c> の宣言（コメント・文字列を除いた本文で照合する）。</summary>
    internal static readonly Regex PartialClassDeclarationPattern =
        new Regex(@"\bpartial\s+class\s+HistoryPanelViewModel\b", RegexOptions.Compiled);

    /// <summary><c>HistoryPanelViewModel</c> を構成する本番ソース（相対パスの順）。</summary>
    public static IReadOnlyList<ProductionSourceFiles.SourceFile> All
        => ProductionSourceFiles.CSharp
            .Where(f => PartialClassDeclarationPattern.IsMatch(f.CodeOnly))
            .ToList();
}
