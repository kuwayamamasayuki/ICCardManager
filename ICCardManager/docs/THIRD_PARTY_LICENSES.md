# サードパーティライブラリ ライセンス一覧

本ドキュメントは、交通系ICカード管理システム「ピッすい」が使用しているサードパーティライブラリとそのライセンス情報をまとめたものです。

本システム自体は [MIT License](../LICENSE) の下で公開されています。

---

## 1. アプリケーション本体の依存ライブラリ

| ライブラリ名 | バージョン | ライセンス | 用途 |
|---|---|---|---|
| [System.Data.SQLite.Core](https://system.data.sqlite.org/) | 1.0.119 | Public Domain | SQLiteデータベースアクセス |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) | 0.105.0 | MIT | Excel帳票（物品出納簿）の生成 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.2.2 | MIT | MVVMパターン実装（ObservableProperty、RelayCommand等） |
| [FelicaLib.DotNet](https://github.com/sakapon/felicalib-remodeled) | 1.2.67 | MIT + BSD-3-Clause | FeliCa（Sony PaSoRi）カード読み取り ※1 |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | 8.0.1 | Apache-2.0 | 依存性注入（DIコンテナ） |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) | 8.0.1 | Apache-2.0 | アプリケーションホスティング |
| [Microsoft.Extensions.Logging](https://github.com/dotnet/runtime) | 8.0.1 | Apache-2.0 | ログ記録フレームワーク |
| [Microsoft.Extensions.Logging.Debug](https://github.com/dotnet/runtime) | 8.0.1 | Apache-2.0 | デバッグ出力へのログ記録 |
| [Microsoft.Extensions.Configuration.Json](https://github.com/dotnet/runtime) | 8.0.1 | Apache-2.0 | JSON設定ファイル読み込み |
| [Microsoft.Extensions.Caching.Memory](https://github.com/dotnet/runtime) | 8.0.1 | Apache-2.0 | インメモリキャッシュ |

> **※1** FelicaLib.DotNet は、felicalib Remodeled 部分が MIT License（Copyright © Keiho Sakapon）、オリジナルの felicalib 部分が BSD-3-Clause License（Copyright © 2007 Takuya Murakami）のデュアルライセンスです。

## 2. テスト用ライブラリ

以下のライブラリは開発・テスト時のみ使用され、配布されるアプリケーションには含まれません。

| ライブラリ名 | バージョン | ライセンス | 用途 |
|---|---|---|---|
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.5.0 | MIT | テストフレームワーク基盤 |
| [xunit](https://github.com/xunit/xunit) | 2.4.2 | Apache-2.0 | ユニットテストフレームワーク |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 2.4.5 | MIT | Visual Studio テストランナー |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 3.2.0 | MIT | コードカバレッジ収集 |
| [FluentAssertions](https://github.com/fluentassertions/fluentassertions) | 6.12.0 | Apache-2.0 | テストアサーションライブラリ |
| [Moq](https://github.com/moq/moq) | 4.20.70 | BSD-3-Clause | モックフレームワーク |
| [FlaUI.Core](https://github.com/FlaUI/FlaUI) | 5.0.0 | MIT | UIオートメーションテスト基盤 |
| [FlaUI.UIA3](https://github.com/FlaUI/FlaUI) | 5.0.0 | MIT | UIA3による画面操作自動化 |

## 3. 開発ツール用ライブラリ

以下のライブラリはデバッグ用ツール（DebugDataViewer）で使用され、配布されるアプリケーションには含まれません。

アプリケーション本体と共通のライブラリ（System.Data.SQLite.Core、CommunityToolkit.Mvvm、FelicaLib.DotNet、Microsoft.Extensions.*）を使用しています。詳細は「1. アプリケーション本体の依存ライブラリ」を参照してください。

## 4. 音声素材

| 素材 | キャラクター | ライセンス・利用規約 | 用途 |
|---|---|---|---|
| [VOICEVOX](https://voicevox.hiroshiba.jp/) 生成音声 | 四国めたん | [VOICEVOX 四国めたん 利用規約](https://voicevox.hiroshiba.jp/term/) | 貸出・返却時の女性音声 |
| [VOICEVOX](https://voicevox.hiroshiba.jp/) 生成音声 | 玄野武宏 | [VOICEVOX 玄野武宏 利用規約](https://voicevox.hiroshiba.jp/term/) | 貸出・返却時の男性音声 |

> クレジット表記（VOICEVOX利用規約に基づき必須）: **VOICEVOX:四国めたん / VOICEVOX:玄野武宏**
>
> アプリケーション内では設定ダイアログに上記クレジットを表示しています。

## 5. ライセンス種別の概要

本システムで使用しているライセンスはすべて**寛容型（permissive）ライセンス**であり、商用利用・再配布が許可されています。コピーレフト型ライセンス（GPL等）は含まれていません。

| ライセンス | 種別 | 主な条件 |
|---|---|---|
| MIT | 寛容型 | 著作権表示とライセンス文の保持 |
| Apache-2.0 | 寛容型 | 著作権表示、ライセンス文の保持、変更の明示 |
| BSD-2-Clause | 寛容型 | 著作権表示とライセンス文の保持 |
| BSD-3-Clause | 寛容型 | 著作権表示とライセンス文の保持、著作者名の無断使用禁止 |
| Public Domain | パブリックドメイン | 制約なし |

## 6. 更新履歴

| 日付 | 内容 |
|---|---|
| 2026-04-16 | パッケージバージョンを最新に同期、VOICEVOX音声素材のセクションを追加 |
| 2026-03-23 | 初版作成 |

---

*本ドキュメントは Issue [#1054](https://github.com/kuwayamamasayuki/ICCardManager/issues/1054) に基づき作成されました。パッケージの更新時には本一覧も併せて更新してください。*
