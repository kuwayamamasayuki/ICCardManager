# サードパーティライブラリ ライセンス一覧

本ドキュメントは、交通系ICカード管理システム「ピッすい」が使用しているサードパーティライブラリとそのライセンス情報をまとめたものです。

本システム自体は [MIT License](../LICENSE) の下で公開されています。

---

## 1. アプリケーション本体の依存ライブラリ

| ライブラリ名 | バージョン | ライセンス | 用途 |
|---|---|---|---|
| [System.Data.SQLite.Core](https://system.data.sqlite.org/) | 1.0.119 | Public Domain | SQLiteデータベースアクセス |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) | 0.105.1 | MIT | Excel帳票（物品出納簿）の生成 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | MVVMパターン実装（ObservableProperty、RelayCommand等） |
| [FelicaLib.DotNet](https://github.com/sakapon/felicalib-remodeled) | 1.2.67 | MIT + BSD-3-Clause | FeliCa（Sony PaSoRi）カード読み取り ※1 |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | 8.0.1 | MIT | 依存性注入（DIコンテナ） |
| [Microsoft.Extensions.Logging](https://github.com/dotnet/runtime) | 8.0.1 | MIT | ログ記録フレームワーク |
| [Microsoft.Extensions.Logging.Debug](https://github.com/dotnet/runtime) | 8.0.1 | MIT | デバッグ出力へのログ記録 |
| [Microsoft.Extensions.Configuration.Json](https://github.com/dotnet/runtime) | 8.0.1 | MIT | JSON設定ファイル読み込み |
| [Microsoft.Extensions.Logging.Configuration](https://github.com/dotnet/runtime) | 8.0.1 | MIT | 設定ファイルからのログ設定の読み込み |
| [Microsoft.Extensions.Options.ConfigurationExtensions](https://github.com/dotnet/runtime) | 8.0.0 | MIT | 設定ファイルの値を型付きオプションへ割り当て |
| [Microsoft.Extensions.Caching.Memory](https://github.com/dotnet/runtime) | 8.0.1 | MIT | インメモリキャッシュ |

> **※1** FelicaLib.DotNet は、felicalib Remodeled 部分が MIT License（Copyright © Keiho Sakapon）、オリジナルの felicalib 部分が BSD-3-Clause License（Copyright © 2007 Takuya Murakami）のデュアルライセンスです。

## 1a. 推移的に配布されるライブラリ

§1 の直接参照（と §4 の開発ツール）が依存として連れてくるライブラリです。インストーラーに同梱される配布物（アプリケーション本体と `Tools` の DebugDataViewer）に含まれるため、直接参照と同じくライセンスの条件（著作権表示とライセンス文の保持など）が適用されます。一覧は本体と DebugDataViewer のロックファイル（`packages.lock.json`）から、ビルド時にのみ使う参照（`PrivateAssets="all"`）を除いて導出したもので、名前・版・ライセンスはテストでロックファイルと各パッケージの宣言（`.nuspec`）に照合しています。

| ライブラリ名 | バージョン | ライセンス | 依存元 |
|---|---|---|---|
| [ClosedXML.Parser](https://github.com/ClosedXML/ClosedXML.Parser) | 2.0.0 | MIT | ClosedXML |
| [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) | 3.1.1 | MIT | ClosedXML |
| [DocumentFormat.OpenXml.Framework](https://github.com/dotnet/Open-XML-SDK) | 3.1.1 | MIT | ClosedXML |
| [ExcelNumberFormat](https://github.com/andersnm/ExcelNumberFormat) | 1.1.0 | MIT | ClosedXML |
| [Microsoft.Bcl.AsyncInterfaces](https://github.com/dotnet/runtime) | 10.0.1 | MIT | CommunityToolkit.Mvvm・Microsoft.Extensions.* |
| [Microsoft.Bcl.HashCode](https://github.com/dotnet/corefx) | 1.1.1 | MIT | ClosedXML |
| [Microsoft.Extensions.Caching.Abstractions](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.Configuration](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.Configuration.Abstractions](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.Configuration.Binder](https://github.com/dotnet/runtime) | 8.0.2 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.Configuration.FileExtensions](https://github.com/dotnet/runtime) | 8.0.1 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | 8.0.2 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.FileProviders.Abstractions](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.FileProviders.Physical](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.FileSystemGlobbing](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | 8.0.2 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.Options](https://github.com/dotnet/runtime) | 8.0.2 | MIT | Microsoft.Extensions.* |
| [Microsoft.Extensions.Primitives](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [RBush.Signed](https://github.com/viceroypenguin/RBush) | 4.0.0 | MIT | ClosedXML |
| [SixLabors.Fonts](https://github.com/SixLabors/Fonts) | 1.0.0 | Apache-2.0 ※2 | ClosedXML |
| [Stub.System.Data.SQLite.Core.NetFramework](https://system.data.sqlite.org/) | 1.0.119 | Public Domain | System.Data.SQLite.Core |
| [System.Buffers](https://github.com/dotnet/maintenance-packages) | 4.6.1 | MIT | ClosedXML・CommunityToolkit.Mvvm・Microsoft.Extensions.* |
| [System.ComponentModel.Annotations](https://github.com/dotnet/runtime) | 5.0.0 | MIT | CommunityToolkit.Mvvm |
| [System.Diagnostics.DiagnosticSource](https://github.com/dotnet/runtime) | 8.0.1 | MIT | Microsoft.Extensions.* |
| [System.Memory](https://github.com/dotnet/maintenance-packages) | 4.6.3 | MIT | ClosedXML・CommunityToolkit.Mvvm・Microsoft.Extensions.* |
| [System.Numerics.Vectors](https://github.com/dotnet/maintenance-packages) | 4.6.1 | MIT | ClosedXML・CommunityToolkit.Mvvm・Microsoft.Extensions.* |
| [System.Runtime.CompilerServices.Unsafe](https://github.com/dotnet/maintenance-packages) | 6.1.2 | MIT | ClosedXML・CommunityToolkit.Mvvm・Microsoft.Extensions.* |
| [System.Text.Encodings.Web](https://github.com/dotnet/runtime) | 8.0.0 | MIT | Microsoft.Extensions.* |
| [System.Text.Json](https://github.com/dotnet/runtime) | 8.0.5 | MIT | Microsoft.Extensions.* |
| [System.Threading.Tasks.Extensions](https://github.com/dotnet/maintenance-packages) | 4.6.3 | MIT | CommunityToolkit.Mvvm・Microsoft.Extensions.* |
| [System.ValueTuple](https://github.com/dotnet/runtime) | 4.5.0 | MIT | Microsoft.Extensions.* |

> **※2** SixLabors.Fonts は 1.x が Apache-2.0 ですが、2.x 以降は Six Labors Split License に変わっています（利用の形態などの条件によって Apache-2.0 の扱いになる場合と、商用ライセンスが必要になる場合があります）。ClosedXML を更新して 2.x が入ってきた場合は、ライセンス本文で条件を確認してください。§6 の「すべて寛容型」も、その時点で見直しが必要です。なお 2.x のライセンスは SPDX のライセンス式で書けないため、版を上げるとライセンス式を持たない宣言として照合のテストが失敗し、確認を促します。

## 2. テスト用ライブラリ

以下のライブラリは開発・テスト時のみ使用され、配布されるアプリケーションには含まれません。

| ライブラリ名 | バージョン | ライセンス | 用途 |
|---|---|---|---|
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.14.1 | MIT | テストフレームワーク基盤 |
| [xunit](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 | ユニットテストフレームワーク |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 2.8.2 | Apache-2.0 | Visual Studio テストランナー |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 3.2.0 | MIT | コードカバレッジ収集 |
| [FluentAssertions](https://github.com/fluentassertions/fluentassertions) | 6.12.2 | Apache-2.0 | テストアサーションライブラリ |
| [Moq](https://github.com/moq/moq) | 4.21.0 | BSD-3-Clause | モックフレームワーク |
| [Xunit.SkippableFact](https://github.com/AArnott/Xunit.SkippableFact) | 1.5.85 | MS-PL | 実行時の条件による UI テストのスキップ |
| [FlaUI.Core](https://github.com/FlaUI/FlaUI) | 5.0.0 | MIT | UIオートメーションテスト基盤 |
| [FlaUI.UIA3](https://github.com/FlaUI/FlaUI) | 5.0.0 | MIT | UIA3による画面操作自動化 |

UI テストは本体と同じ System.Data.SQLite.Core（§1）も使います。2 つのテストプロジェクトで共通するパッケージは同じ版にそろえています。

## 3. ビルド時にのみ使用するライブラリ

以下はアプリケーション本体のビルド時にのみ使用され、配布されるアプリケーションには含まれません（csproj で `PrivateAssets="all"`）。共有のビルド設定（`Directory.Build.targets`）で全プロジェクトに足している参照アセンブリ（Microsoft.NETFramework.ReferenceAssemblies。.NET Framework の参照用アセンブリで、ビルドにのみ使い配布されない）は、この表には載せていません。

| ライブラリ名 | バージョン | ライセンス | 用途 |
|---|---|---|---|
| [Microsoft.CodeAnalysis.NetAnalyzers](https://github.com/dotnet/roslyn-analyzers) | 8.0.0 | MIT | .NET アナライザー（CA ルール）によるビルド時の静的解析 |

## 4. 開発ツール（DebugDataViewer）の依存ライブラリ

デバッグ用ツール（DebugDataViewer）は、インストーラーでアプリケーションのフォルダーの `Tools` に同梱され、スタートメニューの「デバッグツール」から起動できます（配布物に含まれます）。

DebugDataViewer はアプリケーション本体と共通のライブラリ（System.Data.SQLite.Core、CommunityToolkit.Mvvm、FelicaLib.DotNet、Microsoft.Extensions.*）を本体と同じ版で直接参照し、本体をプロジェクト参照するため本体の依存（ClosedXML など）も同梱されます。ライセンスは「1. アプリケーション本体の依存ライブラリ」と「1a. 推移的に配布されるライブラリ」を参照してください。本体と異なるライブラリや版を使う場合は、この節に表を設けて記載します。

## 5. 音声素材

| 素材 | キャラクター | ライセンス・利用規約 | 用途 |
|---|---|---|---|
| [VOICEVOX](https://voicevox.hiroshiba.jp/) 生成音声 | 四国めたん | [VOICEVOX 四国めたん 利用規約](https://voicevox.hiroshiba.jp/term/) | 貸出・返却時の女性音声 |
| [VOICEVOX](https://voicevox.hiroshiba.jp/) 生成音声 | 玄野武宏 | [VOICEVOX 玄野武宏 利用規約](https://voicevox.hiroshiba.jp/term/) | 貸出・返却時の男性音声 |

> クレジット表記（VOICEVOX利用規約に基づき必須）: **VOICEVOX:四国めたん / VOICEVOX:玄野武宏**
>
> アプリケーション内では設定ダイアログに上記クレジットを表示しています。

## 6. ライセンス種別の概要

配布されるアプリケーション（§1・§1a・§4）が使用しているライセンスはすべて**寛容型（permissive）ライセンス**であり、商用利用・再配布が許可されています。コピーレフト型ライセンス（GPL等）は含まれていません。テスト用ライブラリ（§2）の Xunit.SkippableFact は MS-PL（弱いコピーレフト）ですが、開発・テスト時にのみ使い、配布物には含まれません。

| ライセンス | 種別 | 主な条件 |
|---|---|---|
| MIT | 寛容型 | 著作権表示とライセンス文の保持 |
| Apache-2.0 | 寛容型 | 著作権表示、ライセンス文の保持、変更の明示 |
| BSD-2-Clause | 寛容型 | 著作権表示とライセンス文の保持 |
| BSD-3-Clause | 寛容型 | 著作権表示とライセンス文の保持、著作者名の無断使用禁止 |
| MS-PL | 弱いコピーレフト（OSI 承認） | 著作権・特許・商標の表示の保持。ソースコードは同じライセンスで、コンパイル済みの形は MS-PL に適合するライセンスで配布する。特許訴訟を起こすとライセンスが終了する（テスト用のみ・配布物に含まれない） |
| Public Domain | パブリックドメイン | 制約なし |

## 7. 更新履歴

| 日付 | 内容 |
|---|---|
| 2026-10-03 | 推移的に配布されるライブラリ（§1a）を追加し、§1 の Microsoft.Extensions.* のライセンスを MIT に訂正（8.0 系の宣言は MIT。Apache-2.0 は 3.x 系まで）。ライセンス欄を各パッケージの宣言と照合するテストを追加（Issue #2215） |
| 2026-10-03 | §3 の見出しと中身の食い違いを解消し、ビルド時のみ使用するライブラリ（§3）と開発ツール（DebugDataViewer）の依存ライブラリ（§4）に分けた。DebugDataViewer はインストーラーに同梱されるため、「配布されない」とした記述を改めた。以降の節番号を 1 つずつ繰り下げた |
| 2026-10-02 | テスト用ライブラリ（§2）の版を csproj に同期し、Xunit.SkippableFact を追加。UI テストのテスト基盤パッケージを単体テストと同じ版へ更新。xunit.runner.visualstudio は 2.8.2 でライセンスが MIT から Apache-2.0 に変わった（Issue #2166） |
| 2026-10-02 | 未使用の Microsoft.Extensions.Hosting を削除し、Microsoft.Extensions.Logging.Configuration / Options.ConfigurationExtensions を直接参照として追加。ClosedXML・CommunityToolkit.Mvvm の版を csproj に同期（Issue #2165） |
| 2026-10-02 | ビルド時のみ使用する Microsoft.CodeAnalysis.NetAnalyzers を追加（Issue #2162） |
| 2026-04-16 | パッケージバージョンを最新に同期、VOICEVOX音声素材のセクションを追加 |
| 2026-03-23 | 初版作成 |

---

*本ドキュメントは Issue [#1054](https://github.com/kuwayamamasayuki/ICCardManager/issues/1054) に基づき作成されました。パッケージの更新時には本一覧も併せて更新してください。*
