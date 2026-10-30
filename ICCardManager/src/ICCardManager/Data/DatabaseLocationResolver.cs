#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Common;
using ICCardManager.Infrastructure.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ICCardManager.Data
{
    /// <summary>
    /// 棄却したデータベースパス（形式不正）の記録（Issue #1599 / #2328）
    /// </summary>
    /// <param name="Source">どの設定ファイルの値か（利用者へ示すファイル名）</param>
    /// <param name="Value">棄却した値</param>
    /// <param name="Reason">棄却した理由（<see cref="PathValidator.ValidatePathFormat"/> の文言）</param>
    public sealed record RejectedDatabasePath(string Source, string Value, string? Reason);

    /// <summary>
    /// 実際に開くデータベースパスの解決結果（Issue #2328）
    /// </summary>
    /// <param name="Path">開くパス。null は既定（<c>C:\ProgramData\ICCardManager\iccard.db</c>）</param>
    /// <param name="Rejected">形式不正のため棄却した設定値（空なら棄却なし）</param>
    public sealed record ResolvedDatabasePath(string? Path, IReadOnlyList<RejectedDatabasePath> Rejected);

    /// <summary>
    /// データベースパスを 1 か所で解決し、DB 接続（<see cref="DatabaseOptions"/>）と
    /// 共有モードのキャッシュ TTL 短縮（<see cref="CacheOptions"/>）の両方へ同じ値を渡す（Issue #2328）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧実装は判定するメソッド（<see cref="DbContext.IsSharedModePath(string?)"/>）だけを共有し、
    /// <b>入力は別々に読んでいた</b>。DB 接続は「database_config.txt があればそれ、無ければ appsettings.json の
    /// <c>DatabaseOptions:Path</c>」を開くのに、TTL の短縮は database_config.txt だけを見ていたため、
    /// appsettings.json で共有フォルダーを指すと、接続は共有モードなのに TTL は単独利用の長さのままになり、
    /// 「カード系の最長 TTL＝ヘルスチェック間隔 15 秒」（04_機能設計書 §18.4）が崩れていた。
    /// appsettings.json 由来の値は形式の検証（#1599）も通っていなかった。
    /// </para>
    /// <para>
    /// <b>判定の手段を 1 つにしても、判定に渡す値を別々に読めば二重管理は残る</b>。
    /// 解決は本クラスの <see cref="Resolve"/> だけで行い、両方の PostConfigure はその結果を参照する。
    /// </para>
    /// </remarks>
    public static class DatabaseLocationResolver
    {
        /// <summary>database_config.txt を利用者へ示す名前</summary>
        public const string ConfigFileSource = "database_config.txt";

        /// <summary>appsettings.json の項目を利用者へ示す名前</summary>
        public const string AppSettingsSource = "appsettings.json（DatabaseOptions の Path）";

        /// <summary>
        /// 開くデータベースパスを決める。
        /// </summary>
        /// <param name="configFileValue">database_config.txt の内容（未作成・空なら null／空白）</param>
        /// <param name="appSettingsValue">appsettings.json の <c>DatabaseOptions:Path</c></param>
        /// <returns>
        /// 優先順は database_config.txt → appsettings.json → 既定（null）。
        /// どちらも <see cref="PathValidator.ValidatePathFormat"/> で形式だけを検証し、不正なら棄却して次へ進む
        /// （到達性は見ない。一時的な切断で既定のローカル DB へ黙って切り替わらないため。#1599）。
        /// </returns>
        public static ResolvedDatabasePath Resolve(string? configFileValue, string? appSettingsValue)
        {
            var rejected = new List<RejectedDatabasePath>();

            var path = Accept(configFileValue, ConfigFileSource, rejected)
                ?? Accept(appSettingsValue, AppSettingsSource, rejected);

            return new ResolvedDatabasePath(path, rejected);
        }

        private static string? Accept(string? value, string source, List<RejectedDatabasePath> rejected)
        {
            if (value is null || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var validation = PathValidator.ValidatePathFormat(value);
            if (validation.IsValid)
            {
                return value;
            }

            rejected.Add(new RejectedDatabasePath(source, value, validation.ErrorMessage));
            return null;
        }

        /// <summary>
        /// 起動失敗のダイアログに添える、実際に開こうとしたデータベースパス（Issue #2328 のレビューで検出）
        /// </summary>
        /// <remarks>
        /// database_config.txt を直接読んで表示すると、appsettings.json だけで共有フォルダーを指す構成では空になり、
        /// database_config.txt が形式不正で棄却されたときは実際には開いていないパスを示す。接続と同じ
        /// <see cref="Resolve"/> の結果を表示し、既定のときは既定のパスを示す。
        /// 起動失敗は DI コンテナの構築前でも起こり得るため、Options ではなく生の設定値から解決する。
        /// </remarks>
        /// <param name="configFileValue">database_config.txt の内容</param>
        /// <param name="appSettingsValue">appsettings.json の <c>DatabaseOptions:Path</c>（構成の読み込み前なら null）</param>
        public static string DescribePathForStartupError(string? configFileValue, string? appSettingsValue)
        {
            var path = Resolve(configFileValue, appSettingsValue).Path;
            return path ?? $"{System.IO.Path.Combine(AppDataPaths.RootDirectory, DbContext.DatabaseFileName)}（既定）";
        }

        /// <summary>
        /// 棄却したデータベースパスを起動後に知らせる警告文言（Issue #1599 / #2328）
        /// </summary>
        /// <remarks>
        /// 棄却した値ごとにどの設定ファイルの値かを示し、実際に使った保存先（既定または次の候補）を述べる。
        /// database_config.txt を棄却して appsettings.json の値で起動した場合に「既定の保存先」と書くと事実に反する。
        /// </remarks>
        public static string BuildRejectedWarningMessage(ResolvedDatabasePath resolution)
        {
            var rejectedLines = string.Join(
                "\n",
                resolution.Rejected.Select(r => $"・設定ファイル {r.Source} の「{r.Value}」"));
            var used = resolution.Path is null ? "既定の保存先" : $"保存先「{resolution.Path}」";

            return "次のデータベース保存先は無効な形式（相対パス・不正な文字を含む等）のため使用せず、" +
                $"{used}で起動しました。\n{rejectedLines}\n\n" +
                "意図した保存先と異なる場合は、設定画面（F5）でドライブ文字から始まる絶対パス、" +
                "または「\\\\server\\share」形式のネットワークパスを再設定してください。";
        }

        /// <summary>
        /// <see cref="DatabaseOptions"/> と <see cref="CacheOptions"/> を、同じ解決結果から構成するよう登録する。
        /// </summary>
        /// <param name="services">サービスコレクション</param>
        /// <param name="configuration">appsettings.json の構成</param>
        /// <param name="readConfigFile">database_config.txt を読む関数（未作成なら null を返す）</param>
        /// <param name="onResolved">解決した直後に 1 度だけ呼ばれる（棄却の警告・ログ用）。null 可</param>
        /// <remarks>
        /// 解決は最初に必要になった時点で 1 度だけ行い、以後は同じ結果を使う
        /// （DB 接続と TTL で、読んだ時点の違いによる食い違いも起こさない）。
        /// </remarks>
        public static void Register(
            IServiceCollection services,
            IConfiguration configuration,
            Func<string?> readConfigFile,
            Action<ResolvedDatabasePath>? onResolved)
        {
            var section = configuration.GetSection("DatabaseOptions");
            var resolution = new Lazy<ResolvedDatabasePath>(() =>
            {
                var result = Resolve(readConfigFile(), section["Path"]);
                onResolved?.Invoke(result);
                return result;
            });

            services.Configure<DatabaseOptions>(section);
            services.PostConfigure<DatabaseOptions>(dbOptions =>
            {
                // 形式不正で棄却した appsettings.json の値を、そのまま DbContext へ渡さない
                dbOptions.Path = resolution.Value.Path ?? string.Empty;
            });

            // 共有モード時はキャッシュTTLを短縮（他PCの変更を素早く反映するため。値と理由は CacheOptions.ApplySharedModeTtl）。
            // Issue #1597: 判定は DbContext と同一の共有モード判定（UNC／マップドネットワークドライブ）。
            // Issue #2328: 判定に渡すのも、DbContext が実際に開くパス（上の PostConfigure と同じ解決結果）。
            services.PostConfigure<CacheOptions>(cacheOptions =>
            {
                if (DbContext.IsSharedModePath(resolution.Value.Path))
                {
                    cacheOptions.ApplySharedModeTtl();
                }
            });
        }
    }
}
