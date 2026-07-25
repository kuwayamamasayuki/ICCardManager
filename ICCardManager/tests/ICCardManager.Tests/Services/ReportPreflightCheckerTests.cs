using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services
{
    /// <summary>
    /// 帳票出力前プリフライトチェックの単体テスト（Issue #1688）
    /// </summary>
    /// <remarks>
    /// 判定ロジックは <see cref="ReportPreflightChecker.CheckReportData"/> 以下の internal static
    /// メソッド群に切り出されているため、DB を介さずに境界値を直接検証する。
    /// </remarks>
    public class ReportPreflightCheckerTests
    {
        private const string TestCardIdm = "0123456789ABCDEF";

        #region テストデータ構築ヘルパー

        private static IcCard CreateCard() => new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001"
        };

        /// <summary>
        /// 整合の取れた 2026年7月分の帳票データを生成する（各テストで一部だけ壊して使う）
        /// </summary>
        /// <remarks>
        /// 前月末残高 3,000円 → チャージ +1,000円（残高 4,000円）→ 利用 -500円（残高 3,500円）。
        /// 月計: 受入 1,000 / 払出 500、累計: 受入 9,000 / 払出 5,500 / 残額 3,500。
        /// </remarks>
        private static MonthlyReportData CreateConsistentJulyData() => new MonthlyReportData
        {
            Card = CreateCard(),
            Year = 2026,
            Month = 7,
            PrecedingBalance = 3000,
            Carryover = new CarryoverRowData
            {
                Date = new DateTime(2026, 7, 1),
                Summary = SummaryGenerator.GetCarryoverFromPreviousMonthSummary(6),
                Income = null,
                Balance = 3000
            },
            Ledgers = new List<Ledger>
            {
                new Ledger { Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 7, 5), Summary = "役務費によりチャージ", Income = 1000, Expense = 0, Balance = 4000 },
                new Ledger { Id = 2, CardIdm = TestCardIdm, Date = new DateTime(2026, 7, 15), Summary = "鉄道（博多～天神）", Income = 0, Expense = 500, Balance = 3500 }
            },
            MonthlyTotal = new ReportTotalData { Label = "7月計", Income = 1000, Expense = 500, Balance = null },
            CumulativeTotal = new ReportTotalData { Label = "累計", Income = 9000, Expense = 5500, Balance = 3500 }
        };

        /// <summary>
        /// 4月分（月計に残額を持ち、累計行を省略する）の整合データを生成する
        /// </summary>
        private static MonthlyReportData CreateConsistentAprilData() => new MonthlyReportData
        {
            Card = CreateCard(),
            Year = 2026,
            Month = 4,
            PrecedingBalance = 5000,
            Carryover = new CarryoverRowData
            {
                Date = new DateTime(2026, 4, 1),
                Summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
                Income = 5000,
                Balance = 5000
            },
            Ledgers = new List<Ledger>
            {
                new Ledger { Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 4, 10), Summary = "鉄道（博多～天神）", Income = 0, Expense = 300, Balance = 4700 }
            },
            // Issue #1494: 4月計の受入には前年度繰越額を含める
            MonthlyTotal = new ReportTotalData { Label = "4月計", Income = 5000, Expense = 300, Balance = 4700 },
            CumulativeTotal = null
        };

        private static Ledger CreateLentRecord(DateTime date) => new Ledger
        {
            Id = 99,
            CardIdm = TestCardIdm,
            Date = date,
            Summary = SummaryGenerator.GetLendingSummary(),
            IsLentRecord = true
        };

        private static ReportPreflightResult Check(MonthlyReportData data, Ledger lentRecord = null)
        {
            var result = new ReportPreflightResult();
            ReportPreflightChecker.CheckReportData(data, lentRecord, result);
            return result;
        }

        #endregion

        #region 未返却検出

        /// <summary>
        /// 対象月より前に貸し出されたまま未返却なら UnreturnedAcrossMonth を報告する
        /// </summary>
        [Fact]
        public void CheckReportData_LentBeforeTargetMonth_ReportsUnreturnedAcrossMonth()
        {
            var result = Check(CreateConsistentJulyData(), CreateLentRecord(new DateTime(2026, 6, 28)));

            result.Warnings.Should().ContainSingle()
                .Which.IssueType.Should().Be(ReportPreflightIssueType.UnreturnedAcrossMonth);
            result.Warnings[0].Date.Should().Be(new DateTime(2026, 6, 28));
            result.Warnings[0].DisplayText.Should().Contain("2026-06-28").And.Contain("7月をまたいで");
        }

        /// <summary>
        /// 対象月内に貸し出されたまま未返却なら LendingRecordInMonth を報告する
        /// </summary>
        [Fact]
        public void CheckReportData_LentWithinTargetMonth_ReportsLendingRecordInMonth()
        {
            var result = Check(CreateConsistentJulyData(), CreateLentRecord(new DateTime(2026, 7, 15)));

            result.Warnings.Should().ContainSingle()
                .Which.IssueType.Should().Be(ReportPreflightIssueType.LendingRecordInMonth);
            result.Warnings[0].DetailText.Should().Contain("帳票から欠落");
        }

        /// <summary>
        /// 対象月末日ちょうどの貸出も対象月内として扱う（境界値）
        /// </summary>
        [Fact]
        public void CheckReportData_LentOnLastDayOfMonth_ReportsLendingRecordInMonth()
        {
            var result = Check(CreateConsistentJulyData(), CreateLentRecord(new DateTime(2026, 7, 31)));

            result.Warnings.Should().ContainSingle()
                .Which.IssueType.Should().Be(ReportPreflightIssueType.LendingRecordInMonth);
        }

        /// <summary>
        /// 対象月より後の貸出は当月帳票に影響しないため報告しない（境界値）
        /// </summary>
        [Fact]
        public void CheckReportData_LentAfterTargetMonth_ReportsNothing()
        {
            var result = Check(CreateConsistentJulyData(), CreateLentRecord(new DateTime(2026, 8, 1)));

            result.Warnings.Should().BeEmpty();
        }

        /// <summary>
        /// 貸出中レコードがなければ未返却の警告は出ない
        /// </summary>
        [Fact]
        public void CheckReportData_NoLentRecord_ReportsNoUnreturnedWarning()
        {
            var result = Check(CreateConsistentJulyData(), lentRecord: null);

            result.Warnings.Should().NotContain(w =>
                w.IssueType == ReportPreflightIssueType.UnreturnedAcrossMonth ||
                w.IssueType == ReportPreflightIssueType.LendingRecordInMonth);
        }

        #endregion

        #region 負残高検出

        /// <summary>
        /// 明細行の残額がマイナスなら NegativeBalance を報告する（該当行の日付・摘要を含む）
        /// </summary>
        [Fact]
        public void CheckReportData_NegativeLedgerBalance_ReportsNegativeBalance()
        {
            var data = CreateConsistentJulyData();
            data.Ledgers[1].Expense = 4120;
            data.Ledgers[1].Balance = -120;
            // 残高チェーンは壊さない（負残高だけを検出させる）
            data.MonthlyTotal.Expense = 4120;
            data.CumulativeTotal.Expense = 9120;
            data.CumulativeTotal.Balance = -120;

            var result = Check(data);

            var negative = result.Warnings.Where(w => w.IssueType == ReportPreflightIssueType.NegativeBalance).ToList();
            negative.Should().HaveCount(2, "明細行と累計行の両方がマイナスのため");
            negative[0].Date.Should().Be(new DateTime(2026, 7, 15));
            negative[0].DisplayText.Should().Contain("鉄道（博多～天神）").And.Contain("-120円");
        }

        /// <summary>
        /// 4月の月計残額がマイナスなら NegativeBalance を報告する
        /// </summary>
        [Fact]
        public void CheckReportData_NegativeMonthlyTotalBalance_ReportsNegativeBalance()
        {
            var data = CreateConsistentAprilData();
            data.MonthlyTotal.Income = 100;
            data.MonthlyTotal.Balance = -200;
            data.Ledgers[0].Expense = 300;
            data.Ledgers[0].Balance = -200;
            data.Carryover.Balance = 100;
            data.PrecedingBalance = 100;

            var result = Check(data);

            result.Warnings.Should().Contain(w =>
                w.IssueType == ReportPreflightIssueType.NegativeBalance && w.RowSummary == "4月計");
        }

        /// <summary>
        /// 残額がすべて0以上なら負残高の警告は出ない（境界値: 残額0）
        /// </summary>
        [Fact]
        public void CheckReportData_ZeroBalance_ReportsNoNegativeBalance()
        {
            var data = CreateConsistentJulyData();
            data.Ledgers[1].Expense = 4000;
            data.Ledgers[1].Balance = 0;
            data.MonthlyTotal.Expense = 4000;
            data.CumulativeTotal.Expense = 9000;
            data.CumulativeTotal.Balance = 0;

            var result = Check(data);

            result.Warnings.Should().NotContain(w => w.IssueType == ReportPreflightIssueType.NegativeBalance);
        }

        #endregion

        #region 繰越不一致検出

        /// <summary>
        /// 繰越額と先頭行の残高チェーンが繋がらなければ CarryoverMismatch を報告する
        /// </summary>
        [Fact]
        public void CheckReportData_CarryoverDoesNotChainToFirstRow_ReportsCarryoverMismatch()
        {
            var data = CreateConsistentJulyData();
            // 繰越 3,000 + 受入 1,000 = 4,000 のはずが 3,900 になっている
            data.Ledgers[0].Balance = 3900;
            data.Ledgers[1].Balance = 3400;
            data.CumulativeTotal.Balance = 3400;

            var result = Check(data);

            var mismatch = result.Warnings.Single(w => w.IssueType == ReportPreflightIssueType.CarryoverMismatch);
            mismatch.DisplayText.Should().Contain("期待 4,000円").And.Contain("実際 3,900円");
            mismatch.LedgerId.Should().Be(1);
        }

        /// <summary>
        /// 繰越額と先頭行が整合していれば警告を出さない
        /// </summary>
        [Fact]
        public void CheckReportData_CarryoverChainsCorrectly_ReportsNoCarryoverMismatch()
        {
            var result = Check(CreateConsistentJulyData());

            result.Warnings.Should().NotContain(w => w.IssueType == ReportPreflightIssueType.CarryoverMismatch);
        }

        /// <summary>
        /// 紙出納簿移行カード（Issue #510）の「○月から繰越」が先頭行なら繰越チェックをスキップする
        /// </summary>
        /// <remarks>
        /// 当該レコードは Income=残高 で保存されるため、通常の残高チェーン式では必ず不一致になる。
        /// これを警告すると移行カードで常に誤検知が出る。
        /// </remarks>
        [Fact]
        public void CheckReportData_FirstRowIsMidYearCarryover_SkipsCarryoverMismatch()
        {
            var data = CreateConsistentJulyData();
            data.Ledgers[0] = new Ledger
            {
                Id = 1,
                CardIdm = TestCardIdm,
                Date = new DateTime(2026, 7, 1),
                Summary = SummaryGenerator.GetMidYearCarryoverSummary(6),
                Income = 3000,
                Expense = 0,
                Balance = 3000
            };
            data.Ledgers[1].Balance = 2500;
            data.Ledgers[1].Expense = 500;
            data.CumulativeTotal.Balance = 2500;
            data.CumulativeTotal.Income = 8000;

            var result = Check(data);

            result.Warnings.Should().NotContain(w => w.IssueType == ReportPreflightIssueType.CarryoverMismatch);
        }

        /// <summary>
        /// 繰越行がない月（新規購入カード）では繰越チェックをスキップする
        /// </summary>
        [Fact]
        public void CheckReportData_NoCarryoverRow_SkipsCarryoverMismatch()
        {
            var data = CreateConsistentJulyData();
            data.Carryover = null;
            data.PrecedingBalance = null;

            var result = Check(data);

            result.Warnings.Should().NotContain(w => w.IssueType == ReportPreflightIssueType.CarryoverMismatch);
        }

        #endregion

        #region 受入 − 払出 = 残額 の検算

        /// <summary>
        /// 累計行で「受入 − 払出 = 残額」が成立しなければ TotalMismatch を報告する
        /// </summary>
        [Fact]
        public void CheckReportData_CumulativeTotalDoesNotBalance_ReportsTotalMismatch()
        {
            var data = CreateConsistentJulyData();
            data.CumulativeTotal.Balance = 3400;   // 9,000 − 5,500 = 3,500 のはず

            var result = Check(data);

            var mismatch = result.Warnings.Single(w => w.IssueType == ReportPreflightIssueType.TotalMismatch);
            mismatch.RowSummary.Should().Be("累計");
            mismatch.DisplayText.Should().Contain("受入 9,000円").And.Contain("残額 3,400円");
        }

        /// <summary>
        /// 4月の月計で「受入 − 払出 = 残額」が成立しなければ TotalMismatch を報告する
        /// </summary>
        [Fact]
        public void CheckReportData_AprilMonthlyTotalDoesNotBalance_ReportsTotalMismatch()
        {
            var data = CreateConsistentAprilData();
            data.MonthlyTotal.Balance = 4600;      // 5,000 − 300 = 4,700 のはず

            var result = Check(data);

            result.Warnings.Should().ContainSingle(w => w.IssueType == ReportPreflightIssueType.TotalMismatch)
                .Which.RowSummary.Should().Be("4月計");
        }

        /// <summary>
        /// 5月以降の月計は残額が帳票に出ないため、前月末残高からの残高チェーンで検算する
        /// </summary>
        [Fact]
        public void CheckReportData_MonthlyChainDoesNotBalance_ReportsTotalMismatch()
        {
            var data = CreateConsistentJulyData();
            // 前月末残高 3,000 + 受入 1,000 − 払出 500 = 3,500 のはずが、月計の受入が 900 になっている
            data.MonthlyTotal.Income = 900;

            var result = Check(data);

            var mismatch = result.Warnings.Single(w => w.IssueType == ReportPreflightIssueType.TotalMismatch);
            mismatch.RowSummary.Should().Be("7月計");
            mismatch.DisplayText.Should().Contain("前月末残高 3,000円").And.Contain("月末残額 3,500円");
        }

        /// <summary>
        /// 前月末残高が不明（新規購入カード）なら月計の検算はスキップする
        /// </summary>
        [Fact]
        public void CheckReportData_NoPrecedingBalance_SkipsMonthlyChainCheck()
        {
            var data = CreateConsistentJulyData();
            data.PrecedingBalance = null;
            data.Carryover = null;
            data.MonthlyTotal.Income = 900;   // 検算すれば不一致になる値

            var result = Check(data);

            result.Warnings.Should().NotContain(w => w.IssueType == ReportPreflightIssueType.TotalMismatch);
        }

        /// <summary>
        /// 紙出納簿移行月（「○月から繰越」を含む月）は月計の検算をスキップする
        /// </summary>
        /// <remarks>
        /// 当該レコードの受入は集計から除外される一方で残高チェーンには寄与するため、
        /// 「前月末残高 + 受入 − 払出 = 月末残高」が構造的に成立しない（Issue #510 / #1494）。
        /// </remarks>
        [Fact]
        public void CheckReportData_MonthContainsMidYearCarryover_SkipsMonthlyChainCheck()
        {
            var data = CreateConsistentJulyData();
            data.Ledgers.Insert(0, new Ledger
            {
                Id = 10,
                CardIdm = TestCardIdm,
                Date = new DateTime(2026, 7, 1),
                Summary = SummaryGenerator.GetMidYearCarryoverSummary(6),
                Income = 3000,
                Expense = 0,
                Balance = 3000
            });

            var result = Check(data);

            result.Warnings.Should().NotContain(w =>
                w.IssueType == ReportPreflightIssueType.TotalMismatch && w.RowSummary == "7月計");
        }

        /// <summary>
        /// 明細行が0件の月は月末残高が確定しないため月計の検算をスキップする
        /// </summary>
        [Fact]
        public void CheckReportData_NoLedgerRows_SkipsMonthlyChainCheck()
        {
            var data = CreateConsistentJulyData();
            data.Ledgers = new List<Ledger>();
            data.MonthlyTotal.Income = 0;
            data.MonthlyTotal.Expense = 0;
            data.CumulativeTotal.Income = 8000;
            data.CumulativeTotal.Expense = 4500;
            data.CumulativeTotal.Balance = 3500;

            var result = Check(data);

            result.Warnings.Should().BeEmpty();
        }

        /// <summary>
        /// 紙出納簿移行カードの累計（紙時代の累計を加算した形）では誤検知しない
        /// </summary>
        /// <remarks>
        /// 設計書 §4.5 の数値例: 紙時代 受入 10,000 / 払出 7,000（移行時残高 3,000）、
        /// 7月に +1,000 / −500 → 累計 受入 11,000 / 払出 7,500 / 残額 3,500。
        /// </remarks>
        [Fact]
        public void CheckReportData_MidYearMigratedCardCumulative_ReportsNothing()
        {
            var data = new MonthlyReportData
            {
                Card = CreateCard(),
                Year = 2026,
                Month = 7,
                PrecedingBalance = null,
                Carryover = null,
                Ledgers = new List<Ledger>
                {
                    new Ledger { Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 7, 1), Summary = SummaryGenerator.GetMidYearCarryoverSummary(6), Income = 3000, Expense = 0, Balance = 3000 },
                    new Ledger { Id = 2, CardIdm = TestCardIdm, Date = new DateTime(2026, 7, 10), Summary = "役務費によりチャージ", Income = 1000, Expense = 0, Balance = 4000 },
                    new Ledger { Id = 3, CardIdm = TestCardIdm, Date = new DateTime(2026, 7, 20), Summary = "鉄道（博多～天神）", Income = 0, Expense = 500, Balance = 3500 }
                },
                MonthlyTotal = new ReportTotalData { Label = "7月計", Income = 1000, Expense = 500, Balance = null },
                CumulativeTotal = new ReportTotalData { Label = "累計", Income = 11000, Expense = 7500, Balance = 3500 }
            };

            var result = Check(data);

            result.Warnings.Should().BeEmpty();
        }

        #endregion

        #region 正常系・メッセージ品質

        /// <summary>
        /// 整合の取れた通常カードでは警告が1件も出ない
        /// </summary>
        [Fact]
        public void CheckReportData_ConsistentData_ReportsNothing()
        {
            Check(CreateConsistentJulyData()).Warnings.Should().BeEmpty();
            Check(CreateConsistentAprilData()).Warnings.Should().BeEmpty();
        }

        /// <summary>
        /// すべての警告メッセージが「何が・なぜ・どうすれば」の3要素を満たす
        /// （.claude/rules/error-messages.md の品質基準）
        /// </summary>
        [Fact]
        public void AllWarnings_SatisfyErrorMessageQualityCriteria()
        {
            var warnings = new List<ReportPreflightWarning>();

            // 5種別すべてを発生させる
            var unreturned = CreateConsistentJulyData();
            warnings.AddRange(Check(unreturned, CreateLentRecord(new DateTime(2026, 6, 28))).Warnings);
            warnings.AddRange(Check(CreateConsistentJulyData(), CreateLentRecord(new DateTime(2026, 7, 15))).Warnings);

            var negative = CreateConsistentJulyData();
            negative.Ledgers[1].Balance = -120;
            negative.CumulativeTotal.Balance = -120;
            warnings.AddRange(Check(negative).Warnings.Where(w => w.IssueType == ReportPreflightIssueType.NegativeBalance));

            var carryover = CreateConsistentJulyData();
            carryover.Ledgers[0].Balance = 3900;
            warnings.AddRange(Check(carryover).Warnings.Where(w => w.IssueType == ReportPreflightIssueType.CarryoverMismatch));

            var total = CreateConsistentJulyData();
            total.CumulativeTotal.Balance = 3400;
            warnings.AddRange(Check(total).Warnings.Where(w => w.IssueType == ReportPreflightIssueType.TotalMismatch));

            warnings.Select(w => w.IssueType).Distinct().Should().HaveCount(5, "5種別すべてを網羅していること");

            foreach (var warning in warnings)
            {
                warning.DisplayText.Length.Should().BeGreaterOrEqualTo(20,
                    $"「{warning.DisplayText}」は情報が不足している");
                warning.DisplayText.Should().Contain("はやかけん 001", "どのカードの問題かが分かること");
                warning.DetailText.Should().EndWith("してください。",
                    $"「{warning.DetailText}」は行動指示で終わっていない");
                warning.CardIdm.Should().Be(TestCardIdm);
            }
        }

        #endregion

        #region CheckAsync（DB経路）

        /// <summary>
        /// 複数カードを走査して警告を統合する
        /// </summary>
        [Fact]
        public async Task CheckAsync_MultipleCards_AggregatesWarningsFromAllCards()
        {
            const string secondIdm = "FEDCBA9876543210";

            var firstData = CreateConsistentJulyData();
            firstData.CumulativeTotal.Balance = 3400;    // TotalMismatch

            var secondData = CreateConsistentJulyData();
            secondData.Card = new IcCard { CardIdm = secondIdm, CardType = "nimoca", CardNumber = "002" };
            secondData.Ledgers[1].Balance = -120;        // NegativeBalance
            secondData.CumulativeTotal.Balance = -120;

            var builderMock = new Mock<IReportDataBuilder>();
            builderMock.Setup(b => b.BuildAsync(TestCardIdm, 2026, 7)).ReturnsAsync(firstData);
            builderMock.Setup(b => b.BuildAsync(secondIdm, 2026, 7)).ReturnsAsync(secondData);

            var ledgerRepositoryMock = new Mock<ILedgerRepository>();
            ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync())
                .ReturnsAsync(new List<Ledger> { CreateLentRecord(new DateTime(2026, 6, 28)) });

            var checker = new ReportPreflightChecker(builderMock.Object, ledgerRepositoryMock.Object);

            var result = await checker.CheckAsync(new[] { TestCardIdm, secondIdm }, 2026, 7);

            result.HasWarnings.Should().BeTrue();
            result.Warnings.Should().Contain(w => w.CardIdm == TestCardIdm && w.IssueType == ReportPreflightIssueType.UnreturnedAcrossMonth);
            result.Warnings.Should().Contain(w => w.CardIdm == TestCardIdm && w.IssueType == ReportPreflightIssueType.TotalMismatch);
            result.Warnings.Should().Contain(w => w.CardIdm == secondIdm && w.IssueType == ReportPreflightIssueType.NegativeBalance);
            // 貸出中レコードは1枚目のカードのものだけなので、2枚目に未返却警告は付かない
            result.Warnings.Should().NotContain(w => w.CardIdm == secondIdm && w.IssueType == ReportPreflightIssueType.UnreturnedAcrossMonth);
        }

        /// <summary>
        /// 帳票データが構築できないカード（削除済み等）は例外を投げずにスキップする
        /// </summary>
        [Fact]
        public async Task CheckAsync_BuildReturnsNull_SkipsCardWithoutThrowing()
        {
            var builderMock = new Mock<IReportDataBuilder>();
            builderMock.Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync((MonthlyReportData)null);

            var ledgerRepositoryMock = new Mock<ILedgerRepository>();
            ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync()).ReturnsAsync(new List<Ledger>());

            var checker = new ReportPreflightChecker(builderMock.Object, ledgerRepositoryMock.Object);

            var result = await checker.CheckAsync(new[] { TestCardIdm }, 2026, 7);

            result.HasWarnings.Should().BeFalse();
        }

        /// <summary>
        /// カードが1枚も選択されていない場合はDBに問い合わせない
        /// </summary>
        [Fact]
        public async Task CheckAsync_EmptyCardList_DoesNotQueryRepository()
        {
            var builderMock = new Mock<IReportDataBuilder>();
            var ledgerRepositoryMock = new Mock<ILedgerRepository>();

            var checker = new ReportPreflightChecker(builderMock.Object, ledgerRepositoryMock.Object);

            var result = await checker.CheckAsync(new string[0], 2026, 7);

            result.HasWarnings.Should().BeFalse();
            ledgerRepositoryMock.Verify(r => r.GetAllLentRecordsAsync(), Times.Never);
            builderMock.Verify(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        /// <summary>
        /// 同じIDmが重複して渡されても1回だけチェックする
        /// </summary>
        [Fact]
        public async Task CheckAsync_DuplicateCardIdms_ChecksEachCardOnce()
        {
            var builderMock = new Mock<IReportDataBuilder>();
            builderMock.Setup(b => b.BuildAsync(TestCardIdm, 2026, 7))
                .ReturnsAsync(CreateConsistentJulyData());

            var ledgerRepositoryMock = new Mock<ILedgerRepository>();
            ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync()).ReturnsAsync(new List<Ledger>());

            var checker = new ReportPreflightChecker(builderMock.Object, ledgerRepositoryMock.Object);

            await checker.CheckAsync(new[] { TestCardIdm, TestCardIdm }, 2026, 7);

            builderMock.Verify(b => b.BuildAsync(TestCardIdm, 2026, 7), Times.Once);
        }

        #endregion
    }
}
