#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ICCardManager.Models;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// 仮想カードリーダー（<c>HybridCardReader</c>）に、カードごとの残額と利用履歴を外から渡す（Issue #2293。DEBUG ビルドの UI テスト用）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 仮想タッチ（<c>HybridCardReader.SimulateCardRead</c>）は IDm を届けるだけで、残額・履歴の読み取りは
    /// 実カードリーダーへ委譲される。そのため、タッチしたカードの残額で判定する画面（残額の食い違い警告、カードの履歴からの取り込み）を
    /// UI テストで通せなかった。環境変数 <see cref="EnvironmentVariable"/> に JSON ファイルのパスを渡すと、起動時に読み込んで
    /// <c>HybridCardReader.SetCustomCardData</c> へ設定する。
    /// </para>
    /// <para>
    /// 読み込むのは DEBUG ビルドの起動時だけ（<c>App.xaml.cs</c>）。Release ビルドには仮想カードリーダー自体が無いため、
    /// リーダーへ設定する <c>ApplyFromEnvironment</c> は DEBUG だけにある（解析の <see cref="Parse"/> は Release の単体テストでも検証できるよう常にある）。
    /// 形式:
    /// <code>
    /// { "cards": [ { "idm": "07FE112233445566", "balance": 19580,
    ///   "history": [ { "useDate": "2026-10-19", "entryStation": "博多", "exitStation": "天神", "amount": 210, "balance": 19580 } ] } ] }
    /// </code>
    /// 履歴はカードと同じく新しい順に並べる。
    /// </para>
    /// </remarks>
    public static class DebugCardData
    {
        /// <summary>カードデータの JSON ファイルのパスを渡す環境変数</summary>
        public const string EnvironmentVariable = "ICCARDMANAGER_DEBUG_CARD_DATA";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

#if DEBUG
        /// <summary>
        /// 環境変数が指す JSON ファイルを読み、仮想カードリーダーへ設定する。環境変数が無ければ何もしない
        /// </summary>
        /// <returns>設定したカードの数</returns>
        public static int ApplyFromEnvironment(HybridCardReader reader, Func<string, string?> getEnvironmentVariable)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var path = getEnvironmentVariable(EnvironmentVariable);
            if (path is null || string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            var cards = Parse(File.ReadAllText(path));
            foreach (var card in cards)
            {
                reader.SetCustomCardData(card.Idm, card.Balance, card.History);
            }

            return cards.Count;
        }
#endif

        /// <summary>
        /// JSON を読み、カードごとの残額と利用履歴にする
        /// </summary>
        /// <exception cref="FormatException">形式が違う（IDm が無い、日付が読めない等）</exception>
        public static IReadOnlyList<Entry> Parse(string json)
        {
            var document = JsonSerializer.Deserialize<FileDto>(json, JsonOptions)
                ?? throw new FormatException("カードデータの JSON が空です。");

            return (document.Cards ?? new List<CardDto>())
                .Select(ToEntry)
                .ToList();
        }

        private static Entry ToEntry(CardDto card)
        {
            if (card.Idm is null || string.IsNullOrWhiteSpace(card.Idm))
            {
                throw new FormatException("カードデータに idm が無いカードがあります。");
            }

            var history = card.History?.Select(h => new LedgerDetail
            {
                UseDate = h.UseDate is null || string.IsNullOrWhiteSpace(h.UseDate)
                    ? null
                    : DateTime.ParseExact(h.UseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                EntryStation = h.EntryStation ?? string.Empty,
                ExitStation = h.ExitStation ?? string.Empty,
                Amount = h.Amount,
                Balance = h.Balance,
                IsCharge = h.IsCharge,
                IsBus = h.IsBus,
            }).ToList();

            return new Entry(card.Idm, card.Balance, history);
        }

        /// <summary>1 枚のカードの残額と利用履歴（どちらも省略できる）</summary>
        public sealed class Entry
        {
            public Entry(string idm, int? balance, IReadOnlyList<LedgerDetail>? history)
            {
                Idm = idm;
                Balance = balance;
                History = history;
            }

            public string Idm { get; }

            public int? Balance { get; }

            public IReadOnlyList<LedgerDetail>? History { get; }
        }

        private sealed class FileDto
        {
            public List<CardDto>? Cards { get; set; }
        }

        private sealed class CardDto
        {
            public string? Idm { get; set; }

            public int? Balance { get; set; }

            public List<HistoryDto>? History { get; set; }
        }

        private sealed class HistoryDto
        {
            public string? UseDate { get; set; }

            public string? EntryStation { get; set; }

            public string? ExitStation { get; set; }

            public int? Amount { get; set; }

            public int? Balance { get; set; }

            public bool IsCharge { get; set; }

            public bool IsBus { get; set; }
        }
    }
}
