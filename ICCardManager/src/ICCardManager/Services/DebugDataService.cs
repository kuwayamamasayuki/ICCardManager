using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
#if DEBUG
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
/// <summary>
    /// DEBUGビルド時のテストデータ管理サービス
    /// </summary>
    public class DebugDataService
    {
        private readonly IStaffRepository _staffRepository;
        private readonly ICardRepository _cardRepository;
        private readonly ILedgerRepository _ledgerRepository;

        /// <summary>
        /// テストデータの初期残高（全カード共通）
        /// </summary>
        internal const int InitialBalance = 50000;

        /// <summary>
        /// テスト職員データ
        /// </summary>
        public static readonly Staff[] TestStaffList =
        {
            new() { StaffIdm = "FFFF000000000001", Name = "山田太郎", Number = "001", Note = "テスト職員1（管理者）" },
            new() { StaffIdm = "FFFF000000000002", Name = "鈴木花子", Number = "002", Note = "テスト職員2（一般）" },
            new() { StaffIdm = "FFFF000000000003", Name = "佐藤一郎", Number = "003", Note = "テスト職員3（一般）" },
            new() { StaffIdm = "FFFF000000000004", Name = "田中美咲", Number = "004", Note = "テスト職員4（一般）" },
            new() { StaffIdm = "FFFF000000000005", Name = "伊藤健二", Number = "005", Note = "テスト職員5（新人）" },
        };

        /// <summary>
        /// テストカードデータ
        /// </summary>
        public static readonly IcCard[] TestCardList =
        {
            new() { CardIdm = "07FE112233445566", CardType = "はやかけん", CardNumber = "H-001", Note = "テストカード1" },
            new() { CardIdm = "05FE112233445567", CardType = "nimoca", CardNumber = "N-001", Note = "テストカード2" },
            new() { CardIdm = "06FE112233445568", CardType = "SUGOCA", CardNumber = "S-001", Note = "テストカード3" },
            new() { CardIdm = "01FE112233445569", CardType = "Suica", CardNumber = "Su-001", Note = "テストカード4（関東）" },
            new() { CardIdm = "07FE112233445570", CardType = "はやかけん", CardNumber = "H-002", Note = "テストカード5" },
            new() { CardIdm = "05FE112233445571", CardType = "nimoca", CardNumber = "N-002", Note = "テストカード6" },
        };

        /// <summary>
        /// サンプル駅名データ（福岡周辺）
        /// </summary>
        private static readonly string[] SampleStations =
        {
            "博多", "天神", "薬院", "大橋", "春日", "二日市", "久留米",
            "福岡空港", "貝塚", "箱崎", "千早", "香椎", "新宮", "古賀"
        };

        public DebugDataService(
            IStaffRepository staffRepository,
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository)
        {
            _staffRepository = staffRepository;
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
        }

        /// <summary>
        /// 全テストデータを登録
        /// </summary>
        public async Task RegisterAllTestDataAsync()
        {
            await RegisterTestStaffAsync();
            await RegisterTestCardsAsync();
            var finalBalances = await RegisterSampleHistoryAsync();
            await RegisterSpecialScenariosAsync(finalBalances);
        }

        /// <summary>
        /// テスト職員を登録
        /// </summary>
        public async Task RegisterTestStaffAsync()
        {
            foreach (var staff in TestStaffList)
            {
                var existing = await _staffRepository.GetByIdmAsync(staff.StaffIdm);
                if (existing == null)
                {
                    await _staffRepository.InsertAsync(staff);
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] テスト職員登録: {staff.Name} ({staff.StaffIdm})");
                }
            }
        }

        /// <summary>
        /// テストカードを登録
        /// </summary>
        public async Task RegisterTestCardsAsync()
        {
            foreach (var card in TestCardList)
            {
                var existing = await _cardRepository.GetByIdmAsync(card.CardIdm);
                if (existing == null)
                {
                    await _cardRepository.InsertAsync(card);
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] テストカード登録: {card.CardType} {card.CardNumber} ({card.CardIdm})");
                }
            }
        }

        /// <summary>
        /// サンプル履歴データを登録
        /// </summary>
        /// <remarks>
        /// 各月50件以上のレコードを生成（ページングテスト用）
        /// - 平日は毎日2-3件のレコードを生成
        /// - 約22平日/月 × 2-3件 = 50-70件/月
        /// </remarks>
        /// <returns>各カードIDmをキーとした最終残高のDictionary</returns>
        public async Task<Dictionary<string, int>> RegisterSampleHistoryAsync()
        {
            var finalBalances = new Dictionary<string, int>();
            var random = new Random(42); // 再現性のためシード固定
            var today = DateTime.Now.Date;

            // 各カードに対してサンプル履歴を登録
            // Su-001は新規購入～払い戻しのライフサイクルで個別生成するため除外
            var cardsForSampleHistory = TestCardList.Where(c => c.CardIdm != TestCardList[3].CardIdm);
            foreach (var card in cardsForSampleHistory)
            {
                // 既存の履歴があるかチェック
                var existingHistory = await _ledgerRepository.GetByMonthAsync(card.CardIdm, today.Year, today.Month);
                if (existingHistory.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] 履歴が既に存在: {card.CardNumber}");
                    continue;
                }

                var balance = InitialBalance; // 初期残高（長期間・大量データ用に増額）
                var staffName = TestStaffList[random.Next(TestStaffList.Length)].Name;

                // H-001（特殊シナリオ対象カード）の場合、特殊シナリオが配置される
                // 週末日以降の通常データ生成を停止して残高チェーンの連続性を保つ
                var cutoffDate = (card.CardIdm == TestCardList[0].CardIdm)
                    ? FindNthWeekendDayBefore(today, 6) // 最古の特殊シナリオ日
                    : (DateTime?)null;

                // 過去180日分（約6ヶ月）のサンプル履歴を生成
                // 各月50件以上のデータでページングテストが可能
                for (int daysAgo = 180; daysAgo >= 0; daysAgo--)
                {
                    var date = today.AddDays(-daysAgo);

                    // 土日はスキップ（平日のみ利用）
                    if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                        continue;

                    // H-001: 特殊シナリオ日以降は生成停止（残高チェーン連続性のため）
                    if (cutoffDate.HasValue && date >= cutoffDate.Value)
                        break;

                    // 残高が少ない場合はチャージ
                    if (balance < 3000)
                    {
                        var chargeAmount = random.Next(3, 6) * 1000; // 3000, 4000, 5000円
                        balance += chargeAmount;

                        var chargeLedger = new Ledger
                        {
                            CardIdm = card.CardIdm,
                            Date = date,
                            Summary = SummaryGenerator.GetChargeSummary(),
                            Income = chargeAmount,
                            Expense = 0,
                            Balance = balance,
                            StaffName = staffName,
                            Note = "テストデータ"
                        };
                        var chargeLedgerId = await _ledgerRepository.InsertAsync(chargeLedger);

                        // チャージの詳細レコードを作成
                        var chargeDetail = new LedgerDetail
                        {
                            LedgerId = chargeLedgerId,
                            UseDate = date.AddHours(7).AddMinutes(random.Next(60)),
                            Amount = chargeAmount,
                            Balance = balance,
                            IsCharge = true,
                            IsBus = false
                        };
                        await _ledgerRepository.InsertDetailAsync(chargeDetail);
                    }

                    // 1件目: 朝の通勤（往復）
                    var fromIdx = random.Next(SampleStations.Length);
                    var toIdx = (fromIdx + random.Next(1, 5)) % SampleStations.Length;
                    var fare1 = 200 + random.Next(10) * 30; // 200-470円

                    balance -= fare1 * 2; // 往復分

                    var usageLedger1 = new Ledger
                    {
                        CardIdm = card.CardIdm,
                        Date = date,
                        Summary = $"鉄道（{SampleStations[fromIdx]}～{SampleStations[toIdx]} 往復）",
                        Income = 0,
                        Expense = fare1 * 2,
                        Balance = balance,
                        StaffName = staffName,
                        Note = "テストデータ"
                    };
                    var usageLedgerId1 = await _ledgerRepository.InsertAsync(usageLedger1);

                    // 往路の詳細レコード
                    var outboundBalance1 = balance + fare1;
                    var outboundDetail1 = new LedgerDetail
                    {
                        LedgerId = usageLedgerId1,
                        UseDate = date.AddHours(8).AddMinutes(random.Next(60)),
                        EntryStation = SampleStations[fromIdx],
                        ExitStation = SampleStations[toIdx],
                        Amount = fare1,
                        Balance = outboundBalance1,
                        IsCharge = false,
                        IsBus = false
                    };
                    await _ledgerRepository.InsertDetailAsync(outboundDetail1);

                    // 復路の詳細レコード
                    var returnDetail1 = new LedgerDetail
                    {
                        LedgerId = usageLedgerId1,
                        UseDate = date.AddHours(18).AddMinutes(random.Next(60)),
                        EntryStation = SampleStations[toIdx],
                        ExitStation = SampleStations[fromIdx],
                        Amount = fare1,
                        Balance = balance,
                        IsCharge = false,
                        IsBus = false
                    };
                    await _ledgerRepository.InsertDetailAsync(returnDetail1);

                    // 2件目: 昼の外出（片道）
                    var fromIdx2 = random.Next(SampleStations.Length);
                    var toIdx2 = (fromIdx2 + random.Next(1, 3)) % SampleStations.Length;
                    var fare2 = 200 + random.Next(5) * 30; // 200-320円

                    balance -= fare2;

                    var usageLedger2 = new Ledger
                    {
                        CardIdm = card.CardIdm,
                        Date = date,
                        Summary = $"鉄道（{SampleStations[fromIdx2]}～{SampleStations[toIdx2]}）",
                        Income = 0,
                        Expense = fare2,
                        Balance = balance,
                        StaffName = staffName,
                        Note = "テストデータ"
                    };
                    var usageLedgerId2 = await _ledgerRepository.InsertAsync(usageLedger2);

                    // 片道の詳細レコード
                    var onewayDetail = new LedgerDetail
                    {
                        LedgerId = usageLedgerId2,
                        UseDate = date.AddHours(12).AddMinutes(random.Next(60)),
                        EntryStation = SampleStations[fromIdx2],
                        ExitStation = SampleStations[toIdx2],
                        Amount = fare2,
                        Balance = balance,
                        IsCharge = false,
                        IsBus = false
                    };
                    await _ledgerRepository.InsertDetailAsync(onewayDetail);

                    // 3件目: 50%の確率でバス利用も追加
                    if (random.Next(100) < 50)
                    {
                        var busFare = 200 + random.Next(3) * 30; // 200-260円
                        balance -= busFare;

                        var busLedger = new Ledger
                        {
                            CardIdm = card.CardIdm,
                            Date = date,
                            Summary = "バス（★）",
                            Income = 0,
                            Expense = busFare,
                            Balance = balance,
                            StaffName = staffName,
                            Note = "テストデータ"
                        };
                        var busLedgerId = await _ledgerRepository.InsertAsync(busLedger);

                        // バス利用の詳細レコード
                        var busDetail = new LedgerDetail
                        {
                            LedgerId = busLedgerId,
                            UseDate = date.AddHours(15).AddMinutes(random.Next(60)),
                            Amount = busFare,
                            Balance = balance,
                            IsCharge = false,
                            IsBus = true
                        };
                        await _ledgerRepository.InsertDetailAsync(busDetail);
                    }
                }

                finalBalances[card.CardIdm] = balance;
                System.Diagnostics.Debug.WriteLine($"[DEBUG] サンプル履歴登録完了: {card.CardNumber} (残高: {balance})");
            }

            return finalBalances;
        }

        /// <summary>
        /// 特殊パターンのテストデータを登録
        /// </summary>
        /// <remarks>
        /// 以下のパターンを追加:
        /// - 2線乗り継ぎ / 3線乗り継ぎ（GroupId使用）
        /// - ポイント還元
        /// - 不足分のみチャージ
        /// - 年度繰越（前年度からの繰越 / 次年度への繰越）
        /// - 新規購入 / 払い戻し
        ///
        /// 通常データは平日のみ生成されるため、特殊シナリオは週末日に配置して
        /// 日付重複による残高チェーン不整合を回避する。
        /// </remarks>
        /// <param name="finalBalances">RegisterSampleHistoryAsyncから受け取った各カードの最終残高</param>
        public async Task RegisterSpecialScenariosAsync(Dictionary<string, int> finalBalances)
        {
            var today = DateTime.Now.Date;
            var staffName = TestStaffList[0].Name; // 山田太郎

            // ── カード H-001: 乗り継ぎ・ポイント還元・不足分チャージ ──
            var h001Balance = finalBalances.TryGetValue(TestCardList[0].CardIdm, out var bal) ? bal : 10000;
            await RegisterTransferAndSpecialUsageAsync(TestCardList[0].CardIdm, today, staffName, h001Balance);

            // ── カード N-002: 年度繰越パターン ──
            await RegisterFiscalYearCarryoverAsync(TestCardList[5].CardIdm, today, staffName);

            // ── カード Su-001: 新規購入・払い戻し ──
            await RegisterPurchaseAndRefundAsync(TestCardList[3].CardIdm, today, staffName);

            System.Diagnostics.Debug.WriteLine("[DEBUG] 特殊パターンテストデータ登録完了");
        }

        /// <summary>
        /// 乗り継ぎ・ポイント還元・不足分チャージのパターンを登録
        /// </summary>
        /// <remarks>
        /// 特殊シナリオは週末日に配置して通常データ（平日のみ）との日付重複を回避。
        /// 残高チェーンは通常データの最終残高から連続するように構築する。
        /// </remarks>
        private async Task RegisterTransferAndSpecialUsageAsync(string cardIdm, DateTime today, string staffName, int currentBalance)
        {
            // 既に特殊パターンが登録済みかチェック（ポイント還元レコードの存在で判定）
            // 当月と前月の両方をチェック（月跨ぎ対応）
            var recentHistory = await _ledgerRepository.GetByMonthAsync(cardIdm, today.Year, today.Month);
            var prevMonth = today.AddMonths(-1);
            var prevHistory = await _ledgerRepository.GetByMonthAsync(cardIdm, prevMonth.Year, prevMonth.Month);
            var allRecent = recentHistory.Concat(prevHistory);
            if (allRecent.Any(l => l.Summary == SummaryGenerator.GetPointRedemptionSummary()))
            {
                System.Diagnostics.Debug.WriteLine("[DEBUG] 特殊パターン(H-001)は登録済み");
                return;
            }

            var balance = currentBalance;

            // 週末日を6つ取得（n=6が最古、n=1が最新）
            var weekendDates = new DateTime[6];
            for (int i = 0; i < 6; i++)
            {
                weekendDates[i] = FindNthWeekendDayBefore(today, 6 - i); // [0]=最古(n=6), [5]=最新(n=1)
            }

            // ── 週末日[0] (n=6): 2線乗り継ぎ（博多→天神→薬院） ──
            var transferFare1A = 210;
            var transferFare1B = 200;
            var transferTotal1 = transferFare1A + transferFare1B;
            balance -= transferTotal1;

            var transfer1Ledger = new Ledger
            {
                CardIdm = cardIdm,
                Date = weekendDates[0],
                Summary = "鉄道（博多～薬院）",
                Income = 0,
                Expense = transferTotal1,
                Balance = balance,
                StaffName = staffName,
                Note = "テストデータ（2線乗り継ぎ）"
            };
            var transfer1Id = await _ledgerRepository.InsertAsync(transfer1Ledger);

            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = transfer1Id,
                UseDate = weekendDates[0].AddHours(8).AddMinutes(30),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = transferFare1A,
                Balance = balance + transferFare1B,
                IsCharge = false,
                IsBus = false,
                GroupId = 1,
                SequenceNumber = 1
            });
            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = transfer1Id,
                UseDate = weekendDates[0].AddHours(8).AddMinutes(45),
                EntryStation = "天神",
                ExitStation = "薬院",
                Amount = transferFare1B,
                Balance = balance,
                IsCharge = false,
                IsBus = false,
                GroupId = 1,
                SequenceNumber = 2
            });

            // ── 週末日[1] (n=5): 3線乗り継ぎ（博多→天神→薬院→大橋） ──
            var transferFare2A = 210;
            var transferFare2B = 200;
            var transferFare2C = 200;
            var transferTotal2 = transferFare2A + transferFare2B + transferFare2C;
            balance -= transferTotal2;

            var transfer2Ledger = new Ledger
            {
                CardIdm = cardIdm,
                Date = weekendDates[1],
                Summary = "鉄道（博多～大橋）",
                Income = 0,
                Expense = transferTotal2,
                Balance = balance,
                StaffName = staffName,
                Note = "テストデータ（3線乗り継ぎ）"
            };
            var transfer2Id = await _ledgerRepository.InsertAsync(transfer2Ledger);

            var runningBalance2 = balance + transferFare2B + transferFare2C;
            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = transfer2Id,
                UseDate = weekendDates[1].AddHours(9),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = transferFare2A,
                Balance = runningBalance2,
                IsCharge = false,
                IsBus = false,
                GroupId = 2,
                SequenceNumber = 1
            });
            runningBalance2 -= transferFare2B;
            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = transfer2Id,
                UseDate = weekendDates[1].AddHours(9).AddMinutes(15),
                EntryStation = "天神",
                ExitStation = "薬院",
                Amount = transferFare2B,
                Balance = runningBalance2,
                IsCharge = false,
                IsBus = false,
                GroupId = 2,
                SequenceNumber = 2
            });
            runningBalance2 -= transferFare2C;
            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = transfer2Id,
                UseDate = weekendDates[1].AddHours(9).AddMinutes(30),
                EntryStation = "薬院",
                ExitStation = "大橋",
                Amount = transferFare2C,
                Balance = runningBalance2,
                IsCharge = false,
                IsBus = false,
                GroupId = 2,
                SequenceNumber = 3
            });

            // ── 週末日[2] (n=4): ポイント還元 ──
            var pointAmount = 500;
            balance += pointAmount;

            var pointLedger = new Ledger
            {
                CardIdm = cardIdm,
                Date = weekendDates[2],
                Summary = SummaryGenerator.GetPointRedemptionSummary(),
                Income = pointAmount,
                Expense = 0,
                Balance = balance,
                StaffName = staffName,
                Note = "テストデータ（ポイント還元）"
            };
            var pointLedgerId = await _ledgerRepository.InsertAsync(pointLedger);

            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = pointLedgerId,
                UseDate = weekendDates[2].AddHours(10),
                Amount = pointAmount,
                Balance = balance,
                IsCharge = false,
                IsPointRedemption = true,
                IsBus = false
            });

            // ── 週末日[3] (n=3): 残高調整（残高を200円まで消化） ──
            var targetBalance = 200;
            var drainExpense = balance - targetBalance;
            if (drainExpense > 0)
            {
                balance = targetBalance;

                var drainLedger = new Ledger
                {
                    CardIdm = cardIdm,
                    Date = weekendDates[3],
                    Summary = "鉄道（博多～久留米）",
                    Income = 0,
                    Expense = drainExpense,
                    Balance = balance,
                    StaffName = staffName,
                    Note = "テストデータ（残高調整用）"
                };
                var drainLedgerId = await _ledgerRepository.InsertAsync(drainLedger);

                await _ledgerRepository.InsertDetailAsync(new LedgerDetail
                {
                    LedgerId = drainLedgerId,
                    UseDate = weekendDates[3].AddHours(11),
                    EntryStation = "博多",
                    ExitStation = "久留米",
                    Amount = drainExpense,
                    Balance = balance,
                    IsCharge = false,
                    IsBus = false
                });
            }

            // ── 週末日[4] (n=2): 不足分のみチャージ（残高200円で340円の利用） ──
            var originalBalance = balance; // 実際のrunning balance（200円）
            var totalFare = 340;
            var shortfall = totalFare - originalBalance;

            var insufficientLedger = new Ledger
            {
                CardIdm = cardIdm,
                Date = weekendDates[4],
                Summary = "鉄道（博多～春日）",
                Income = 0,
                Expense = originalBalance,
                Balance = 0,
                StaffName = staffName,
                Note = SummaryGenerator.GetInsufficientBalanceNote(totalFare, shortfall)
            };
            var insufficientLedgerId = await _ledgerRepository.InsertAsync(insufficientLedger);

            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = insufficientLedgerId,
                UseDate = weekendDates[4].AddHours(14),
                EntryStation = "博多",
                ExitStation = "春日",
                Amount = totalFare,
                Balance = 0,
                IsCharge = false,
                IsBus = false
            });
            balance = 0;

            // ── 週末日[5] (n=1): チャージして残高回復 ──
            var chargeAmount = 5000;
            balance += chargeAmount;

            var recoveryCharge = new Ledger
            {
                CardIdm = cardIdm,
                Date = weekendDates[5],
                Summary = SummaryGenerator.GetChargeSummary(),
                Income = chargeAmount,
                Expense = 0,
                Balance = balance,
                StaffName = staffName,
                Note = "テストデータ（残高回復チャージ）"
            };
            var recoveryChargeId = await _ledgerRepository.InsertAsync(recoveryCharge);

            await _ledgerRepository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = recoveryChargeId,
                UseDate = weekendDates[5].AddHours(8),
                Amount = chargeAmount,
                Balance = balance,
                IsCharge = true,
                IsBus = false
            });

            System.Diagnostics.Debug.WriteLine("[DEBUG] 特殊パターン(H-001: 乗り継ぎ・ポイント還元・不足分チャージ)登録完了");
        }

        /// <summary>
        /// 年度繰越パターンを登録
        /// </summary>
        private async Task RegisterFiscalYearCarryoverAsync(string cardIdm, DateTime today, string staffName)
        {
            // 直近の年度境界を計算（日本の会計年度: 4月～翌年3月）
            var fiscalYearStart = today.Month >= 4
                ? new DateTime(today.Year, 4, 1)
                : new DateTime(today.Year - 1, 4, 1);
            var previousFiscalYearEnd = fiscalYearStart.AddDays(-1); // 3月31日

            // 既に繰越パターンが登録済みかチェック
            var marchHistory = await _ledgerRepository.GetByMonthAsync(
                cardIdm, previousFiscalYearEnd.Year, previousFiscalYearEnd.Month);
            if (marchHistory.Any(l => l.Summary == SummaryGenerator.GetCarryoverToNextYearSummary()))
            {
                System.Diagnostics.Debug.WriteLine("[DEBUG] 特殊パターン(N-002: 年度繰越)は登録済み");
                return;
            }

            var carryoverAmount = InitialBalance; // 繰越額（通常データの初期残高と一致させる）

            // ── 3月31日: 次年度への繰越 ──
            var carryoverOut = new Ledger
            {
                CardIdm = cardIdm,
                Date = previousFiscalYearEnd,
                Summary = SummaryGenerator.GetCarryoverToNextYearSummary(),
                Income = 0,
                Expense = carryoverAmount,
                Balance = 0,
                StaffName = staffName,
                Note = "テストデータ（次年度への繰越）"
            };
            await _ledgerRepository.InsertAsync(carryoverOut);

            // ── 4月1日: 前年度からの繰越 ──
            var carryoverIn = new Ledger
            {
                CardIdm = cardIdm,
                Date = fiscalYearStart,
                Summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
                Income = carryoverAmount,
                Expense = 0,
                Balance = carryoverAmount,
                StaffName = staffName,
                Note = "テストデータ（前年度からの繰越）"
            };
            await _ledgerRepository.InsertAsync(carryoverIn);

            System.Diagnostics.Debug.WriteLine("[DEBUG] 特殊パターン(N-002: 年度繰越)登録完了");
        }

        /// <summary>
        /// 新規購入・利用・払い戻しの一連のライフサイクルを登録
        /// </summary>
        /// <remarks>
        /// Su-001はRegisterSampleHistoryAsyncから除外されているため、
        /// このメソッドで新規購入→通常利用→払い戻しの完全なライフサイクルを生成する。
        /// 新規購入より前、払い戻しより後にはデータが存在しない。
        /// </remarks>
        private async Task RegisterPurchaseAndRefundAsync(string cardIdm, DateTime today, string staffName)
        {
            // 既に新規購入パターンが登録済みかチェック
            var date170 = today.AddDays(-170);
            var oldHistory = await _ledgerRepository.GetByMonthAsync(cardIdm, date170.Year, date170.Month);
            if (oldHistory.Any(l => l.Summary == "新規購入"))
            {
                System.Diagnostics.Debug.WriteLine("[DEBUG] 特殊パターン(Su-001: 新規購入・払い戻し)は登録済み");
                return;
            }

            var random = new Random(731); // 再現性のためシード固定
            var purchaseAmount = 1500; // デポジット500円は含まない（カード残高として管理される額）

            // ── 170日前: 新規購入 ──
            var balance = purchaseAmount;
            var purchaseLedger = new Ledger
            {
                CardIdm = cardIdm,
                Date = date170,
                Summary = "新規購入",
                Income = purchaseAmount,
                Expense = 0,
                Balance = balance,
                StaffName = staffName,
                Note = "テストデータ（新規購入: 2000円カード、デポジット500円を除く）"
            };
            await _ledgerRepository.InsertAsync(purchaseLedger);

            // ── 170日前～3日前: 通常利用データ ──
            for (int daysAgo = 169; daysAgo >= 3; daysAgo--)
            {
                var date = today.AddDays(-daysAgo);

                // 土日はスキップ
                if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                    continue;

                // 残高が少ない場合はチャージ
                if (balance < 3000)
                {
                    var chargeAmount = random.Next(3, 6) * 1000;
                    balance += chargeAmount;

                    var chargeLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = date,
                        Summary = SummaryGenerator.GetChargeSummary(),
                        Income = chargeAmount,
                        Expense = 0,
                        Balance = balance,
                        StaffName = staffName,
                        Note = "テストデータ"
                    };
                    var chargeLedgerId = await _ledgerRepository.InsertAsync(chargeLedger);

                    await _ledgerRepository.InsertDetailAsync(new LedgerDetail
                    {
                        LedgerId = chargeLedgerId,
                        UseDate = date.AddHours(7).AddMinutes(random.Next(60)),
                        Amount = chargeAmount,
                        Balance = balance,
                        IsCharge = true,
                        IsBus = false
                    });
                }

                // 鉄道利用（片道）
                var fromIdx = random.Next(SampleStations.Length);
                var toIdx = (fromIdx + random.Next(1, 5)) % SampleStations.Length;
                var fare = 200 + random.Next(10) * 30;
                balance -= fare;

                var usageLedger = new Ledger
                {
                    CardIdm = cardIdm,
                    Date = date,
                    Summary = $"鉄道（{SampleStations[fromIdx]}～{SampleStations[toIdx]}）",
                    Income = 0,
                    Expense = fare,
                    Balance = balance,
                    StaffName = staffName,
                    Note = "テストデータ"
                };
                var usageLedgerId = await _ledgerRepository.InsertAsync(usageLedger);

                await _ledgerRepository.InsertDetailAsync(new LedgerDetail
                {
                    LedgerId = usageLedgerId,
                    UseDate = date.AddHours(8).AddMinutes(random.Next(60)),
                    EntryStation = SampleStations[fromIdx],
                    ExitStation = SampleStations[toIdx],
                    Amount = fare,
                    Balance = balance,
                    IsCharge = false,
                    IsBus = false
                });
            }

            // ── 2日前: 払い戻し（残高全額を払出） ──
            var date2 = today.AddDays(-2);
            var refundLedger = new Ledger
            {
                CardIdm = cardIdm,
                Date = date2,
                Summary = SummaryGenerator.GetRefundSummary(),
                Income = 0,
                Expense = balance,
                Balance = 0,
                StaffName = staffName,
                Note = "テストデータ（払い戻し）"
            };
            await _ledgerRepository.InsertAsync(refundLedger);

            System.Diagnostics.Debug.WriteLine("[DEBUG] 特殊パターン(Su-001: 新規購入→利用→払い戻し)登録完了");
        }

        /// <summary>
        /// 指定日より前の n 番目の土日を返す（n=1 が最も近い）
        /// </summary>
        /// <param name="today">基準日</param>
        /// <param name="n">何番目の土日か（1以上）</param>
        /// <returns>n 番目の土日の日付</returns>
        internal static DateTime FindNthWeekendDayBefore(DateTime today, int n)
        {
            if (n <= 0)
                throw new ArgumentOutOfRangeException(nameof(n), "n は1以上を指定してください");

            var count = 0;
            var date = today.Date.AddDays(-1); // today自体は含めない
            while (true)
            {
                if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                {
                    count++;
                    if (count == n)
                        return date;
                }
                date = date.AddDays(-1);
            }
        }
    }
}
#endif
