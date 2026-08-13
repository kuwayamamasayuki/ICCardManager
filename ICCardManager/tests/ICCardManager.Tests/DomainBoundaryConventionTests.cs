using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1695: 交通系固有ロジックの境界を固定する規約テスト。
///
/// 本システムのコア（タッチ認証 → 貸出/返却 → 出納簿）は「複数職員でシェアする物品の
/// 出納管理」として交通系ICカード以外にも通用する構造を持つ。今すぐ汎用化はしないが、
/// 交通系固有の知識がコアへ染み出すと将来その選択肢を取り戻すのに大規模改修が必要になる。
///
/// 本テストは <b>既に閉じている境界だけ</b>を固定する（設計書 05 §2a.5）。
/// <see cref="ICCardManager.Services.SummaryGenerator"/> 全体への参照制限はかけない
/// ——汎用メソッド（繰越・月計・累計・貸出中）が 22 ファイルから正当に使われているため。
///
/// テストは禁止ではなく<b>気づきの装置</b>。意図的に境界を変更する場合は、
/// `docs/design/05_クラス設計書.md` §2a.4 の seam カタログを更新したうえで本テストを修正する。
/// </summary>
/// <remarks>
/// 実装方式は <see cref="UserFacingTextConventionTests"/> と同じソースの静的検査。
/// Reflection によるアセンブリ依存解析ではなくテキスト検査を選ぶ理由は、既存の規約テスト群と
/// 方式が揃うこと、違反箇所をファイル名・行番号で指摘できること、そして
/// <b>コンパイル後に消えるコメントを除外できる</b>こと（下記 CommentStripping の項を参照）。
/// </remarks>
public class DomainBoundaryConventionTests
{
    /// <summary>
    /// FeliCa 生データの解釈は交通系固有ロジックであり、Infrastructure/CardReader に閉じる。
    /// </summary>
    private const string FelicaDecoderSymbol = "FelicaHistoryBlockDecoder";

    /// <summary>
    /// 駅コード→駅名の解決は「読取直後の変換」に留め、上位層は駅名を文字列としてのみ扱う。
    /// インターフェース名を先に並べているのは、部分一致で <c>IStationMasterService</c> を
    /// <c>StationMasterService</c> として二重計上しないため（検出は行単位の存在判定なので
    /// 実害はないが、違反メッセージに出る記号名を正確にする）。
    /// </summary>
    private static readonly string[] StationMasterSymbols =
    {
        "IStationMasterService",
        "StationMasterService",
    };

    /// <summary>
    /// 永続化層（Data/）に入ってはいけない交通系ロジックの記号。
    /// </summary>
    /// <remarks>
    /// <b>ここに <c>EntryStation</c> / <c>ExitStation</c> / <c>BusStops</c> / <c>IsBus</c> は含めない。</b>
    /// <see cref="ICCardManager.Models.LedgerDetail"/> の固有列を DB へマッピングするのは
    /// リポジトリの正当な責務であり、境界違反ではない。禁止するのは「データの形」ではなく
    /// 「データの解釈」——摘要生成・駅名解決・バス判別といった<b>判断ロジック</b>が
    /// 永続化層に入ること。
    /// </remarks>
    private static readonly string[] TransitLogicSymbolsForbiddenInDataLayer =
    {
        "SummaryGenerator",
        "IStationMasterService",
        "StationMasterService",
        "FelicaHistoryBlockDecoder",
        "DetermineIsBusUsage",
    };

