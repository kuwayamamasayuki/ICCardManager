using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1997: <c>SettingsRepository.SaveAppSettingsAsync</c> が書き込む設定キーの集合を固定する静的検査。
/// </summary>
/// <remarks>
/// <para>
/// <c>settings</c> テーブルには、画面から編集する設定と、保守処理だけが書く値が同居している。
/// 後者（<c>last_vacuum_date</c> の CAS ロック（#1482）、<c>last_backup_success_at</c> /
/// <c>last_backup_machine</c> / <c>last_vacuum_machine</c> の実施記録（#1689））は、
/// <b>専用の書き込み経路が持つ条件（月ガード・成功時のみ記録）ごとが仕様</b>であり、
/// 素の <c>ON CONFLICT DO UPDATE</c> で一括保存すると条件が失われる。
/// </para>
/// <para>
/// 実際 <c>last_vacuum_date</c> は一括保存に載っており、TTL キャッシュ由来の古い
/// <c>AppSettings</c>（他 PC が CAS を獲得する前の値）を書き戻して当月のロックを巻き戻していた。
/// 挙動テストは「いま載っているキー」ごとの回帰しか固定できないため、<b>集合そのもの</b>を
/// ここで固定する（`.claude/rules/error-messages.md` #1764「個別テストは経路の追加に追随できない」）。
/// 期待値は本番の定数から導出せず<b>リテラルで書く</b>（#1884 / #1940: 本番と期待値が
/// 同時に動くと表明が自己充足する）。
/// </para>
/// <para>
/// <b>走査は一括保存から到達する private ヘルパーまで辿る</b>（コードレビューで検出）。
/// 直接の本体だけを見ると、<c>SaveWindowSettingsToDbAsync</c> のようなヘルパー（あるいは今後
/// 追加されるヘルパー）の内側へ保守用のキーを書き足した形が 4 件すべて緑のまま通り、
/// #1997 がそのまま再発する（`.claude/rules/development-conventions.md` #1786
/// 「守りたい性質ではなく、その性質を破れる全経路を列挙する」）。
/// </para>
/// </remarks>
public class SettingsMaintenanceKeyConventionTests
{
    private static readonly string RepositoryPath = Path.Combine(
        TestPaths.GetProductionSourceRoot(), "Data", "Repositories", "SettingsRepository.cs");

    private const string SaveSignatureMarker = "Task<bool> SaveAppSettingsAsync(AppSettings settings)";

    /// <summary>
    /// 一括保存（到達する private ヘルパーを含む）が書き込む設定キー定数。
    /// 増減させるときは、その値が「画面から編集する設定」であることを確かめること。
    /// </summary>
    private static readonly string[] ExpectedKeyConstants =
    {
        "KeyWarningBalance",
        "KeyBackupPath",
        "KeyFontSize",
        "KeySoundMode",
        "KeyToastPosition",
        "KeyDepartmentType",
        "KeySkipBusStopInputOnReturn",
        "KeySkipCompanionCountInputOnReturn",
        "KeyReportOutputFolder",
        // SaveWindowSettingsToDbAsync（一括保存から呼ばれる private ヘルパー）が書く
        "KeyWindowLeft",
        "KeyWindowTop",
        "KeyWindowWidth",
        "KeyWindowHeight",
        "KeyWindowMaximized",
    };

    /// <summary>
    /// 専用経路だけが書いてよい保守用のキー（生の文字列でも書かせない）。
    /// </summary>
    private static readonly string[] MaintenanceKeyLiterals =
    {
        "last_vacuum_date",
        "last_vacuum_machine",
        "last_backup_success_at",
        "last_backup_machine",
    };

    [Fact]
    public void 一括保存が書き込む設定キーは画面から編集する設定だけであること()
    {
        var body = ExtractSaveBodyWithCallees();

        ExtractKeyConstants(body).Should().BeEquivalentTo(
            ExpectedKeyConstants,
            "保守用のキーを足すと専用経路の条件（月ガード・成功時のみ記録）が失われ、" +
            "抜くと画面で編集した設定が保存されなくなる（Issue #1997）");
    }

    [Fact]
    public void 一括保存は保守用のキーを生の文字列でも書かないこと()
    {
        var body = ExtractSaveBodyWithCallees();

        foreach (var literal in MaintenanceKeyLiterals)
        {
            body.Should().NotContain(
                literal,
                $"{literal} は専用の書き込み経路だけが更新する（Issue #1482 / #1689 / #1997）");
        }
    }

    /// <summary>
    /// 対の表明: 走査が一括保存から呼ばれる private ヘルパーの内側まで届いていること。
    /// これが無いと、ヘルパーへ保守用のキーを書き足した形を検出できないまま緑になる。
    /// </summary>
    [Fact]
    public void 走査は一括保存から呼ばれるヘルパーの内側まで届くこと()
    {
        var direct = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            File.ReadAllText(RepositoryPath), SaveSignatureMarker);

        direct.Should().NotContain(
            "KeyWindowLeft",
            "ウィンドウ設定キーは SaveWindowSettingsToDbAsync の内側にある（直接の本体には現れない）");

