using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ICCardManager.Tests;

/// <summary>
/// 本番ソース（<c>src/ICCardManager</c> 配下の <c>.cs</c> / <c>.xaml</c>）の読み込み結果を、
/// テストプロセス全体で 1 回だけ作って共有するキャッシュ（Issue #2108）。
/// </summary>
/// <remarks>
/// <para>
/// 静的検査（規約テスト）はそれぞれが本番ソース全体（約 4MB）を読み込み・サニタイズし直していた。
/// Theory の各ケースで読み直すもの（<c>OrganizationOptionsUsageConventionTests</c> は 36 ケース）、
/// ファイルごとに親方向の探索を繰り返すもの（<c>DomainBoundaryConventionTests</c>）もあった。
/// 読み込みとサニタイズは入力が同じなら結果も同じなので、ここで 1 回だけ行う。
/// </para>
/// <para>
/// <b>ビルド出力（<c>bin</c> / <c>obj</c>）のディレクトリには降りない</b>。開発機の <c>obj</c> には
/// WPF の一時プロジェクト（<c>*_wpftmp</c>）の残骸が千件単位で溜まることがあり、列挙してから
/// パスで捨てる形では走査そのものが重くなる。XAML から生成された <c>.g.cs</c> / <c>.g.i.cs</c> も
/// <c>obj</c> 配下にしか無いので、同時に除外される（<see cref="IsBuildOutputDirectoryName"/>）。
/// </para>
/// <para>
/// サニタイズ済みの形（<see cref="SourceFile.CodeOnly"/> 等）は、使われたときに 1 回だけ作る。
/// 変換は <see cref="TestSourceInspection"/> の同名メソッドそのものであり、ここで別の実装を持たない。
/// </para>
/// <para>
/// <see cref="LazyThreadSafetyMode.PublicationOnly"/> を使うのは、例外をキャッシュしないため。
/// 初回の読み込みで一過性の <see cref="IOException"/>（同期中のロック等）が起きたとき、プロセス内の
/// すべての規約テストが同じ例外で赤くなるのを避け、次に読んだテストが読み直せるようにする。
/// </para>
/// </remarks>
internal static class ProductionSourceFiles
{
    private static readonly Lazy<string> RootLazy =
        new Lazy<string>(TestPaths.GetProductionSourceRoot, LazyThreadSafetyMode.PublicationOnly);

    private static readonly Lazy<IReadOnlyList<SourceFile>> CSharpLazy =
        new Lazy<IReadOnlyList<SourceFile>>(() => Load("*.cs"), LazyThreadSafetyMode.PublicationOnly);

    private static readonly Lazy<IReadOnlyList<SourceFile>> XamlLazy =
        new Lazy<IReadOnlyList<SourceFile>>(() => Load("*.xaml"), LazyThreadSafetyMode.PublicationOnly);

    /// <summary>本番ソースのルート（<see cref="TestPaths.GetProductionSourceRoot"/>）。</summary>
    public static string Root => RootLazy.Value;

    /// <summary>本番の <c>.cs</c> ファイル（ビルド出力を除く）。相対パスの順。</summary>
    public static IReadOnlyList<SourceFile> CSharp => CSharpLazy.Value;

    /// <summary>本番の <c>.xaml</c> ファイル（ビルド出力を除く）。相対パスの順。</summary>
    public static IReadOnlyList<SourceFile> Xaml => XamlLazy.Value;

    /// <summary>
    /// <paramref name="files"/> のうち、ルートからの相対ディレクトリ <paramref name="relativeDirectory"/>
    /// の配下（入れ子を含む）にあるものを返す。区切り文字は <c>/</c> と <c>\</c> のどちらでもよい。
    /// </summary>
    public static IEnumerable<SourceFile> Under(this IEnumerable<SourceFile> files, string relativeDirectory)
    {
        var prefix = NormalizeSeparators(relativeDirectory).TrimEnd(Path.DirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return files.Where(f => f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 走査で降りないディレクトリ名（ビルド出力）かどうか。大文字小文字は区別しない。
    /// </summary>
    internal static bool IsBuildOutputDirectoryName(string directoryName)
        => string.Equals(directoryName, "bin", StringComparison.OrdinalIgnoreCase)
           || string.Equals(directoryName, "obj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="root"/> 配下で <paramref name="pattern"/> に一致するファイルを、
    /// ビルド出力のディレクトリへ降りずに列挙する。
    /// </summary>
    internal static IEnumerable<string> EnumerateSourcePaths(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IsBuildOutputDirectoryName(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static IReadOnlyList<SourceFile> Load(string pattern)
    {
        var root = Root;
        return EnumerateSourcePaths(root, pattern)
            .Select(path => new SourceFile(
                path,
                path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                File.ReadAllText(path)))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeSeparators(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    /// <summary>読み込み済みの本番ソース 1 ファイル。</summary>
    internal sealed class SourceFile
    {
        private readonly Lazy<string> _commentsRemovedPreservingLines;
        private readonly Lazy<string> _codeOnly;
        private readonly Lazy<string> _codeOnlyPreservingLines;

        public SourceFile(string fullPath, string relativePath, string text)
        {
            FullPath = fullPath;
            RelativePath = relativePath;
            Text = text;
            _commentsRemovedPreservingLines = new Lazy<string>(
                () => TestSourceInspection.RemoveCommentsPreservingLines(Text),
                LazyThreadSafetyMode.PublicationOnly);
            _codeOnly = new Lazy<string>(
                () => TestSourceInspection.ToCodeOnly(Text),
                LazyThreadSafetyMode.PublicationOnly);
            _codeOnlyPreservingLines = new Lazy<string>(
                () => TestSourceInspection.ToCodeOnlyPreservingLines(Text),
                LazyThreadSafetyMode.PublicationOnly);
        }

        /// <summary>絶対パス。</summary>
        public string FullPath { get; }

        /// <summary>本番ソースのルートからの相対パス（OS の区切り文字）。</summary>
        public string RelativePath { get; }

        /// <summary>ファイル名。</summary>
        public string Name => Path.GetFileName(FullPath);

        /// <summary>読み込んだままのテキスト。</summary>
        public string Text { get; }

        /// <summary><see cref="TestSourceInspection.RemoveCommentsPreservingLines"/> を通した形。</summary>
        public string CommentsRemovedPreservingLines => _commentsRemovedPreservingLines.Value;

        /// <summary><see cref="TestSourceInspection.ToCodeOnly"/> を通した形。</summary>
        public string CodeOnly => _codeOnly.Value;

        /// <summary><see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/>（既定の引数）を通した形。</summary>
        public string CodeOnlyPreservingLines => _codeOnlyPreservingLines.Value;

        public override string ToString() => RelativePath;
    }
}