    /// <summary>
    /// Data 層から参照してよい <c>SummaryGenerator</c> の汎用メソッド呼び出し
    /// （Issue #1749、設計書 05 §2a.4 seam カタログ）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 繰越 LIKE パターン導出（<c>GetMidYearCarryoverLikePattern</c>）は交通系固有の
    /// 「データの解釈」ではなく物品出納簿の様式に属する汎用ロジックであり、
    /// SQL 側の繰越判定を組織設定 <c>MidYearCarryoverFormat</c> へ追従させるために
    /// <c>LedgerRepository</c> がパラメータバインドで参照する。
    /// 記号一致だけでは <c>SummaryGenerator</c> の汎用/固有メソッドを区別できないため、
    /// <b>完全修飾の呼び出し形</b>（開き括弧まで）で限定して許可する。
    /// </para>
    /// <para>
    /// 許可の適用は「行から許可呼び出しを除去してから禁止記号を照合する」方式。
    /// 同一行に固有メソッドの参照が同居していれば従来どおり違反として検出される
    /// （<see cref="RemoveAllowedDataLayerCalls_KnownSamples_BehaveAsPinned"/> で固定）。
    /// </para>
    /// </remarks>
    private static readonly string[] GenericSummaryGeneratorCallsAllowedInDataLayer =
    {
        "SummaryGenerator.GetMidYearCarryoverLikePattern(",
    };

    /// <summary>
    /// Data 層の 1 行から、許可された汎用メソッド呼び出しを除去する。
    /// 除去後に禁止記号が残らなければ、その行は許可された参照だけで構成されている。
    /// </summary>
    private static string RemoveAllowedDataLayerCalls(string line)
    {
        foreach (var allowed in GenericSummaryGeneratorCallsAllowedInDataLayer)
        {
            line = line.Replace(allowed, string.Empty);
        }

        return line;
    }

    /// <summary>
    /// 交通系固有ロジックを参照してよいディレクトリ（ソースルートからの相対パス）。
    /// </summary>
    private const string CardReaderInfrastructureDir = @"Infrastructure\CardReader";

    /// <summary>
    /// DI 登録のためだけに交通系固有サービスへ触れることを許すファイル。
    /// コンポジションルートは全リングを知っている必要があるため、境界の例外として扱う。
    /// </summary>
    private const string CompositionRootFile = "App.xaml.cs";

