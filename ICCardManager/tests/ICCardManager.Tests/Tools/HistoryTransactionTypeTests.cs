using DebugDataViewer;
using FluentAssertions;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// DebugDataViewer の取引種別表示（<see cref="HistoryTransactionType"/>）の回帰（Issue #2012）。
    /// </summary>
    [Trait("Category", "Unit")]
    public class HistoryTransactionTypeTests
    {
        [Fact]
        public void ポイント還元の明細をポイント還元と表示すること()
        {
            // Issue #2012 の欠陥そのもの。是正前は「鉄道」と表示されていた
            var detail = new LedgerDetail { IsPointRedemption = true };

            HistoryTransactionType.Classify(detail).Should().Be("ポイント還元");
        }

        [Fact]
        public void ポイント還元とバスが同時に立つ既存データはポイント還元と表示すること()
        {
            // #1948 でこの複合状態は作られなくなったが、6 年保存の既存データには残り得る。
            // 本体の RouteDisplayFormatter と同じ優先順位（ポイント還元がバスより先）を保つ
            var detail = new LedgerDetail { IsPointRedemption = true, IsBus = true };

            HistoryTransactionType.Classify(detail).Should().Be("ポイント還元");
        }

        [Fact]
        public void チャージは他のどのフラグよりも優先すること()
        {
            var detail = new LedgerDetail { IsCharge = true, IsPointRedemption = true, IsBus = true };

            HistoryTransactionType.Classify(detail).Should().Be("チャージ");
        }

        [Fact]
        public void バスの明細をバスと表示すること()
        {
            // 対の表明: ポイント還元を足したことでバス判定を潰していないこと。
            // これが無いと、常に「ポイント還元」を返す実装でも上の 2 件は緑になる
            var detail = new LedgerDetail { IsBus = true };

            HistoryTransactionType.Classify(detail).Should().Be("バス");
        }

        [Fact]
        public void フラグがどれも立たない明細を鉄道と表示すること()
        {
            var detail = new LedgerDetail();

            HistoryTransactionType.Classify(detail).Should().Be("鉄道");
        }

        [Fact]
        public void チャージの明細をチャージと表示すること()
        {
            var detail = new LedgerDetail { IsCharge = true };

            HistoryTransactionType.Classify(detail).Should().Be("チャージ");
        }
    }
}
