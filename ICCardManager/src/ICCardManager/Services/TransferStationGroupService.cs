using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 同一とみなす駅・バス停のグループを <c>settings</c> テーブルへ保存する（Issue #1905）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>settings</c> は key-value テーブルのためスキーマ変更（マイグレーション）は不要
    /// （Issue #1689 の <c>last_backup_success_at</c> と同じ作法）。値は JSON 配列の配列で保存する。
    /// </para>
    /// <para>
    /// <b>AppSettings の一括保存には巻き込まない。</b> <c>SaveAppSettingsAsync</c> は
    /// F5 設定画面が組み立てた <see cref="Models.AppSettings"/> を丸ごと書くため、
    /// そこへ載せると F5 の保存がこのグループを既定値で上書きし得る
    /// （<c>development-conventions.md</c>「UPDATE の SET 句は、その経路で本当に編集する列に限る」）。
    /// </para>
    /// </remarks>
    public class TransferStationGroupService : ITransferStationGroupService
    {
        private readonly ISettingsRepository _settingsRepository;
        private readonly OrganizationOptions _organizationOptions;
        private readonly ILogger<TransferStationGroupService> _logger;

        /// <summary>
        /// 1 グループに必要な名前の最小件数
        /// </summary>
        /// <remarks>
        /// 1 件だけのグループは「何とも同一視しない」＝グループとして意味を持たない。
        /// 保存時に黙って捨てず、UI 側のバリデーションが先に弾く。
        /// </remarks>
        public const int MinimumNamesPerGroup = 2;

        public TransferStationGroupService(
            ISettingsRepository settingsRepository,
            OrganizationOptions organizationOptions,
            ILogger<TransferStationGroupService> logger)
        {
            _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
            _organizationOptions = organizationOptions ?? throw new ArgumentNullException(nameof(organizationOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<List<List<string>>> GetGroupsAsync()
        {
            var stored = await _settingsRepository
                .GetAsync(SettingsRepository.KeyTransferStationGroups)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(stored))
            {
                // 画面から一度も保存していない環境。appsettings.json（未指定なら C# 既定値）を初期値とする
                return CloneFromOptions();
            }

            if (TryDeserialize(stored!, out var groups))
            {
                return groups;
            }

            // 壊れた値は上書きせず残す（管理者が原因を追えるようにする）。
            // Issue #1819: 縮退したことを本番ログへ残す。件数を載せないと
            // 「縮退したのか、元からその件数なのか」を後から判別できない
            var fallback = CloneFromOptions();
            _logger.LogWarning(
                "同一視グループの設定値を解釈できなかったため、初期値（{GroupCount}グループ）へ縮退しました。" +
                "設定キー: {SettingKey}",
                fallback.Count,
                SettingsRepository.KeyTransferStationGroups);
            return fallback;
        }

        /// <inheritdoc />
        public async Task<bool> SaveGroupsAsync(IEnumerable<IEnumerable<string>> groups)
        {
            var normalized = Normalize(groups);
            var json = JsonSerializer.Serialize(normalized);

            var saved = await _settingsRepository
                .SetAsync(SettingsRepository.KeyTransferStationGroups, json)
                .ConfigureAwait(false);

            if (!saved)
            {
                return false;
            }

            // 保存できたときだけ実行中の静的状態へ反映する。
            // 反映しないとアプリを再起動するまで新しいグループが効かない
            SummaryGenerator.ApplyTransferStationGroups(normalized);

            _logger.LogInformation(
                "同一視グループを保存しました（{GroupCount}グループ・{NameCount}件）",
                normalized.Count,
                normalized.Sum(g => g.Count));

            return true;
        }

        /// <summary>
        /// 保存前の正規化（空白の除去・グループ内重複の除去・2 件未満のグループの除外）
        /// </summary>
        /// <remarks>
        /// 純関数として切り出し、DB を用意せずに境界を単体テストで固定できるようにする
        /// （<c>development-conventions.md</c> #1794「判断を純関数へ切り出す」）。
        /// </remarks>
        internal static List<List<string>> Normalize(IEnumerable<IEnumerable<string>> groups)
        {
            var result = new List<List<string>>();

            foreach (var group in groups ?? Enumerable.Empty<IEnumerable<string>>())
            {
                if (group == null)
                {
                    continue;
                }

                var names = new List<string>();
                foreach (var name in group)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var trimmed = name.Trim();
                    if (!names.Contains(trimmed, StringComparer.Ordinal))
                    {
                        names.Add(trimmed);
                    }
                }

                if (names.Count >= MinimumNamesPerGroup)
                {
                    result.Add(names);
                }
            }

            return result;
        }

        /// <summary>
        /// 保存済み JSON の解釈。失敗しても例外を投げず false を返す
        /// </summary>
        internal static bool TryDeserialize(string json, out List<List<string>> groups)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<List<string>>>(json);
                if (parsed == null)
                {
                    groups = new List<List<string>>();
                    return false;
                }

                // null 要素を含む配列（"[null]" 等）は解釈できたことにしない
                if (parsed.Any(g => g == null) || parsed.Any(g => g!.Any(n => n == null)))
                {
                    groups = new List<List<string>>();
                    return false;
                }

                groups = Normalize(parsed!);
                return true;
            }
            catch (JsonException)
            {
                groups = new List<List<string>>();
                return false;
            }
        }

        /// <summary>
        /// 組織設定（appsettings.json ないし C# 既定値）由来の初期値をコピーで返す
        /// </summary>
        private List<List<string>> CloneFromOptions()
            => _organizationOptions.SummaryRules.TransferStationGroups
                .Select(g => g.ToList())
                .ToList();
    }
}
