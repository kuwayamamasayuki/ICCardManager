using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.ViewModels
{
    /// <summary>
    /// 帳票出力前プリフライトチェック結果ダイアログのViewModelの単体テスト（Issue #1688）
    /// </summary>
    public class ReportPreflightViewModelTests
    {
        private static ReportPreflightResult CreateResult(params ReportPreflightWarning[] warnings)
        {
            var result = new ReportPreflightResult();
            result.Warnings.AddRange(warnings);
            return result;
        }

        private static ReportPreflightWarning CreateWarning(
            string cardIdm, string cardDisplayName, DateTime? date,
            ReportPreflightIssueType issueType = ReportPreflightIssueType.NegativeBalance)
            => new ReportPreflightWarning
            {
                CardIdm = cardIdm,
                CardDisplayName = cardDisplayName,
                IssueType = issueType,
                Date = date,
                DisplayText = $"⚠️ {cardDisplayName} の警告",
                DetailText = "履歴画面で該当行を修正してください。"
            };

        /// <summary>
        /// 警告なしの場合、件数表示ではなく「問題なし」の文言になること
        /// </summary>
        [Fact]
        public void SetResult_WithNoWarnings_ReportsNoProblem()
        {
            var viewModel = new ReportPreflightViewModel();

            viewModel.SetResult(CreateResult(), 2026, 7, isConfirmationMode: false);

            viewModel.HasWarnings.Should().BeFalse();
            viewModel.HasNoWarnings.Should().BeTrue();
            viewModel.SummaryText.Should().Contain("問題は見つかりませんでした");
            viewModel.TargetPeriodText.Should().Be("2026年7月分");
        }

        /// <summary>
        /// 警告件数と対象カード枚数が要約に表示されること
        /// </summary>
        [Fact]
        public void SetResult_WithWarnings_ReportsCountAndCardCount()
        {
            var viewModel = new ReportPreflightViewModel();

            viewModel.SetResult(CreateResult(
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5)),
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 10)),
                CreateWarning("BBB", "nimoca 002", new DateTime(2026, 7, 3))),
                2026, 7, isConfirmationMode: true);

            viewModel.HasWarnings.Should().BeTrue();
            viewModel.HasNoWarnings.Should().BeFalse();
            viewModel.SummaryText.Should().Contain("3件").And.Contain("2枚");
            viewModel.IsConfirmationMode.Should().BeTrue();
        }

        /// <summary>
        /// 同一カードの警告が一箇所にまとまり、カード内では日付の昇順に並ぶこと
        /// </summary>
        /// <remarks>
        /// カード間の並び順は文字列の照合順序（カルチャ依存）に委ねるため、
        /// ここでは「連続していること」と「カード内の日付順」を検証する。
        /// </remarks>
        [Fact]
        public void SetResult_GroupsWarningsByCardAndSortsByDateWithinCard()
        {
            var viewModel = new ReportPreflightViewModel();

            viewModel.SetResult(CreateResult(
                CreateWarning("BBB", "nimoca 002", new DateTime(2026, 7, 3)),
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 20)),
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5))),
                2026, 7, isConfirmationMode: false);

            var cardOrder = viewModel.Warnings.Select(w => w.CardIdm).ToList();
            cardOrder.Distinct().Should().HaveCount(2);
            // 同じカードの警告が連続している（間に別カードが挟まらない）
            cardOrder.Should().BeEquivalentTo(new[] { "AAA", "AAA", "BBB" }, o => o.WithoutStrictOrdering());
            cardOrder.IndexOf("BBB").Should().Be(cardOrder.LastIndexOf("BBB"));

            viewModel.Warnings.Where(w => w.CardIdm == "AAA").Select(w => w.Date)
                .Should().ContainInOrder(new DateTime(2026, 7, 5), new DateTime(2026, 7, 20));
        }

        /// <summary>
        /// 日付を持たない警告（合計行）は同一カード内で末尾に並ぶこと
        /// </summary>
        [Fact]
        public void SetResult_PlacesWarningsWithoutDateLast()
        {
            var viewModel = new ReportPreflightViewModel();

            viewModel.SetResult(CreateResult(
                CreateWarning("AAA", "はやかけん 001", null, ReportPreflightIssueType.TotalMismatch),
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5))),
                2026, 7, isConfirmationMode: false);

            viewModel.Warnings.Last().IssueType.Should().Be(ReportPreflightIssueType.TotalMismatch);
        }

        /// <summary>
        /// 未選択時は詳細欄に操作を促す文言が表示されること
        /// </summary>
        [Fact]
        public void SelectedDetailText_WithNoSelection_PromptsUserToSelect()
        {
            var viewModel = new ReportPreflightViewModel();
            viewModel.SetResult(CreateResult(
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5))),
                2026, 7, isConfirmationMode: false);

            viewModel.SelectedDetailText.Should().Contain("選択すると");
        }

        /// <summary>
        /// 警告を選択すると詳細説明が切り替わること
        /// </summary>
        [Fact]
        public void SelectedDetailText_WhenWarningSelected_ShowsItsDetail()
        {
            var viewModel = new ReportPreflightViewModel();
            viewModel.SetResult(CreateResult(
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5))),
                2026, 7, isConfirmationMode: false);

            viewModel.SelectedWarning = viewModel.Warnings[0];

            viewModel.SelectedDetailText.Should().Be("履歴画面で該当行を修正してください。");
        }

        /// <summary>
        /// 警告を選択したとき SelectedDetailText の PropertyChanged が発火すること
        /// </summary>
        /// <remarks>
        /// SelectedDetailText は算出プロパティのため、SelectedWarning の変更時に明示的に
        /// 通知しないと**値は正しいのに UI が更新されない**（プロパティを直接読むテストでは
        /// 検出できない失敗モード）。バインディングが実際に更新される保証として通知を固定する。
        /// </remarks>
        [Fact]
        public void SelectedWarning_WhenChanged_RaisesPropertyChangedForSelectedDetailText()
        {
            var viewModel = new ReportPreflightViewModel();
            viewModel.SetResult(CreateResult(
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5)),
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 20))),
                2026, 7, isConfirmationMode: false);

            var raised = new List<string>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            viewModel.SelectedWarning = viewModel.Warnings[1];

            raised.Should().Contain(nameof(ReportPreflightViewModel.SelectedDetailText),
                "算出プロパティの通知がないとバインディングが更新されず、詳細欄が古い表示のまま残る");
            raised.Should().Contain(nameof(ReportPreflightViewModel.SelectedWarning));
        }

        /// <summary>
        /// 選択を解除したとき詳細欄が案内文に戻ること（通知込み）
        /// </summary>
        [Fact]
        public void SelectedWarning_WhenClearedToNull_RaisesNotificationAndRestoresPrompt()
        {
            var viewModel = new ReportPreflightViewModel();
            viewModel.SetResult(CreateResult(
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5))),
                2026, 7, isConfirmationMode: false);
            viewModel.SelectedWarning = viewModel.Warnings[0];

            var raised = new List<string>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            viewModel.SelectedWarning = null;

            raised.Should().Contain(nameof(ReportPreflightViewModel.SelectedDetailText));
            viewModel.SelectedDetailText.Should().Contain("選択すると");
        }

        /// <summary>
        /// 再チェック時に前回の選択状態が残らないこと
        /// </summary>
        [Fact]
        public void SetResult_CalledTwice_ClearsPreviousSelection()
        {
            var viewModel = new ReportPreflightViewModel();
            viewModel.SetResult(CreateResult(
                CreateWarning("AAA", "はやかけん 001", new DateTime(2026, 7, 5))),
                2026, 7, isConfirmationMode: false);
            viewModel.SelectedWarning = viewModel.Warnings[0];

            viewModel.SetResult(CreateResult(), 2026, 8, isConfirmationMode: false);

            viewModel.SelectedWarning.Should().BeNull();
            viewModel.Warnings.Should().BeEmpty();
            viewModel.TargetPeriodText.Should().Be("2026年8月分");
        }
    }
}
