#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2306: 摘要のバス停名を明細へ書き戻す対応付け（<see cref="SummaryGenerator.PlanBusStopSync"/>）の単体テスト。
/// </summary>
/// <remarks>
/// <para>
/// 往復のバス摘要（「バス（A～B 往復）」）は 2 明細から 1 区間を作るため、旧実装の「区間数＝明細数」の
/// ガードでは<b>必ず</b>件数が合わず、行編集で直した往復が明細へ反映されなかった。
/// 往復は往路・復路の 2 件へ展開し、生成（<c>FormatRoundTrip</c>）の逆変換になっていることを、
/// 「同期した明細から作り直した摘要が、編集した摘要と一致する」形で固定する（統合しても編集が保たれる）。
/// </para>
/// <para>
/// 対の表明として「位置の対応付けが誤りになる形（往路と復路が隣り合わない）」「件数が合わない形」を
/// <see cref="BusStopSyncOutcome.Unmappable"/> と表明し、展開を無条件に行う実装・ガードを外した実装を赤にする。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGeneratorBusStopSyncPlanTests : IDisposable
{
    private static readonly DateTime UseDate = new(2026, 4, 1);

    public SummaryGeneratorBusStopSyncPlanTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// バス明細を作る。<paramref name="sequenceNumber"/> は FeliCa 互換で大きいほど古い。
    /// </summary>
    private static LedgerDetail Bus(int sequenceNumber, string? busStops, int balance) => new()
    {
        UseDate = UseDate,
        SequenceNumber = sequenceNumber,
        IsBus = true,
        BusStops = busStops,
        Amount = 230,
        Balance = balance,
    };

    /// <summary>古い順に並べたバス停名から、バス明細を作る（残高は利用ごとに 230 円ずつ減る）。</summary>
    private static List<LedgerDetail> BusDetails(params string?[] busStopsOldestFirst)
        => busStopsOldestFirst
            .Select((busStops, i) => Bus(
                sequenceNumber: busStopsOldestFirst.Length - i,
                busStops,
                balance: 5000 - (230 * (i + 1))))
            .ToList();

    /// <summary>明細から摘要を作り直す（統合が行うのと同じ再生成。Generate は新しい順を受け取る）。</summary>
    private static string Regenerate(IEnumerable<LedgerDetail> details)
        => new SummaryGenerator().Generate(details.OrderBy(d => d.SequenceNumber).ToList());

    private static Dictionary<int, string> AssignedBySequence(BusStopSyncPlan plan)
        => plan.Assignments.ToDictionary(a => a.Detail.SequenceNumber, a => a.BusStops);

    private static void Apply(BusStopSyncPlan plan)
    {
        foreach (var assignment in plan.Assignments)
        {
            assignment.Detail.BusStops = assignment.BusStops;
        }
    }

    [Fact]
    public void 往復の摘要_往路と復路の2明細へ展開すること()
    {
        var details = BusDetails("★", "★");
        const string summary = "バス（天神～博多 往復）";

        var plan = SummaryGenerator.PlanBusStopSync(summary, details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "天神～博多",
            [1] = "博多～天神",
        }, "古い明細が往路、新しい明細が復路（逆向き）");

        // 受け入れ条件: 同期した明細から摘要を作り直しても、編集した摘要が保たれる
        Apply(plan);
        Regenerate(details).Should().Be(summary);
    }

    [Fact]
    public void 往復の摘要を直す_既存の往復の明細を書き換えること()
    {
        var details = BusDetails("天神～博多", "博多～天神");
        Regenerate(details).Should().Be("バス（天神～博多 往復）", "前提: 元の摘要は往復");
        const string edited = "バス（天神～大橋 往復）";

        var plan = SummaryGenerator.PlanBusStopSync(edited, details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "天神～大橋",
            [1] = "大橋～天神",
        });
        Apply(plan);
        Regenerate(details).Should().Be(edited);
    }

    [Fact]
    public void 往復の端点の併記_同一視グループの別名なら復路は併記の名前にすること()
    {
        SummaryGenerator.ApplyTransferStationGroups(new[]
        {
            new[] { "天神日銀前", "天神中央郵便局前" },
        });
        var details = BusDetails("★", "★");
        const string summary = "バス（天神日銀前（天神中央郵便局前）～下原中央 往復）";

        var plan = SummaryGenerator.PlanBusStopSync(summary, details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "天神日銀前～下原中央",
            [1] = "下原中央～天神中央郵便局前",
        }, "帰りに降りたバス停（併記の括弧の中）を台帳から失わない（Issue #1905）");
        Apply(plan);
        Regenerate(details).Should().Be(summary);
    }

    [Fact]
    public void 同一視されていない括弧_併記ではなくバス停名の一部として扱うこと()
    {
        // 同一視グループの登録なし: 「博多駅（筑紫口）」は 1 つのバス停名
        var details = BusDetails("★", "★");
        const string summary = "バス（博多駅（筑紫口）～天神 往復）";

        var plan = SummaryGenerator.PlanBusStopSync(summary, details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "博多駅（筑紫口）～天神",
            [1] = "天神～博多駅（筑紫口）",
        });
        Apply(plan);
        Regenerate(details).Should().Be(summary);
    }

    /// <summary>
    /// 往復の検出は隣り合わない区間とも組む（天神→博多／大橋→薬院／博多→天神 は「天神～博多 往復、大橋～薬院」）。
    /// 位置で書き戻すと 2 番目の利用（大橋→薬院）へ復路の名前が入るため、対応付けを諦める。
    /// </summary>
    [Fact]
    public void 往路と復路が隣り合わない往復_位置で書き戻さないこと()
    {
        var details = BusDetails("天神～博多", "大橋～薬院", "博多～天神");
        Regenerate(details).Should().Be("バス（天神～博多 往復、大橋～薬院）", "前提: 隣り合わない往復が検出される");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～吉塚 往復、大橋～薬院）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Unmappable);
        plan.Failure.Should().Be(BusStopSyncFailure.RoundTripNotAdjacent, "件数は 3＝3 で合っている");
        plan.Assignments.Should().BeEmpty();
    }

    /// <summary>
    /// コードレビューで検出: 往路の候補の片方が未入力（★）でも、解析できた側の相手（逆向きの値）が
    /// ほかの位置にあれば位置で書き戻さない。明細 [天神～博多, ★, 博多～天神] に手で書いた
    /// 「天神～博多 往復、★」を位置で書くと、★の明細へ「博多～天神」が、博多～天神 の明細へ★が入れ替わって入る。
    /// </summary>
    [Fact]
    public void 片方が未入力で相手がほかの位置にある往復_位置で書き戻さないこと()
    {
        var details = BusDetails("天神～博多", "★", "博多～天神");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～博多 往復、★）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Unmappable);
        plan.Failure.Should().Be(BusStopSyncFailure.RoundTripNotAdjacent);
    }

    /// <summary>上の対: 片方が未入力でも、相手がほかの位置に無ければ摘要の並びを信じて書き戻す。</summary>
    [Fact]
    public void 片方が未入力で相手がほかの位置に無い往復_位置で書き戻すこと()
    {
        var details = BusDetails("天神～博多", "★", "大橋～薬院");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～博多 往復、大橋～薬院）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [3] = "天神～博多",
            [2] = "博多～天神",
            [1] = "大橋～薬院",
        });
    }

    /// <summary>上の対: 往路と復路が隣り合っていれば、同じ摘要を書き戻すこと。</summary>
    [Fact]
    public void 往路と復路が隣り合う往復と片道_それぞれへ書き戻すこと()
    {
        var details = BusDetails("天神～博多", "博多～天神", "大橋～薬院");
        const string edited = "バス（天神～吉塚 往復、大橋～薬院）";

        var plan = SummaryGenerator.PlanBusStopSync(edited, details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [3] = "天神～吉塚",
            [2] = "吉塚～天神",
            [1] = "大橋～薬院",
        });
        Apply(plan);
        Regenerate(details).Should().Be(edited);
    }

    /// <summary>
    /// コードレビュー 2 回目で検出: 相手の候補から、ほかの往復の枠で隣どうしが既に組めている位置は外す。
    /// 明細 [A～B, B～A, A～B, ★] に「A～B 往復、A～B 往復」では、2 組目の A～B の逆向き（位置 1 の B～A）は
    /// 1 組目が使っているので 2 組目の相手ではない。
    /// </summary>
    [Fact]
    public void ほかの往復が使っている明細_相手の候補に数えず書き戻すこと()
    {
        var details = BusDetails("天神～博多", "博多～天神", "天神～博多", "★");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～博多 往復、天神～博多 往復）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [4] = "天神～博多",
            [3] = "博多～天神",
            [2] = "天神～博多",
            [1] = "博多～天神",
        });
    }

    /// <summary>上の対: ほかの往復の枠でも、隣どうしが組めていない位置は相手の候補に数える。</summary>
    [Fact]
    public void 組めていない往復の枠にある明細_相手の候補に数えて書き戻さないこと()
    {
        var details = BusDetails("天神～博多", "★", "博多～天神", "★");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～博多 往復、天神～博多 往復）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Unmappable);
        plan.Failure.Should().Be(BusStopSyncFailure.RoundTripNotAdjacent);
    }

    /// <summary>
    /// コードレビュー 2 回目で検出: 往復の相手を取り違える余地が無ければ、現在の値が行きと帰りになっていなくても
    /// 摘要を正として書き戻す（帰りの入力ミス「博多～薬院」を、往復の摘要で直す）。
    /// </summary>
    [Fact]
    public void 相手の候補がほかに無い往復_現在の値が行きと帰りでなくても書き戻すこと()
    {
        var details = BusDetails("天神～博多", "博多～薬院");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～博多 往復）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "天神～博多",
            [1] = "博多～天神",
        });
    }

    /// <summary>
    /// コードレビュー 2 回目で検出: 利用 1 件に区間が複数ある摘要は対応付けない。以前は先頭のブロックをそのまま書き、
    /// 「バス（X～Y、Z～W）」は結合テキストを 1 明細へ、ブロックが 2 つなら 2 つ目を無言で捨てていた。
    /// </summary>
    [Theory]
    [InlineData("バス（天神～博多、大橋～薬院）")]
    [InlineData("バス（天神～博多）、鉄道（博多～吉塚）、バス（吉塚～千早）")]
    public void バス利用1件に区間が複数の摘要_件数の不一致と判定すること(string summary)
    {
        var plan = SummaryGenerator.PlanBusStopSync(summary, BusDetails("★"));

        plan.Outcome.Should().Be(BusStopSyncOutcome.Unmappable);
        plan.Failure.Should().Be(BusStopSyncFailure.CountMismatch);
    }

    /// <summary>上の対: 利用 1 件に区間 1 つは従来どおり書き戻す。</summary>
    [Fact]
    public void バス利用1件に区間1つの摘要_書き戻すこと()
    {
        var details = BusDetails("★");

        var plan = SummaryGenerator.PlanBusStopSync("鉄道（博多～吉塚）、バス（吉塚～千早）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string> { [1] = "吉塚～千早" });
    }

    /// <summary>
    /// 往復の接尾辞を空に設定した組織では往復の表記が無いので、警告文は往復に触れない（コードレビュー 2 回目の参考指摘）。
    /// </summary>
    [Fact]
    public void 往復の接尾辞が空の組織_警告文が往復に触れないこと()
    {
        var options = new OrganizationOptions();
        options.SummaryText.RoundTripSuffix = string.Empty;
        SummaryGenerator.Configure(options);

        var message = ICCardManager.ViewModels.LedgerRowEditViewModel.BuildBusStopUnmappableWarning(
            BusStopSyncFailure.CountMismatch, 2);

        message.Should().NotContain("往復");
        message.Should().EndWith("区切って入力してください。");
    }

    [Fact]
    public void 区間数がバス利用の件数と合わない摘要_対応付けられないと判定すること()
    {
        var plan = SummaryGenerator.PlanBusStopSync("バス（天神）", BusDetails("★", "★"));

        plan.Outcome.Should().Be(BusStopSyncOutcome.Unmappable);
        plan.Failure.Should().Be(BusStopSyncFailure.CountMismatch);
        plan.Assignments.Should().BeEmpty();
    }

    [Fact]
    public void バス利用1件に往復の摘要_対応付けられないと判定すること()
    {
        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～博多 往復）", BusDetails("★"));

        plan.Outcome.Should().Be(BusStopSyncOutcome.Unmappable);
        plan.Failure.Should().Be(BusStopSyncFailure.RoundTripOnSingleUsage);
    }

    [Fact]
    public void 往復でない複数区間_従来どおり位置で対応付けること()
    {
        var details = BusDetails("★", "★");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神～博多、大橋～薬院）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "天神～博多",
            [1] = "大橋～薬院",
        });
    }

    [Fact]
    public void 摘要にバスのブロックが無い_書き戻す対象なしと判定すること()
    {
        SummaryGenerator.PlanBusStopSync("鉄道（博多～天神）", BusDetails("★"))
            .Outcome.Should().Be(BusStopSyncOutcome.NothingToSync);
        SummaryGenerator.PlanBusStopSync("バス（天神～博多）", new List<LedgerDetail>())
            .Outcome.Should().Be(BusStopSyncOutcome.NothingToSync);
    }

    /// <summary>
    /// 不変条件: アプリ自身が生成した摘要を同期しても、明細の値は変わらない（同期は生成の逆変換）。
    /// 往復・併記・片道の混在を走査する。逆変換が崩れると、統合のたびに明細が書き換わる。
    /// </summary>
    [Theory]
    [InlineData("天神～博多|博多～天神")]
    [InlineData("天神～博多|博多～天神|大橋～薬院")]
    [InlineData("大橋～薬院|天神～博多|博多～天神")]
    [InlineData("天神～博多|博多～天神|天神～大橋|大橋～天神")]
    [InlineData("天神日銀前～下原中央|下原中央～天神中央郵便局前")]
    [InlineData("天神～博多|大橋～薬院")]
    public void 生成した摘要を同期しても明細は変わらないこと(string busStopsOldestFirstJoined)
    {
        SummaryGenerator.ApplyTransferStationGroups(new[]
        {
            new[] { "天神日銀前", "天神中央郵便局前" },
        });
        var details = BusDetails(busStopsOldestFirstJoined.Split('|'));
        var summary = Regenerate(details);

        var plan = SummaryGenerator.PlanBusStopSync(summary, details);

        // コードレビュー 3 回目: 現在の明細から作り直した摘要と同じなら、そもそも書き戻さない
        plan.Outcome.Should().Be(BusStopSyncOutcome.NothingToSync, $"生成した摘要「{summary}」は明細と整合している");
        plan.Changes.Should().BeEmpty($"生成した摘要「{summary}」の同期で明細が書き換わってはならない");
    }

    /// <summary>
    /// コードレビュー 3 回目で検出: 摘要を生成した後に同一視グループを消しても、併記「往路（復路）」を 1 つのバス停名と
    /// 読み直して明細を書き換えない（統合は摘要を変えていない行も同期する）。書き換えると、実際に降りた
    /// 天神中央郵便局前 が単独の値として 6 年保存の明細から失われる。
    /// </summary>
    [Fact]
    public void 同一視グループを消した後の併記の摘要_明細を書き換えないこと()
    {
        SummaryGenerator.ApplyTransferStationGroups(new[] { new[] { "天神日銀前", "天神中央郵便局前" } });
        var details = BusDetails("天神日銀前～下原中央", "下原中央～天神中央郵便局前");
        var summary = Regenerate(details);
        summary.Should().Be("バス（天神日銀前（天神中央郵便局前）～下原中央 往復）", "前提: グループがある間に生成した併記");
        SummaryGenerator.ApplyTransferStationGroups(Array.Empty<string[]>());

        var plan = SummaryGenerator.PlanBusStopSync(summary, details);

        plan.Changes.Should().BeEmpty();
        details.Select(d => d.BusStops).Should().Equal("天神日銀前～下原中央", "下原中央～天神中央郵便局前");
    }

    /// <summary>
    /// 上の対: グループを消した後でも、併記の往復を正当に直した編集は書き戻す。
    /// 括弧の外と内が現在の往路の乗車地・復路の降車地と一致するので、併記として読む。
    /// </summary>
    [Fact]
    public void 同一視グループを消した後に併記の往復を直した摘要_併記として書き戻すこと()
    {
        var details = BusDetails("天神日銀前～下原中央", "下原中央～天神中央郵便局前");

        var plan = SummaryGenerator.PlanBusStopSync("バス（天神日銀前（天神中央郵便局前）～下原 往復）", details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "天神日銀前～下原",
            [1] = "下原～天神中央郵便局前",
        });
    }

    /// <summary>
    /// コードレビュー 3 回目で検出: 往復の接尾辞で終わるのに「乗車～降車」と読めない区間は、
    /// 接尾辞ごと 1 件のバス停名として明細へ書かない。
    /// </summary>
    [Theory]
    [InlineData("バス（天神～博多～吉塚 往復）", 1)]
    [InlineData("バス（天神 往復）", 2)]
    public void 往復の接尾辞の前が乗車と降車でない区間_対応付けないこと(string summary, int busCount)
    {
        var details = BusDetails(Enumerable.Repeat<string?>("★", busCount).ToArray());

        var plan = SummaryGenerator.PlanBusStopSync(summary, details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Unmappable);
        plan.Failure.Should().Be(BusStopSyncFailure.MalformedRoundTrip);
    }

    [Fact]
    public void 往復の接尾辞を組織設定で変えた場合_設定の接尾辞で往復を読むこと()
    {
        var options = new OrganizationOptions();
        options.SummaryText.RoundTripSuffix = "（往復）";
        SummaryGenerator.Configure(options);
        var details = BusDetails("★", "★");
        const string summary = "バス（天神～博多（往復））";

        var plan = SummaryGenerator.PlanBusStopSync(summary, details);

        plan.Outcome.Should().Be(BusStopSyncOutcome.Mapped);
        AssignedBySequence(plan).Should().Equal(new Dictionary<int, string>
        {
            [2] = "天神～博多",
            [1] = "博多～天神",
        });
        Apply(plan);
        Regenerate(details).Should().Be(summary);
    }
}
