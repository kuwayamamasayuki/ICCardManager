using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// IDm をログへ出力する際に <c>IdmMasker.Mask</c> を通していることを
/// ソーステキスト上で固定する規約テスト（Issue #1852）。
/// </summary>
/// <remarks>
/// <para>
/// 本システムでは職員証の IDm が唯一の認証要素であり、平文でログファイルへ残すと
/// ローカルユーザーが認証クレデンシャルを収集できる（CWE-532。Issue #1704 で
/// <c>IdmMasker</c> を導入した経緯は <c>Infrastructure/Security/IdmMasker.cs</c> を参照）。
/// </para>
/// <para>
/// <b>この規約を守らせる静的検査が長らく存在しなかった</b>。<c>IdmMaskerTests</c> は
/// マスカー自体の単体テストであり、「すべての IDm ログがマスクを通っているか」は
/// 誰も見ていなかったため、PR #1851 で新規追加されたログが規約違反を含んだまま
/// 全テストを通過した（セキュリティレビューで検出）。
/// </para>
/// <para>
/// 走査対象は <c>src/</c> 配下の <b>全 <c>.cs</c></b> から導出し、ファイル名で列挙しない
/// （画面・サービスが増えたときに静かに漏れる。
/// <c>.claude/rules/development-conventions.md</c> Issue #1786
/// 「ガードを書くときは『守りたい性質』ではなく『その性質を破れる全経路』を列挙する」）。
/// </para>
/// <para>
/// 判定は <b>呼び出しの引数リスト全体</b>を丸括弧の対応で切り出して行う。
/// 単一行の grep では検出できない — PR #1851 の違反は <c>LogWarning(</c> と
/// IDm 引数が別行にあった。
/// </para>
/// </remarks>
public class IdmLoggingMaskConventionTests
{
    /// <summary>
    /// ログ出力メソッドの呼び出し（<c>_logger.LogWarning</c> / <c>ErrorDialogHelper.LogException</c> 等）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 受け手を <c>_logger</c> に限定せず「<c>Log</c> で始まるメソッド呼び出し」で照合する。
    /// ログ出力を包む自前のヘルパー（<c>LogConnectionCheckOutcome</c> 等）へ生の IDm を
    /// 渡す形も同じ欠陥であり、受け手のフィールド名を直書きすると別の綴りで素通りする
    /// （<c>.claude/rules/development-conventions.md</c> Issue #1843
    /// 「ガードは綴りではなく資源で書く」）。
    /// </para>
    /// <para>
    /// 直前が識別子文字でないことを要求するため、<c>Catalog(</c> のような語尾一致は拾わない。
    /// <c>LogLevel.Information</c> のように丸括弧が続かない参照は
    /// <see cref="TestSourceInspection.ExtractInvocationArguments"/> 側で除かれる。
    /// </para>
    /// </remarks>
    private static readonly Regex LogInvocationPattern = new(
        @"(?<![A-Za-z0-9_])Log[A-Za-z]*",
        RegexOptions.Compiled);

    /// <summary>IDm を指す識別子（<c>idm</c> / <c>cardIdm</c> / <c>CardIdm</c> / <c>LenderIdm</c> …）。</summary>
    private static readonly Regex IdmIdentifierPattern = new(
        @"(?<![A-Za-z0-9_])[A-Za-z0-9_]*[Ii]dm[A-Za-z0-9_]*",
        RegexOptions.Compiled);