    [Fact]
    public void FelicaHistoryBlockDecoder_IsReferencedOnlyFromCardReaderInfrastructure()
    {
        var violations = FindReferencesOutside(
            new[] { FelicaDecoderSymbol },
            isAllowed: relativePath => IsUnderCardReaderInfrastructure(relativePath));

        violations.Should().BeEmpty(
            $"{FelicaDecoderSymbol} は FeliCa 生データの解釈という交通系固有ロジックであり、" +
            "Infrastructure/CardReader に閉じる設計です（Issue #1695、設計書 05 §2a.5）。\n" +
            "上位層へは解釈済みの値（駅名文字列・金額）だけを渡してください。\n" +
            "意図的に境界を変更する場合は、docs/design/05_クラス設計書.md §2a.4 の " +
            "seam カタログを更新したうえで本テストを修正してください。\n\n" +
            "違反箇所:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void StationMasterService_IsReferencedOnlyFromCardReaderInfrastructureAndCompositionRoot()
    {
        var violations = FindReferencesOutside(
            StationMasterSymbols,
            isAllowed: relativePath =>
                IsUnderCardReaderInfrastructure(relativePath)
                || IsCompositionRoot(relativePath)
                || IsStationMasterOwnDefinition(relativePath));

        violations.Should().BeEmpty(
            "駅コード→駅名の解決は「カード読取直後の変換」に留める設計です（Issue #1695、設計書 05 §2a.5）。\n" +
            "Services / ViewModels は駅名を<解決済みの文字列>としてのみ扱ってください。\n" +
            "この境界が保たれている限り、交通系以外へ横展開する際は DI 登録を外すだけで済みます。\n" +
            "意図的に境界を変更する場合は、docs/design/05_クラス設計書.md §2a.4 の " +
            "seam カタログを更新したうえで本テストを修正してください。\n\n" +
            "違反箇所:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void DataLayer_DoesNotReferenceTransitSpecificLogic()
    {
        var dataRoot = Path.Combine(GetSourceRoot(), "Data");
        Directory.Exists(dataRoot).Should().BeTrue(
            $"Data ディレクトリが見つからない: {dataRoot}。テストのソースルート解決ロジックを確認してください。");

        var violations = new List<string>();
        foreach (var csPath in EnumerateProductionSourceFiles(dataRoot))
        {
            foreach (var hit in FindSymbolsInCode(
                csPath, TransitLogicSymbolsForbiddenInDataLayer, RemoveAllowedDataLayerCalls))
            {
                violations.Add(hit);
            }
        }

        violations.Should().BeEmpty(
            "永続化層（Data/）が交通系固有の<判断ロジック>を参照しています（Issue #1695、設計書 05 §2a.5）。\n" +
            "LedgerDetail の乗車駅・降車駅・バス停を DB 列へマッピングすることは正当な責務ですが、\n" +
            "摘要生成・駅名解決・バス判別といった解釈は Services / Infrastructure の責務です。\n" +
            "例外として、汎用メソッドの完全修飾呼び出し（GenericSummaryGeneratorCallsAllowedInDataLayer、\n" +
            "現在は繰越 LIKE パターン導出のみ）は許可されています（Issue #1749）。\n" +
            "意図的に境界を変更する場合は、docs/design/05_クラス設計書.md §2a.4 の " +
            "seam カタログを更新したうえで本テストを修正してください。\n\n" +
            "違反箇所:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// 許可リストの適用ロジック自体を既知のサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ（Data/ 配下のソース）だけに依存すると、許可リストの書き換えや
    /// 除去ロジックの退行（例: 前方一致化で <c>GetMidYearCarryoverLikePatternX</c> まで
    /// 許可してしまう）が「違反ゼロ」のまま素通りする。検査ロジックはサンプルで固定する
    /// （`.claude/rules/development-conventions.md`「空振り検出を『各対象が非空であること』で
    /// 書かない」）。
    /// </remarks>
    [Theory]
    // 許可された汎用メソッドの呼び出しだけの行 → 禁止記号は残らない
    [InlineData("SummaryGenerator.GetMidYearCarryoverLikePattern());", false)]
    // 固有メソッドの参照 → 従来どおり検出される
    [InlineData("var s = SummaryGenerator.GetChargeSummary();", true)]
    // 許可呼び出しと固有メソッドが同居する行 → 検出される
    [InlineData("Use(SummaryGenerator.GetMidYearCarryoverLikePattern(), SummaryGenerator.Generate(d));", true)]
    // 許可メソッド名を前方に含む別メソッド → 検出される（前方一致で素通りしないこと）
    [InlineData("SummaryGenerator.GetMidYearCarryoverLikePatternX();", true)]
    // メソッド参照だけでなく型名の単独参照も検出される
    [InlineData("private readonly SummaryGenerator _generator;", true)]
    public void RemoveAllowedDataLayerCalls_KnownSamples_BehaveAsPinned(string line, bool shouldRemainForbidden)
    {
        var sanitized = RemoveAllowedDataLayerCalls(line);

        TransitLogicSymbolsForbiddenInDataLayer.Any(sanitized.Contains)
            .Should().Be(shouldRemainForbidden);
    }

    /// <summary>
    /// 上記 3 テストが「走査対象ゼロで素通り」していないことを表明する。
    /// </summary>
    /// <remarks>
    /// パスの綴り誤りやディレクトリ移動でソースルート解決が壊れると、
    /// <c>EnumerateProductionSourceFiles</c> が空を返して 3 テストすべてが<b>無条件に green</b> になる。
    /// 規約テストの最も危険な壊れ方（`.claude/rules/testing.md`「通るが目的を果たさないテスト」）なので、
    /// 走査対象の実在と、境界内に検査対象記号が<b>実際に存在すること</b>を併せて検証する。
    /// </remarks>
    [Fact]
    public void BoundaryScan_ActuallyCoversProductionSources()
    {
        var sourceRoot = GetSourceRoot();
        var allFiles = EnumerateProductionSourceFiles(sourceRoot).ToList();

        allFiles.Should().HaveCountGreaterThan(100,
            $"src/ICCardManager 配下の .cs ファイルが十分に走査されていない（{allFiles.Count} 件）。" +
            "ソースルート解決または bin/obj 除外ロジックが壊れている可能性があります。");

        // 検査対象の記号が境界内に実在することを確認する。
        // 記号がリネーム・削除されると 3 テストは「違反ゼロ」で green のまま無意味になる。
        var cardReaderRoot = Path.Combine(sourceRoot, "Infrastructure", "CardReader");
        var cardReaderSources = EnumerateProductionSourceFiles(cardReaderRoot)
            .Select(File.ReadAllText)
            .ToList();

        cardReaderSources.Should().NotBeEmpty(
            $"Infrastructure/CardReader に .cs ファイルが見つからない: {cardReaderRoot}");

        cardReaderSources.Any(src => src.Contains(FelicaDecoderSymbol)).Should().BeTrue(
            $"{FelicaDecoderSymbol} が Infrastructure/CardReader 内に見つからない。" +
            "リネーム・削除された場合、境界テストは検査対象を失って無意味に green になります。" +
            "本テストと設計書 05 §2a の記号名を更新してください。");

        cardReaderSources.Any(src => StationMasterSymbols.Any(src.Contains)).Should().BeTrue(
            "IStationMasterService / StationMasterService が Infrastructure/CardReader 内に見つからない。" +
            "リネーム・削除された場合、境界テストは検査対象を失って無意味に green になります。" +
            "本テストと設計書 05 §2a の記号名を更新してください。");
    }

    // ------------------------------------------------------------------
    // 走査ヘルパー
    // ------------------------------------------------------------------

    /// <summary>
    /// ソースルート全体を走査し、<paramref name="isAllowed"/> が false を返すファイルの中で
    /// <paramref name="symbols"/> のいずれかを<b>実コードとして</b>参照している箇所を列挙する。
    /// </summary>
    private static IReadOnlyList<string> FindReferencesOutside(
        IReadOnlyList<string> symbols,
        Func<string, bool> isAllowed)
    {
        var sourceRoot = GetSourceRoot();
        var violations = new List<string>();

        foreach (var csPath in EnumerateProductionSourceFiles(sourceRoot))
        {
            var relativePath = MakeRelativeToSourceRoot(csPath);
            if (isAllowed(relativePath))
            {
                continue;
            }

            violations.AddRange(FindSymbolsInCode(csPath, symbols));
        }

        return violations;
    }

    /// <summary>
    /// 1 ファイルから、コメントを除去したうえで対象記号を含む行を列挙する。
    /// </summary>
    /// <param name="sanitizeLine">
    /// 照合前に行へ適用する変換（許可された参照の除去。Issue #1749）。
    /// 違反として報告する行テキストは変換前の原文を使う。
    /// </param>
    private static IEnumerable<string> FindSymbolsInCode(
        string csPath, IReadOnlyList<string> symbols, Func<string, string>? sanitizeLine = null)
    {
        var codeLines = StripComments(File.ReadAllText(csPath)).Split('\n');
        var relativePath = MakeRelativeToSourceRoot(csPath);

        for (int i = 0; i < codeLines.Length; i++)
        {
            var line = codeLines[i];
            var target = sanitizeLine != null ? sanitizeLine(line) : line;
            foreach (var symbol in symbols)
            {
                if (target.Contains(symbol))
                {
                    yield return $"  {relativePath}:{i + 1} [{symbol}] {line.Trim()}";
                    break; // 同一行で複数記号が当たっても 1 件として報告する
                }
            }
        }
    }

    /// <summary>
    /// 本番ソース（bin / obj 配下の生成物を除く .cs ファイル）を列挙する。
    /// </summary>
    private static IEnumerable<string> EnumerateProductionSourceFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOutput(path))
            {
                continue;
            }

            yield return path;
        }
    }

    /// <summary>
    /// ビルド生成物（bin / obj）配下かどうか。
    /// これを除外しないと XAML から生成された .g.cs が走査対象に入り、
    /// コンポジションルートの DI 登録が生成コード側でも重複検出される。
    /// </summary>
    private static bool IsGeneratedOutput(string fullPath)
    {
        var relative = MakeRelativeToSourceRoot(fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s =>
            string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderCardReaderInfrastructure(string relativePath) =>
        relativePath.Replace('/', '\\').StartsWith(CardReaderInfrastructureDir + '\\', StringComparison.OrdinalIgnoreCase);

    private static bool IsCompositionRoot(string relativePath) =>
        string.Equals(relativePath, CompositionRootFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 駅マスターサービス自身の定義ファイル（インターフェースと実装）。
    /// 自分自身の型名を含むのは当然なので許可する。
    /// </summary>
    private static bool IsStationMasterOwnDefinition(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return string.Equals(fileName, "IStationMasterService.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "StationMasterService.cs", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // コメント除去
    // ------------------------------------------------------------------

    /// <summary>
    /// C# ソースから行コメント（<c>//</c> / <c>///</c>）とブロックコメント（<c>/* */</c>）を
    /// 取り除く。文字列リテラル・逐語的文字列・文字リテラル内の <c>//</c> は除去しない。
    /// 行数を保つため、除去した箇所は空白へ置換し改行はそのまま残す。
    /// </summary>
    /// <remarks>
    /// コメントを除外する理由は、規約の<b>理由</b>をコードコメントに残せるようにするため。
    /// 実例として <c>Data/Repositories/LedgerRepository.cs</c> には
    /// 「<c>SummaryGenerator.GetMidYearCarryoverSummary</c> の生成結果で検証している」という
    /// 説明コメントがあり、単純な文字列一致ではこれを違反として誤検出する
    /// （`.claude/rules/development-conventions.md`「禁止された要素の不在を検査するときは
    /// 要素タグで照合する」と同じ落とし穴）。
    /// </remarks>
    internal static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];

            // 逐語的文字列 @"..."（"" でエスケープ）
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                result.Append(c).Append('"');
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            result.Append("\"\"");
                            i += 2;
                            continue;
                        }

                        result.Append('"');
                        i++;
                        break;
                    }

                    result.Append(source[i]);
                    i++;
                }

                continue;
            }

            // 通常の文字列リテラル / 文字リテラル（\ でエスケープ）
            if (c == '"' || c == '\'')
            {
                char quote = c;
                result.Append(c);
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(source[i]).Append(source[i + 1]);
                        i += 2;
                        continue;
                    }

                    result.Append(source[i]);
                    if (source[i] == quote)
                    {
                        i++;
                        break;
                    }

                    // 未終端の文字列が行をまたぐことはないので改行で打ち切る
                    if (source[i] == '\n')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            // 行コメント
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // ブロックコメント（改行は保持して行番号を狂わせない）
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                    {
                        result.Append('\n');
                    }

                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    // ------------------------------------------------------------------
    // パス解決（UserFacingTextConventionTests と同じ方式）
    // ------------------------------------------------------------------

    private static string GetSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICCardManager.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException(
                $"ICCardManager.sln が AppContext.BaseDirectory ({AppContext.BaseDirectory}) から見つからない。" +
                "テスト実行ディレクトリの構造を確認してください。");
        }

        return Path.Combine(dir.FullName, "src", "ICCardManager");
    }

    private static string MakeRelativeToSourceRoot(string fullPath)
    {
        var root = GetSourceRoot();
        return fullPath.StartsWith(root, StringComparison.Ordinal)
            ? fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }
}
