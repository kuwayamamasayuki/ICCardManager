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
            await RegisterSampleHistoryAsync();
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
        public async Task RegisterSampleHistoryAsync()
        {
            var random = new Random(42); // 再現性のためシード固定
            var today = DateTime.Now.Date;

            // 各カードに対してサンプル履歴を登録（全カード）
            foreach (var card in TestCardList)
            {
                // 既存の履歴があるかチェック
                var existingHistory = await _ledgerRepository.GetByMonthAsync(card.CardIdm, today.Year, today.Month);
                if (existingHistory.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] 履歴が既に存在: {card.CardNumber}");
                    continue;
                }

                var balance = 50000; // 初期残高（長期間・大量データ用に増額）
                var staffName = TestStaffList[random.Next(TestStaffList.Length)].Name;

                // 過去180日分（約6ヶ月）のサンプル履歴を生成
                // 各月50件以上のデータでページングテストが可能
                for (int daysAgo = 180; daysAgo >= 0; daysAgo--)
                {
                    var date = today.AddDays(-daysAgo);

                    // 土日はスキップ（平日のみ利用）
                    if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                        continue;

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

                System.Diagnostics.Debug.WriteLine($"[DEBUG] サンプル履歴登録完了: {card.CardNumber}");
            }
        }

        /// <summary>
        /// テストデータをリセット（職員・カード・履歴を削除して再登録）
        /// </summary>
        public async Task ResetTestDataAsync()
        {
            System.Diagnostics.Debug.WriteLine("[DEBUG] テストデータリセット開始");

            // テスト職員を削除
            foreach (var staff in TestStaffList)
            {
                var existing = await _staffRepository.GetByIdmAsync(staff.StaffIdm);
                if (existing != null)
                {
                    await _staffRepository.DeleteAsync(staff.StaffIdm);
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] テスト職員削除: {staff.Name}");
                }
            }

            // テストカードを削除
            foreach (var card in TestCardList)
            {
                var existing = await _cardRepository.GetByIdmAsync(card.CardIdm);
                if (existing != null)
                {
                    await _cardRepository.DeleteAsync(card.CardIdm);
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] テストカード削除: {card.CardNumber}");
                }
            }

            // 再登録
            await RegisterAllTestDataAsync();

            System.Diagnostics.Debug.WriteLine("[DEBUG] テストデータリセット完了");
        }

        /// <summary>
        /// 任意のカードに履歴データを生成
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="days">生成する日数</param>
        /// <param name="staffName">職員名</param>
        public async Task GenerateHistoryAsync(string cardIdm, int days, string staffName)
        {
            var card = await _cardRepository.GetByIdmAsync(cardIdm);
            if (card == null)
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] カードが見つかりません: {cardIdm}");
                return;
            }

            var random = new Random();
            var today = DateTime.Now.Date;
            var balance = 5000;

            for (int daysAgo = days; daysAgo >= 0; daysAgo--)
            {
                var date = today.AddDays(-daysAgo);

                // ランダムな駅間移動
                var fromIdx = random.Next(SampleStations.Length);
                var toIdx = (fromIdx + random.Next(1, 5)) % SampleStations.Length;
                var fare = 200 + random.Next(10) * 30;

                balance -= fare;

                var ledger = new Ledger
                {
                    CardIdm = cardIdm,
                    Date = date,
                    Summary = $"鉄道（{SampleStations[fromIdx]}～{SampleStations[toIdx]}）",
                    Income = 0,
                    Expense = fare,
                    Balance = balance,
                    StaffName = staffName,
                    Note = "生成されたテストデータ"
                };
                await _ledgerRepository.InsertAsync(ledger);
            }

            System.Diagnostics.Debug.WriteLine($"[DEBUG] 履歴生成完了: {card.CardNumber} - {days}日分");
        }

        /// <summary>
        /// 全テストデータのIDm一覧を取得
        /// </summary>
        public static IEnumerable<(string Idm, string Description, bool IsStaff)> GetAllTestIdms()
        {
            foreach (var staff in TestStaffList)
            {
                yield return (staff.StaffIdm, $"職員: {staff.Name} ({staff.Number})", true);
            }

            foreach (var card in TestCardList)
            {
                yield return (card.CardIdm, $"カード: {card.CardType} {card.CardNumber}", false);
            }
        }
    }
}
#endif