    /// <summary>
    /// 仮引数の宣言（<c>string? operatorIdm</c> 等）。<c>Log…</c> で始まる<b>メソッド定義</b>の
    /// 引数リストは呼び出しではないため対象外にする。
    /// </summary>
    /// <remarks>
    /// <c>OperationLogger</c> の後方互換オーバーロード（Issue #1265。引数は無視される）が
    /// この形で 11 件ある。呼び出し側の実引数は「型 名前」の 2 トークンにならないため、
    /// この除外で実際の違反を取りこぼすことはない
    /// （<c>ex</c> / <c>card.CardIdm</c> / <c>IdmMasker.Mask(idm)</c> のいずれも一致しない）。
    /// </remarks>
    private static readonly Regex ParameterDeclarationPattern = new(
        @"^(?:(?:this|ref|out|in|params)\s+)?[A-Za-z0-9_.<>\[\],?]+\s+[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 本番ソースの全 <c>.cs</c>（<c>bin</c> / <c>obj</c> を除く）を返す。
    /// </summary>
    private static IReadOnlyList<string> GetProductionSourceFiles()
    {
        var root = TestPaths.GetProductionSourceRoot();

        var files = Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // 走査対象が 0 件へ縮んだ状態でも緑になる空振りを防ぐ（Issue #1786）
        files.Should().NotBeEmpty($"本番ソース（{root}）が走査できること");

        return files;
    }

    /// <summary>
    /// ログ呼び出しの引数のうち、IDm をマスクせずに渡している箇所を列挙する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 引数ごとに判定する。1 つの呼び出しに「マスク済みの引数」と「生の引数」が
    /// 混在する形（<c>Mask(a), b</c>）は、呼び出し単位で <c>IdmMasker</c> の有無を見ると
    /// 見逃す。
    /// </para>
    /// <para>
    /// <c>masked…</c> という名前の変数は、上流で <c>IdmMasker.Mask</c> を通した値を
    /// 受けている前提で許容する（呼び出し行だけでは追えないため名前で判断する）。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> FindUnmaskedIdmLogArguments(string fileName, string source)
    {
        var codeOnly = TestSourceInspection.ToCodeOnlyPreservingLines(source);
        var violations = new List<string>();

        foreach (var (index, arguments) in
                 TestSourceInspection.ExtractInvocationArguments(codeOnly, LogInvocationPattern))
        {
            foreach (var argument in arguments)
            {
                if (!IsUnmaskedIdmArgument(argument))
                {
                    continue;
                }

                var line = codeOnly.Take(index).Count(c => c == '\n') + 1;
                violations.Add($"{fileName}:{line} → {argument}");
            }
        }

        return violations;
    }

    private static bool IsUnmaskedIdmArgument(string argument)
    {
        if (ParameterDeclarationPattern.IsMatch(argument))
        {
            // Log… で始まるメソッドの「定義」— 呼び出しではない
            return false;
        }

        var idmIdentifiers = IdmIdentifierPattern
            .Matches(argument)
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(v => !v.StartsWith("IdmMasker", StringComparison.Ordinal))
            .ToList();

        if (idmIdentifiers.Count == 0)
        {
            return false;
        }

        if (argument.Contains("IdmMasker"))
        {
            return false;
        }

        // 上流でマスク済みの値を受けた変数（maskedIdm 等）
        return !idmIdentifiers.All(v => v.StartsWith("masked", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 本番ソースのログ出力が、生の IDm を渡していないこと
    /// </summary>
    [Fact]
    public void 本番ソースのログ出力は生のIDmを渡さないこと()
    {
        // Act
        var violations = GetProductionSourceFiles()
            .SelectMany(f => FindUnmaskedIdmLogArguments(Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();

        // Assert
        violations.Should().BeEmpty(
            "IDm はログへ生で出さず IdmMasker.Mask() を通すこと（Issue #1704 / #1852、CWE-532）。" +
            $"違反: {string.Join(" / ", violations)}");
    }

    /// <summary>
    /// 「正しい形の存在」も対で表明する（不在だけを見ると、ログごと消した実装や
    /// 走査対象が縮んだ状態でも緑になる。<c>.claude/rules/error-messages.md</c> Issue #1817）
    /// </summary>
    [Fact]
    public void 本番ソースにはIdmMaskerを通したログが実際に存在すること()
    {
        // Act
        var usingFiles = GetProductionSourceFiles()
            .Where(f => TestSourceInspection.ToCodeOnly(File.ReadAllText(f)).Contains("IdmMasker.Mask"))
            .Select(Path.GetFileName)
            .ToList();

        // Assert
        usingFiles.Should().Contain("FelicaCardReader.cs")
            .And.Contain("LendingService.cs");
    }

    /// <summary>
    /// 検出力を既知のサンプル入力で固定する（実データが空でも空振りしない。Issue #1786）
    /// </summary>
    [Theory]
    // 違反: 生の IDm（PR #1851 と同じ「呼び出しと引数が別行」の形）
    [InlineData(
        "_logger.LogWarning(\n    \"修復しました: CardIdm={CardIdm}\",\n    card.CardIdm);",
        true)]
    // 違反: マスク済みと生が混在する呼び出し
    [InlineData(
        "_logger.LogError(\"{A} {B}\", IdmMasker.Mask(cardIdm), lentRecord.LenderIdm);",
        true)]
    // 違反: ログを包む自前ヘルパーへ生で渡す形
    [InlineData("LogCardOutcome(idm, result);", true)]
    // 準拠: マスクを通している
    [InlineData("_logger.LogInformation(\"カード検出 IDm={Idm}\", IdmMasker.Mask(idm));", false)]
    // 準拠: 上流でマスク済みの変数
    [InlineData("_logger.LogInformation(\"IDm={Idm}\", maskedIdm);", false)]
    // 準拠: 書式文字列にだけ Idm が現れる（値は IDm ではない）
    [InlineData("_logger.LogInformation(\"CardIdm 不明のため中止しました\", count);", false)]
    // 準拠: ログ呼び出しではない（丸括弧が続かない参照・別メソッド）
    [InlineData("var level = LogLevel.Information; Repair(card.CardIdm);", false)]
    // 準拠: 語尾が Log に一致するだけのメソッド
    [InlineData("Catalog(card.CardIdm);", false)]
    // 準拠: Log… で始まるメソッドの「定義」（呼び出しではない。OperationLogger の旧 API）
    [InlineData("public Task LogStaffInsertAsync(string? operatorIdm, Staff staff) => LogStaffInsertAsync(staff);", false)]
    public void 検出ロジックはサンプル入力で固定されていること(string snippet, bool expectedViolation)
    {
        // Act
        var violations = FindUnmaskedIdmLogArguments("Sample.cs", snippet);

        // Assert
        violations.Any().Should().Be(
            expectedViolation,
            $"サンプル「{snippet}」の判定。検出結果: {string.Join(" / ", violations)}");
    }
}