        ExtractKeyConstants(ExtractSaveBodyWithCallees()).Should().Contain(
            "KeyWindowLeft",
            "呼び出し先まで辿らないと、ヘルパーへ保守用のキーを書き足した形を検出できない");
    }

    /// <summary>
    /// 対の表明: CAS 経路は <c>last_vacuum_date</c> を<b>実際に書いている</b>こと。
    /// これが無いと、書き込みを両方から消した実装でも上の 2 件は緑になる。
    /// </summary>
    [Fact]
    public void CAS経路はlast_vacuum_dateを書いていること()
    {
        var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            File.ReadAllText(RepositoryPath),
            "Task<bool> TryAcquireMonthlyVacuumLockAsync(DateTime today)");

        body.Should().Contain("KeyLastVacuumDate");
        body.Should().Contain(
            "substr(settings.value, 1, 7)",
            "月ガード付きの UPSERT であること（この条件こそが一括保存へ載せられない理由）");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが変わっても空振りしないようにする、Issue #1786）。
    /// </summary>
    [Fact]
    public void 抽出はキー定数だけを返すこと()
    {
        const string sample = @"
            success &= await SetAsync(KeyWarningBalance, ""1"", scope);
            success &= await SetAsync(SettingsRepository.KeyBackupPath, ""x"", scope);
            _cacheService.Invalidate(CacheKeys.AppSettings);
            var legacy = LegacyKeyBackupPath;
            var underscored = _KeyToastPosition;
            var keyed = KeyLastVacuumDate;
";

        ExtractKeyConstants(sample).Should().BeEquivalentTo(
            new[] { "KeyWarningBalance", "KeyBackupPath", "KeyLastVacuumDate" },
            "CacheKeys のような別クラス名も、識別子の途中に Key を含む名前（LegacyKeyBackupPath / " +
            "_KeyToastPosition）も拾わないこと（後読みを外すとこの表明が赤くなる）");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する: 呼び出し先の展開が 1 段だけで止まらないこと。
    /// </summary>
    [Fact]
    public void 呼び出し先の展開は多段のヘルパーを辿ること()
    {
        const string sample = @"
namespace Sample
{
    public class Repo
    {
        public async Task<bool> TargetAsync(int id)
        {
            await SetAsync(KeyWarningBalance);
            await FirstHelperAsync(id);
            return true;
        }

        private async Task FirstHelperAsync(int id)
        {
            await SecondHelperAsync(id);
        }

        private async Task SecondHelperAsync(int id)
        {
            await SetAsync(KeyWindowLeft);
        }

        private async Task UnreachableAsync(int id)
        {
            await SetAsync(KeyLastVacuumDate);
        }
    }
}
";

        var body = ExpandWithCallees(sample, "Task<bool> TargetAsync(int id)");

        ExtractKeyConstants(body).Should().BeEquivalentTo(
            new[] { "KeyWarningBalance", "KeyWindowLeft" },
            "2 段先のヘルパーまで辿り、呼ばれていないメソッドは巻き込まないこと");
    }

    private static string ExtractSaveBodyWithCallees()
    {
        return ExpandWithCallees(File.ReadAllText(RepositoryPath), SaveSignatureMarker);
    }

    /// <summary>
    /// 指定メソッドの本体と、そこから到達する同一クラスの private メソッドの本体を連結して返す。
    /// </summary>
    private static string ExpandWithCallees(string source, string signatureMarker)
    {
        var declarations = ExtractPrivateMethodDeclarations(source);

        var bodies = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        var rootBody = TestSourceInspection.ExtractMethodBodyPreservingLiterals(source, signatureMarker);
        bodies.Add(rootBody);
        EnqueueCallees(rootBody, declarations, visited, pending);

        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(source, declarations[name]);
            bodies.Add(body);
            EnqueueCallees(body, declarations, visited, pending);
        }

        return string.Join("\n", bodies);
    }

    private static void EnqueueCallees(
        string body,
        IReadOnlyDictionary<string, string> declarations,
        HashSet<string> visited,
        Queue<string> pending)
    {
        foreach (Match match in Regex.Matches(body, @"(?<![A-Za-z0-9_.])(?<name>[A-Za-z_]\w*)\s*\("))
        {
            var name = match.Groups["name"].Value;
            if (declarations.ContainsKey(name) && visited.Add(name))
            {
                pending.Enqueue(name);
            }
        }
    }

    /// <summary>
    /// ソース中の private メソッド宣言を「名前 → シグネチャマーカー（戻り値の型＋引数リスト）」で返す。
    /// </summary>
    /// <remarks>
    /// マーカーに引数リストを含めるのは、オーバーロードを持つメソッド（<c>SetAsync</c>）で
    /// <see cref="TestSourceInspection.ExtractMethodBodyPreservingLiterals"/> が
    /// 「複数一致」の例外で止まらないようにするため。同名のオーバーロードが複数あるときは
    /// 引数の多い方（＝スコープを引き渡す形）を採る — どちらも設定キーを持たないため集合は変わらない。
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ExtractPrivateMethodDeclarations(string source)
    {
        var code = TestSourceInspection.RemoveCommentsPreservingLines(source).Replace("\r\n", "\n");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var pattern = new Regex(
            @"(?<![A-Za-z0-9_])private\s+(?:static\s+)?(?:async\s+)?(?<signature>[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?\s]*?\s+(?<name>[A-Za-z_]\w*)\s*)\(");

        foreach (Match match in pattern.Matches(code))
        {
            var openParen = match.Index + match.Length - 1;
            var closeParen = FindMatchingParen(code, openParen);
            if (closeParen < 0)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            var marker = match.Groups["signature"].Value.Trim()
                + code.Substring(openParen, closeParen - openParen + 1);

            // 同名のオーバーロードは引数リストの長い方（スコープ引き渡し版）を採る
            if (!result.TryGetValue(name, out var existing) || marker.Length > existing.Length)
            {
                result[name] = marker;
            }
        }

        return result;
    }

    private static int FindMatchingParen(string code, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> ExtractKeyConstants(string body)
    {
        return Regex.Matches(body, @"(?<![A-Za-z0-9_])Key[A-Z][A-Za-z0-9_]*")
            .Cast<Match>()
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
