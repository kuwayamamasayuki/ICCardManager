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
        // Issue #2264: KeyBackupPath は外した（職員証の認証と操作ログを伴う ChangeBackupPathAsync だけが書く）
        "KeyFontSize",
        "KeySoundMode",
        "KeyToastPosition",
        "KeyDepartmentType",
        "KeySkipBusStopInputOnReturn",
        "KeySkipCompanionCountInputOnReturn",
        // Issue #2009: 同行者数入力の自動クローズ秒数（設定画面から編集する）
        "KeyCompanionCountInputTimeoutSeconds",
        // Issue #2152: 長期未返却のしきい値（設定画面から編集する）
        "KeyLongTermUnreturnedDays",
        // Issue #1907: 返却時の利用履歴自動表示（設定画面から編集する）
        "KeyShowHistoryOnReturn",
        "KeyReportOutputFolder",
        // Issue #2305: ウィンドウの位置・サイズ（KeyWindow*）は外した（設定画面で編集しない。終了時に
        // SaveMainWindowSettingsAsync だけが書く。下の WindowKeyConstants）
    };

    private const string WindowSaveSignatureMarker = "Task<bool> SaveMainWindowSettingsAsync(WindowSettings windowSettings)";

    /// <summary>
    /// 終了時の保存（<c>SaveMainWindowSettingsAsync</c>。到達する private ヘルパーを含む）が書き込む設定キー定数（Issue #2305）。
    /// </summary>
    private static readonly string[] WindowKeyConstants =
    {
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
        // Issue #2264: バックアップ先とその変更記録は ChangeBackupPathAsync だけが書く
        "backup_path",
        "backup_path_changed_at",
        "backup_path_changed_machine",
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
        // Issue #2305 で一括保存からウィンドウ設定のヘルパー（SaveWindowSettingsToDbAsync）を外したため、
        // 目印は一括保存が今も呼ぶ private ヘルパー FontSizeToString にしか無い式（ExtraLarge → "xlarge" の変換）にした。
        // "xlarge" だけでは逆向きの ParseFontSize（呼ばれない）にも現れ、どちらへ展開したかを区別できない（レビュー 3 回目）。
        const string helperLiteral = "FontSizeOption.ExtraLarge => \"xlarge\"";
        var direct = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            File.ReadAllText(RepositoryPath), SaveSignatureMarker);

        TestSourceInspection.ExtractMethodBodyPreservingLiterals(
                File.ReadAllText(RepositoryPath), "private static string FontSizeToString(FontSizeOption fontsize)")
            .Should().Contain(helperLiteral, "前提: 目印は FontSizeToString の内側にある");
        Regex.Matches(File.ReadAllText(RepositoryPath), Regex.Escape(helperLiteral)).Count.Should().Be(
            1, "前提: 目印はファイル内で FontSizeToString にしか無い（展開先を区別できる）");

        direct.Should().NotContain(
            helperLiteral,
            "前提: 目印は FontSizeToString の内側にある（直接の本体には現れない）");

        ExtractSaveBodyWithCallees().Should().Contain(
            helperLiteral,
            "呼び出し先まで辿らないと、ヘルパーへ保守用のキーを書き足した形を検出できない");
    }

    /// <summary>
    /// Issue #2305: 終了時の保存（<c>SaveMainWindowSettingsAsync</c>）が書くのはウィンドウの位置・サイズの 5 キーだけであること。
    /// </summary>
    /// <remarks>
    /// 以前は終了時に一括保存へキャッシュ由来の <c>AppSettings</c> を丸ごと渡しており、共有モードで他の PC が変えた
    /// 部署種別・残額警告などを古い値へ巻き戻していた。専用経路へ他のキーを書き足すと同じ巻き戻りが再発する。
    /// 走査は一括保存と同じく、到達する private ヘルパー（<c>SaveWindowSettingsToDbAsync</c>）まで辿る。
    /// </remarks>
    [Fact]
    public void 終了時の保存が書き込む設定キーはウィンドウの位置とサイズだけであること()
    {
        var source = File.ReadAllText(RepositoryPath);

        TestSourceInspection.ExtractMethodBodyPreservingLiterals(source, WindowSaveSignatureMarker)
            .Should().NotContain(
                "KeyWindowLeft",
                "前提: キーはヘルパー SaveWindowSettingsToDbAsync の内側にある（直接の本体だけを見ると空振りする）");

        ExtractKeyConstants(ExpandWithCallees(source, WindowSaveSignatureMarker)).Should().BeEquivalentTo(
            WindowKeyConstants,
            "終了時の保存はキャッシュの古さに依らず安全でなければならない。他のキーを書くと、" +
            "他の PC が変えた設定を古い値へ巻き戻す（Issue #2305）");
    }

    /// <summary>
    /// Issue #2305: 本番コードで一括保存（<c>SaveAppSettingsAsync</c>）を呼ぶのは設定画面（<c>SettingsViewModel</c>）だけであること。
    /// </summary>
    /// <remarks>
    /// 巻き戻りの実際の入口は「<c>GetAppSettingsAsync()</c>（TTL キャッシュ）で読む → 一部だけ変える → 一括保存」という呼び出し元で、
    /// 一括保存のキー集合を固定するだけでは防げない（#1997 / #2264 / #2305 と 3 度、この形の呼び出し元が原因になった）。
    /// 設定画面は画面の値から <c>new AppSettings { … }</c> を組み立てて渡すが、画面の初期値は設定の読み取り結果なので、
    /// <b>同じ巻き戻りは設定画面の経路にも残っている</b>（開いた時点の値が古ければ、別の項目だけを変えた保存で巻き戻す）。
    /// 開くたびに DB から読み直して窓を縮めている（<c>SettingsViewModel.LoadSettingsAsync</c>）。根本対策は別 Issue で扱う。
    /// 新しい呼び出し元が要るときは、一括保存ではなく書くキーだけを書く専用メソッドを足すこと（<c>SaveMainWindowSettingsAsync</c> と同じ形）。
    /// </remarks>
    [Fact]
    public void 一括保存を呼ぶ本番コードは設定画面だけであること()
    {
        var callers = ProductionSourceFiles.CSharp
            .Where(f => CountBulkSaveCalls(f.CodeOnly, selfCalls: f.Name == RepositoryFileName) > 0)
            .Select(f => f.Name)
            .ToList();

        callers.Should().BeEquivalentTo(
            new[] { "SettingsViewModel.cs" },
            "一括保存は渡された値をすべて書く。キャッシュで読んだ設定を渡す呼び出し元は、" +
            "共有モードで他の PC が変えた設定を巻き戻す（Issue #2305）");
    }

    /// <summary>
    /// 対の表明: 終了時の保存（<c>MainWindow</c>）が専用経路を実際に呼んでいること。
    /// これが無いと、終了時の保存を丸ごと消した実装でも上の検査は緑になる。
    /// </summary>
    [Fact]
    public void 終了時の保存はウィンドウ位置の専用経路を呼んでいること()
    {
        var mainWindow = ProductionSourceFiles.CSharp.Single(f => f.Name == "MainWindow.xaml.cs");
        var body = TestSourceInspection.ExtractMethodBody(
            mainWindow.CodeOnly, "private async Task SaveWindowPositionAsync(WindowPosition position)");

        body.Should().Contain(".SaveMainWindowSettingsAsync(");
        CountBulkSaveCalls(body).Should().Be(0);
        body.Should().NotContain(
            "GetAppSettingsAsync",
            "キャッシュから読んだ設定を保存へ使う形に戻さない（Issue #2305）");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（呼び出し・メソッドグループ・null 条件演算子を数え、定義は数えない）。
    /// <c>SettingsRepository.cs</c> の中ではドットを伴わない自己呼び出しも数える（同じクラスに一括保存を呼ぶ便利メソッドを足すと、
    /// ドット付きの呼び出しだけを数える検査を素通りするため。レビュー 1 回目で追加）。
    /// </summary>
    [Theory]
    [InlineData("var ok = await _settingsRepository.SaveAppSettingsAsync(settings);", false, 1)]
    [InlineData("await Task.Run(() => _repo.SaveAppSettingsAsync(s));", false, 1)]
    [InlineData("var f = _repo?.SaveAppSettingsAsync;", false, 1)]
    [InlineData("await _repo\n    .SaveAppSettingsAsync(s);", false, 1)]
    [InlineData("public async Task<bool> SaveAppSettingsAsync(AppSettings settings)", false, 0)]
    [InlineData("Task<bool> SaveAppSettingsAsync(AppSettings settings);", false, 0)]
    [InlineData("await _repo.SaveAppSettingsAsyncLegacy(s);", false, 0)]
    [InlineData("success = await SaveAppSettingsAsync(settings).ConfigureAwait(false);", true, 1)]
    [InlineData("public async Task<bool> SaveAppSettingsAsync(AppSettings settings)", true, 0)]
    public void 一括保存の呼び出しの検出は呼び出しだけを数えること(string code, bool selfCalls, int expected)
    {
        CountBulkSaveCalls(code, selfCalls).Should().Be(expected);
    }

    private static readonly Regex BulkSaveCall = new Regex(@"\.\s*SaveAppSettingsAsync\b");

    /// <summary>ドットを伴わない呼び出し（同じクラスからの自己呼び出し）。定義（戻り値の型の直後の名前）は除く。</summary>
    private static readonly Regex SelfBulkSaveCall = new Regex(@"(?<!Task<bool>\s+)(?<!\.\s*)(?<!\w)SaveAppSettingsAsync\s*\(");

    private const string RepositoryFileName = "SettingsRepository.cs";

    private static int CountBulkSaveCalls(string code, bool selfCalls = false)
        => BulkSaveCall.Matches(code).Count + (selfCalls ? SelfBulkSaveCall.Matches(code).Count : 0);

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
    /// 対の表明（Issue #2264）: 専用経路はバックアップ先と変更記録を<b>実際に書いている</b>こと。
    /// これが無いと、バックアップ先の書き込みを両方から消した実装でも一括保存側の検査は緑になる。
    /// </summary>
    [Fact]
    public void バックアップ先の専用経路は値と変更記録を書いていること()
    {
        var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            File.ReadAllText(RepositoryPath),
            "public async Task ChangeBackupPathAsync(");

        body.Should().Contain("KeyBackupPath,");
        body.Should().Contain("KeyBackupPathChangedAt");
        body.Should().Contain("KeyBackupPathChangedMachine");
    }

    /// <summary>
    /// Issue #2264: <c>backup_path</c> を書くのは <c>ChangeBackupPathAsync</c>（職員証の認証と操作ログを伴う経路）だけであること。
    /// </summary>
    /// <remarks>
    /// 汎用の <c>ISettingsRepository.SetAsync(key, value)</c> はどのキーでも書けるため、一括保存から外しただけでは
    /// 別の経路から認証も記録も無く書き換えられる。本番ソース全体で <c>KeyBackupPath</c> の参照を数え、
    /// 定義・読み取り（<c>Get</c> / <c>GetAsync</c> の第 1 引数）と <c>ChangeBackupPathAsync</c> の内側以外を違反とする。
    /// 生の文字列 <c>"backup_path"</c> を <c>SetAsync</c> へ渡す形も違反とする。
    /// </remarks>
    [Fact]
    public void backup_pathは専用経路の外から書き込まないこと()
    {
        var violations = ProductionSourceFiles.CSharp
            .SelectMany(f => FindBackupPathWritesOutsideDedicatedPath(f.Name, f.CodeOnly, f.CommentsRemovedPreservingLines)
                .Select(v => $"  - {f.RelativePath}: {v}"))
            .ToList();

        violations.Should().BeEmpty(
            "バックアップ先は全 PC が DB 全体をコピーする先で、変更は職員証の認証と操作ログを伴う ChangeBackupPathAsync だけが行う（Issue #2264）\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// 対の表明: 検査が実際の参照を拾っていること（読み取りは許し、ChangeBackupPathAsync の内側を除外できていること）。
    /// </summary>
    [Fact]
    public void backup_pathの書き込み検査は本番の読み取りと専用経路を見分けていること()
    {
        var repository = ProductionSourceFiles.CSharp.Single(f => f.Name == "SettingsRepository.cs");
        BackupPathKeyReference.Matches(repository.CodeOnly).Count.Should().BeGreaterOrEqualTo(4,
            "定義・同期と非同期の読み取り・専用経路の書き込みを拾えていること");
        FindBackupPathWritesOutsideDedicatedPath(repository.Name, repository.CodeOnly, repository.CommentsRemovedPreservingLines)
            .Should().BeEmpty();
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（書き込みは検出し、読み取りは検出しない）。
    /// </summary>
    [Fact]
    public void backup_pathの書き込み検査は書き込みだけを検出すること()
    {
        const string write = "await _settingsRepository.SetAsync(SettingsRepository.KeyBackupPath, path);";
        const string read = "var v = await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPath, tx);";
        const string literalWrite = "await repo.SetAsync(\"backup_path\", path);";

        FindBackupPathWritesOutsideDedicatedPath("X.cs", write, write).Should().HaveCount(1);
        FindBackupPathWritesOutsideDedicatedPath("X.cs", read, read).Should().BeEmpty();
        FindBackupPathWritesOutsideDedicatedPath("X.cs", "var s = \"\";", literalWrite).Should().HaveCount(1);
    }

    private static readonly Regex BackupPathKeyReference = new Regex(@"\bKeyBackupPath\b");
    private static readonly Regex BackupPathReadPrefix = new Regex(@"\b(?:Get|GetAsync)\(\s*(?:SettingsRepository\.)?$");
    private static readonly Regex BackupPathDefinitionPrefix = new Regex(@"\bconst\s+string\s+$");
    private static readonly Regex BackupPathLiteralWrite = new Regex(@"SetAsync\(\s*""backup_path""");

    private static IReadOnlyList<string> FindBackupPathWritesOutsideDedicatedPath(
        string fileName, string codeOnly, string commentsRemoved)
    {
        var violations = new List<string>();
        var allowedStart = -1;
        var allowedEnd = -1;
        if (fileName == "SettingsRepository.cs")
        {
            var body = TestSourceInspection.ExtractMethodBody(codeOnly, "public async Task ChangeBackupPathAsync(");
            allowedStart = codeOnly.IndexOf(body, StringComparison.Ordinal);
            allowedEnd = allowedStart + body.Length;
        }

        foreach (Match match in BackupPathKeyReference.Matches(codeOnly))
        {
            if (match.Index >= allowedStart && match.Index < allowedEnd)
            {
                continue;
            }

            var before = codeOnly.Substring(0, match.Index);
            if (BackupPathReadPrefix.IsMatch(before) || BackupPathDefinitionPrefix.IsMatch(before))
            {
                continue;
            }

            var lineStart = codeOnly.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
            var lineEnd = codeOnly.IndexOf('\n', match.Index);
            violations.Add(codeOnly.Substring(lineStart, (lineEnd < 0 ? codeOnly.Length : lineEnd) - lineStart).Trim());
        }

        foreach (Match match in BackupPathLiteralWrite.Matches(commentsRemoved))
        {
            violations.Add(match.Value);
        }

        return violations;
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
