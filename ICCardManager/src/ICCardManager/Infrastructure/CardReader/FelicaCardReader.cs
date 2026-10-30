#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using FelicaLib;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// Sony PaSoRi 用の FeliCa カードリーダー実装です。
    /// FelicaLib.DotNet を使用して交通系ICカードを読み取ります。
    /// </summary>
    /// <remarks>
    /// <para>
    /// このクラスは Sony PaSoRi (RC-S380 等) での FeliCa カード読み取りに特化しています。
    /// 残高・履歴データを FelicaLib.DotNet 経由で取得します。
    /// </para>
    /// <para>
    /// <strong>対応カード:</strong>
    /// 交通系ICカード（Suica、PASMO、ICOCA、nimoca、SUGOCA、はやかけん等）
    /// </para>
    /// <para>
    /// <strong>必要条件:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Sony NFCポートソフトウェア がインストールされていること</description></item>
    /// <item><description>felicalib.dll が実行フォルダに配置されていること</description></item>
    /// </list>
    /// <para>
    /// <strong>felicalib.dll について:</strong>
    /// felicalib.dll は tmurakam/felicalib プロジェクト (https://github.com/tmurakam/felicalib) の
    /// 成果物です。このライブラリは BSD-2-Clause ライセンスで配布されています。
    /// Sony NFCポートソフトウェアには含まれないため、別途ビルドまたはリリースから取得してください。
    /// </para>
    /// </remarks>
    public class FelicaCardReader : ICardReader
    {
        private readonly ILogger<FelicaCardReader> _logger;
        private readonly IStationMasterService _stationMasterService;

        /// <summary>
        /// felicalib への呼び出しの継ぎ目（Issue #2301）。本番は <see cref="FelicaUtilityDevice"/>。
        /// </summary>
        private readonly IFelicaDevice _device;

        /// <summary>
        /// FelicaUtility へのアクセスを同期するためのロックオブジェクト。
        /// FelicaLib.DotNet はスレッドセーフではないため、すべてのアクセスを直列化する必要があります。
        /// </summary>
        private readonly object _felicaLock = new object();

        /// <summary>
        /// 最後に読み取ったIDmの同期用ロックオブジェクト
        /// </summary>
        private readonly object _lastReadLock = new object();

        /// <summary>
        /// タイマーの起動・停止と読み取り中フラグを直列化するロックオブジェクト（Issue #2298）。
        /// </summary>
        /// <remarks>
        /// ヘルスチェックはタイマーのスレッドからポーリングを起動し得るため、
        /// 停止・再接続・破棄と同時に走ると、停止したはずのポーリングが生き残る。
        /// felicalib へのアクセス（<see cref="_felicaLock"/>）とは別に持ち、このロックの内側では felicalib を呼ばない。
        /// </remarks>
        private readonly object _lifecycleLock = new object();

        private Timer? _pollingTimer;
        private Timer? _healthCheckTimer;
        private bool _isReading;
        private bool _disposed;
        private CardReaderConnectionState _connectionState = CardReaderConnectionState.Disconnected;

        /// <summary>
        /// ポーリング処理中かどうか（再入防止用）
        /// </summary>
        private volatile bool _isPolling;

        /// <summary>
        /// 最後に読み取ったカードのIDm
        /// </summary>
        private string? _lastReadIdm;

        /// <summary>
        /// 最後にカードを読み取った時刻
        /// </summary>
        private DateTime _lastReadTime = DateTime.MinValue;

        /// <summary>
        /// カードがリーダーから離されたかどうか（置きっぱなし検出用）
        /// </summary>
        /// <remarks>
        /// Issue #323 対応: カードを置きっぱなしにした場合の連続読み取りを防止するためのフラグ。
        /// カードが検出されなくなった（離された）ときに true になり、
        /// 次にカードが検出されたときに false にリセットされる。
        /// 同じカードが検出された場合、このフラグが true の場合のみイベントを発火する。
        /// </remarks>
        private volatile bool _cardWasLifted = true;

        /// <summary>
        /// カード検出のポーリング間隔（ミリ秒）
        /// </summary>
        /// <remarks>
        /// 500ms に設定した理由:
        /// - 短すぎる(100-200ms): CPUリソース消費が大きく、felicalib.dllへの負荷が高い
        /// - 長すぎる(1000ms以上): カードタッチからの反応が遅く、UXが悪化
        /// - 500ms: レスポンスとリソース消費のバランスが良好
        /// - 実測で安定動作を確認済み（300msでは不安定な場合があった）
        /// </remarks>
        private const int PollingIntervalMs = 500;

        /// <summary>
        /// ヘルスチェック間隔（ミリ秒）
        /// </summary>
        private const int HealthCheckIntervalMs = 10000;

        /// <summary>
        /// 最大履歴件数
        /// </summary>
        private const int MaxHistoryCount = 20;

        /// <summary>
        /// ワイルドカードシステムコード（すべてのFeliCaカードを検出）
        /// </summary>
        private const int WildcardSystemCode = 0xFFFF;

        public event EventHandler<CardReadEventArgs>? CardRead;
        public event EventHandler<Exception>? Error;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public bool IsReading => _isReading;
        public CardReaderConnectionState ConnectionState => _connectionState;

        /// <summary>
        /// FelicaCardReader の新しいインスタンスを初期化します。
        /// </summary>
        /// <param name="logger">ロガー</param>
        /// <param name="stationMasterService">駅マスタサービス</param>
        public FelicaCardReader(ILogger<FelicaCardReader> logger, IStationMasterService stationMasterService)
            : this(logger, stationMasterService, FelicaUtilityDevice.Instance)
        {
        }

        /// <summary>
        /// felicalib への呼び出しを差し替えてインスタンスを初期化します（Issue #2301。テスト用）。
        /// </summary>
        /// <remarks>
        /// DI コンテナは public コンストラクターだけを使うため、本番では常に <see cref="FelicaUtilityDevice"/> になる。
        /// </remarks>
        internal FelicaCardReader(ILogger<FelicaCardReader> logger, IStationMasterService stationMasterService, IFelicaDevice device)
        {
            _logger = logger;
            _stationMasterService = stationMasterService ?? throw new ArgumentNullException(nameof(stationMasterService));
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>
        /// カード読み取りを開始します。
        /// </summary>
        /// <remarks>
        /// ポーリングベースでカードを検出し、カードが検出されると <see cref="CardRead"/> イベントを発火します。
        /// </remarks>
        public Task StartReadingAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    // felicalib.dll の存在確認
                    if (!IsFelicaLibLoaded())
                    {
                        var ex = new InvalidOperationException(
                            "felicalib.dll が見つからないか、Sony NFCポートソフトウェアがインストールされていません。");
                        SetConnectionStateForceNotify(CardReaderConnectionState.Disconnected, ex.Message);
                        throw ex;
                    }

                    // PaSoRi の物理接続確認 (Issue #1428)
                    // DLL は存在しても PaSoRi が接続されていない場合は Disconnected として確実に通知する。
                    if (!IsReaderConnected())
                    {
                        _logger.LogWarning("FelicaCardReader: PaSoRi が見つかりません。接続を監視し、接続されたらカード読み取りを開始します");

                        // Issue #2298: 未接続でも接続の監視（ヘルスチェック）だけは始める。
                        // 始めないと、後から接続しても「再接続」ボタンを押すまで認識されない。
                        // ポーリングは接続を検出した時点でヘルスチェックが起動する（DecideHealthCheckAction）。
                        lock (_lifecycleLock)
                        {
                            StopPollingTimer();
                            _isReading = false;
                            StartHealthCheckTimer();
                        }

                        SetConnectionStateForceNotify(CardReaderConnectionState.Disconnected, ReaderNotFoundMessage);
                        return;
                    }

                    _logger.LogInformation("FelicaCardReader: カード読み取りを開始します");

                    lock (_lifecycleLock)
                    {
                        StartPollingTimer();
                        StartHealthCheckTimer();
                        _isReading = true;
                    }

                    SetConnectionStateForceNotify(CardReaderConnectionState.Connected);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FelicaCardReader: 初期化エラー");
                    var cardReaderException = CardReaderException.ReadFailed(ex.Message, ex);
                    SetConnectionState(CardReaderConnectionState.Disconnected, cardReaderException.UserFriendlyMessage);
                    Error?.Invoke(this, cardReaderException);
                    throw cardReaderException;
                }
            });
        }

        /// <summary>
        /// カード読み取りを停止します。
        /// </summary>
        public Task StopReadingAsync()
        {
            return Task.Run(() =>
            {
                lock (_lifecycleLock)
                {
                    StopPollingTimer();
                    StopHealthCheckTimer();
                    _isReading = false;
                }

                _lastReadIdm = null;
                SetConnectionState(CardReaderConnectionState.Disconnected);

                _logger.LogInformation("FelicaCardReader: カード読み取りを停止しました");
            });
        }

        /// <summary>
        /// カードから履歴を読み取ります（後方互換用）。
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        /// <returns>利用履歴詳細のリスト（最大20件、新しい順）。読み取れなかった場合は空のリスト</returns>
        /// <remarks>
        /// <para>
        /// 読み取り前にカードの IDm を検証し、指定されたカードと一致しない場合（別のカードが載っている）は空のリストを返します。
        /// felicalib の例外（カードが離れた場合を含む）は <see cref="Error"/> を発火して空のリストを返します。
        /// </para>
        /// <para>
        /// Issue #2301 で足した「読み取り後の確認」は、このメソッドでは<b>別のカードが載っていた</b>（IDm の不一致）ときだけ使い、
        /// 空のリストを返します（読んだのは載せ替えた先のカードの履歴である可能性が高く、登録すると別のカードの履歴が導入行・履歴になる。
        /// 読み取り前の不一致を空にするのと同じ扱い）。確認ができなかった（カードが離れた・リーダーの例外）ときは読めた明細を従来どおり返し、
        /// <see cref="Error"/> も発火しません。呼び出し元はカード登録時の事前読み取りで、警告音の直後に離されやすく、
        /// ここで捨てると以前なら取り込めていた登録が黙って履歴なしになるためです（返却の不具合を直すために登録の挙動を変えない）。
        /// 残る危険: 確認の後に別のカードへ載せ替え、そのカードを後の確認より前に離すと「見失った」になり、別のカードの履歴を返し得る
        /// （登録の挙動を変えないために受け入れている。数百ミリ秒の間に載せ替えて離す操作が要るため極めて稀）。
        /// 空のリストは「履歴ゼロ件」と区別できないので、区別が要る処理は <see cref="TryReadHistoryAsync"/> を使うこと。
        /// </para>
        /// </remarks>
        public async Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm)
        {
            var outcome = await Task.Run(() => ReadHistoryCore(idm)).ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case HistoryReadKind.ReadFailed:
                    Error?.Invoke(this, CardReaderException.HistoryReadFailed(outcome.Exception?.Message, outcome.Exception));
                    return new List<LedgerDetail>();

                case HistoryReadKind.NotPresentBeforeRead:
                case HistoryReadKind.OtherCardAfterRead:
                    return new List<LedgerDetail>();

                default:
                    // Succeeded・LostAfterRead・ReaderFailedAfterRead（確認できなかっただけなので従来どおり返す。上の remarks）
                    return outcome.Details;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Issue #2301: 対象のカードが載っていなかった場合は <see cref="CardReaderException.CardRemoved"/> で <c>Fail</c> を返し、
        /// <see cref="Error"/> は発火しない（利用者の操作でありリーダーの故障ではないため。警告エリアへ「カードリーダーエラー」を出さない）。
        /// 以前は読み取り前の IDm の不一致（別のカードが載っている）を <c>Ok(空リスト)</c> としていたため、
        /// 返却で「利用 0 件の返却」が記録されていた。「載っていなかった」に当たるのは次のとおり。
        /// </para>
        /// <list type="bullet">
        /// <item><description>読み取りの前に、別のカードが載っている（IDm の不一致。カード無しで <c>GetIDm</c> が null の場合も含める）</description></item>
        /// <item><description>読み取りの後に、別のカードが載っている・カードを見失った例外が出た。確認の後・ブロックを読む前に載せ替えると、felicalib は
        /// <b>載せ替えた先のカードの履歴</b>を読む（1.2.67 は <c>ReadBlocksWithoutEncryption</c> のたびにポーリングし直す）</description></item>
        /// <item><description>felicalib がカードを見失った例外（<see cref="IsCardNotPresentException"/>）。実機で「タッチしてすぐ離す」と多くはこれになる</description></item>
        /// </list>
        /// <para>
        /// それ以外の felicalib の例外は、読み取りの前・途中・後のどこで出ても <see cref="Error"/> を発火したうえで <c>Fail</c>（<see cref="CardReaderException.HistoryReadFailed"/>）を返す
        /// （読み終えた直後に USB が抜けた等を「交通系ICカードが離れました」と表示しない）。
        /// </para>
        /// </remarks>
        public async Task<CardReadResult<IReadOnlyList<LedgerDetail>>> TryReadHistoryAsync(string idm)
        {
            var outcome = await Task.Run(() => ReadHistoryCore(idm)).ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case HistoryReadKind.Succeeded:
                    return CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(outcome.Details);

                case HistoryReadKind.ReadFailed when !IsCardNotPresentException(outcome.Exception):
                case HistoryReadKind.ReaderFailedAfterRead:
                    var readerError = CardReaderException.HistoryReadFailed(outcome.Exception?.Message, outcome.Exception);
                    Error?.Invoke(this, readerError);
                    return CardReadResult<IReadOnlyList<LedgerDetail>>.Fail(readerError);

                default:
                    // NotPresentBeforeRead・OtherCardAfterRead・LostAfterRead・カードを見失った例外
                    return CardReadResult<IReadOnlyList<LedgerDetail>>.Fail(CardReaderException.CardRemoved(outcome.Exception));
            }
        }

        /// <summary>
        /// felicalib の例外が「読み取り対象のカードを見失った」ことを示すかを判定する純粋関数（Issue #2301）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// FelicaLib.DotNet 1.2.67 の <c>Felica.TransferData</c> は呼び出しのたびに PaSoRi を開いてポーリングし、カードが無ければ
        /// <see cref="InvalidOperationException"/>（<see cref="FelicaLibCardNotFoundMessage"/>）を投げる（<c>GetIDm</c> は null を返さない）。
        /// ブロックの読み取りに失敗すると同じ型で <see cref="FelicaLibBlockNotReadMessage"/> を投げる（読めなかったブロックを null で返さない）。
        /// 履歴の読み取りはカードを検出した直後に行うため、どちらも「カードが離れた・載せ替えられた」と読む。
        /// 後者は「対象のカード（はやかけん・nimoca・SUGOCA 等の Suica 互換の履歴サービス 0x090F）は常に 20 ブロックを返し、未使用は 0 で埋める」
        /// という前提に立つ（本システムが 20 件を読むのも同じ前提）。<b>全種類のカードで実機確認はしていない未確認の前提</b>で、
        /// 載ったままでも 20 ブロック読めないカードがあれば「交通系ICカードが離れました」と誤って案内する（表示だけの誤りで、返却は記録しない）。
        /// どちらも DLL を ildasm で逆アセンブルして確かめた（文言は DLL 内の文字列リテラル）。
        /// </para>
        /// <para>
        /// 文言での判定は felicalib の版で変わり得る（<see cref="IsReaderUnavailableException"/> と同じ弱さ）。変わった場合は
        /// リーダーの例外（<see cref="CardReaderException.HistoryReadFailed"/>）に倒れるだけで、返却が記録されることはない。
        /// </para>
        /// </remarks>
        internal static bool IsCardNotPresentException(Exception? ex)
        {
            if (!(ex is InvalidOperationException) || ex.Message is null)
            {
                return false;
            }

            return ex.Message.IndexOf(FelicaLibCardNotFoundMessage, StringComparison.Ordinal) >= 0
                || ex.Message.IndexOf(FelicaLibBlockNotReadMessage, StringComparison.Ordinal) >= 0;
        }

        /// <summary>FelicaLib.DotNet 1.2.67 がポーリングでカードを見つけられないときの例外メッセージ（の先頭）</summary>
        internal const string FelicaLibCardNotFoundMessage = "IC カードが見つかりません。";

        /// <summary>FelicaLib.DotNet 1.2.67 がブロックを読めないときの例外メッセージ</summary>
        internal const string FelicaLibBlockNotReadMessage = "指定されたサービス コードおよびアドレスのデータが存在しません。";

        /// <summary>履歴の読み取り 1 回の結果の種類（Issue #2301）</summary>
        private enum HistoryReadKind
        {
            /// <summary>読み取りの前後とも対象のカードが載っていた</summary>
            Succeeded,

            /// <summary>読み取りの前に、別のカードが載っていた（またはカード無し）。ブロックは読んでいない</summary>
            NotPresentBeforeRead,

            /// <summary>ブロックは読めたが、読み取りの後に別のカードが載っていた（読んだのは別のカードの履歴の可能性が高い）</summary>
            OtherCardAfterRead,

            /// <summary>ブロックは読めたが、読み取りの後にカードを見失った（カードを見失った例外、または <c>GetIDm</c> が null）</summary>
            LostAfterRead,

            /// <summary>ブロックは読めたが、読み取りの後の確認でリーダーの例外が出た（USB が抜けた等）</summary>
            ReaderFailedAfterRead,

            /// <summary>読み取りの前か途中で felicalib が例外を投げた（カードを見失った場合を含む）</summary>
            ReadFailed,
        }

        /// <summary>
        /// 履歴の読み取り 1 回の結果（Issue #2301）。成否の判断は呼び出し元（<see cref="ReadHistoryAsync"/> / <see cref="TryReadHistoryAsync"/>）が行う。
        /// </summary>
        private sealed class HistoryReadOutcome
        {
            public HistoryReadOutcome(HistoryReadKind kind, List<LedgerDetail> details, Exception? exception = null)
            {
                Kind = kind;
                Details = details;
                Exception = exception;
            }

            public HistoryReadKind Kind { get; }

            /// <summary>読めた明細（読み取り後の確認で失敗した種類でも、読めたぶんを持つ）</summary>
            public List<LedgerDetail> Details { get; }

            /// <summary>例外による種類（<see cref="HistoryReadKind.ReadFailed"/> 等）の原因</summary>
            public Exception? Exception { get; }
        }

        /// <summary>
        /// 履歴を読み取る本体（<see cref="ReadHistoryAsync"/> と <see cref="TryReadHistoryAsync"/> が共有する。Issue #2301）。
        /// </summary>
        /// <remarks>
        /// 読み取りの<b>前と後</b>で対象のカードが載っていることを確かめる。前の確認と <c>ReadBlocksWithoutEncryption</c> は
        /// felicalib の別々の呼び出し（それぞれがポーリングし直す）なので、その間に載せ替えると別のカードの履歴を読む。
        /// 後の確認の結果は「別のカード」「カードを見失った」「リーダーの例外」に分けて返す（後方互換の <see cref="ReadHistoryAsync"/> が扱いを分けるため）。
        /// </remarks>
        private HistoryReadOutcome ReadHistoryCore(string idm)
        {
            lock (_felicaLock)
            {
                List<LedgerDetail> details;
                try
                {
                    // IDm検証: 読み取り対象のカードが載っていることを確認
                    var currentIdm = GetIdmString(_device.GetIDm(FelicaSystemCode.Suica));
                    if (!IsSameCard(idm, currentIdm, "履歴読み取り前"))
                    {
                        return new HistoryReadOutcome(HistoryReadKind.NotPresentBeforeRead, new List<LedgerDetail>());
                    }

                    // FelicaUtility.ReadBlocksWithoutEncryption で複数ブロックを一括取得
                    var historyDataList = new List<byte[]>();

                    _logger.LogDebug("FelicaCardReader: 履歴読み取り開始 (システムコード=0x{SystemCode:X4}, サービスコード=0x{ServiceCode:X4})",
                        (int)FelicaSystemCode.Suica, (int)FelicaServiceCode.SuicaHistory);

                    int blockIndex = 0;
                    foreach (var data in _device.ReadBlocksWithoutEncryption(
                        FelicaSystemCode.Suica,
                        FelicaServiceCode.SuicaHistory,
                        0,
                        MaxHistoryCount))
                    {
                        // FelicaLib.DotNet 1.2.67 は読めなかったブロックを null で返さず例外を投げる（IsCardNotPresentException）。
                        // null の打ち切りは版が変わったときのための防御として残す
                        if (data == null)
                        {
                            _logger.LogDebug("FelicaCardReader: ブロック{Index}はnull", blockIndex);
                            break;
                        }
                        if (data.All(b => b == 0))
                        {
                            _logger.LogDebug("FelicaCardReader: ブロック{Index}は全て0", blockIndex);
                            break;
                        }
                        _logger.LogDebug("FelicaCardReader: ブロック{Index}: {Data}", blockIndex, BitConverter.ToString(data));
                        historyDataList.Add(data);
                        blockIndex++;
                    }

                    _logger.LogInformation("FelicaCardReader: 履歴データを {Count} 件取得", historyDataList.Count);

                    // 駅名解決にはCardType.Unknownを使用
                    // 注: IDmの先頭2バイトは製造者コードであり、カード種別ではないため、
                    //     CardTypeDetector.DetectFromIdmは信頼できない
                    //     StationMasterServiceのUnknown優先順位（九州優先）が使用される
                    var cardType = CardType.Unknown;

                    // 履歴データをパースして金額を計算
                    details = new List<LedgerDetail>();
                    for (int i = 0; i < historyDataList.Count; i++)
                    {
                        var currentData = historyDataList[i];
                        var nextData = i + 1 < historyDataList.Count ? historyDataList[i + 1] : null;

                        var detail = ParseHistoryData(currentData, nextData, cardType);
                        if (detail != null)
                        {
                            details.Add(detail);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (IsCardNotPresentException(ex))
                    {
                        // Issue #2301: カードが離れたのは利用者の操作。本番ログ（Information 以上）へは Warning で残す
                        _logger.LogWarning(ex, "FelicaCardReader: 履歴読み取り中に交通系ICカードを見失いました（カードが離れた・載せ替えられた）");
                    }
                    else
                    {
                        _logger.LogError(ex, "FelicaCardReader: 履歴読み取りエラー");
                    }

                    return new HistoryReadOutcome(HistoryReadKind.ReadFailed, new List<LedgerDetail>(), ex);
                }

                // Issue #2301: 読み取りの後にも対象のカードが載っていることを確認する
                string? idmAfterRead;
                try
                {
                    idmAfterRead = GetIdmString(_device.GetIDm(FelicaSystemCode.Suica));
                }
                catch (Exception ex) when (IsCardNotPresentException(ex))
                {
                    // カードが離れると felicalib はポーリングに失敗して例外を投げる
                    _logger.LogWarning(ex, "FelicaCardReader: 履歴読み取り後の確認でカードを検出できませんでした（カードが離れた）");
                    return new HistoryReadOutcome(HistoryReadKind.LostAfterRead, details, ex);
                }
                catch (Exception ex)
                {
                    // USB が抜けた等。カードの操作ではないので「離れた」とは扱わない
                    _logger.LogError(ex, "FelicaCardReader: 履歴読み取り後の確認でエラーが発生しました");
                    return new HistoryReadOutcome(HistoryReadKind.ReaderFailedAfterRead, details, ex);
                }

                if (IsSameCard(idm, idmAfterRead, "履歴読み取り後"))
                {
                    return new HistoryReadOutcome(HistoryReadKind.Succeeded, details);
                }

                return idmAfterRead is null
                    ? new HistoryReadOutcome(HistoryReadKind.LostAfterRead, details)
                    : new HistoryReadOutcome(HistoryReadKind.OtherCardAfterRead, details);
            }
        }

        /// <summary>
        /// 読み取ったカードの IDm が読み取り対象と同じかを確かめ、違えばマスクしてログへ残す（Issue #2301）。
        /// </summary>
        /// <param name="idm">読み取り対象のカードの IDm</param>
        /// <param name="currentIdm">いまリーダーに載っているカードの IDm（カード無しなら null）</param>
        /// <param name="phase">ログに残す確認の時点（「履歴読み取り前」等）</param>
        private bool IsSameCard(string idm, string? currentIdm, string phase)
        {
            if (string.Equals(currentIdm, idm, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Issue #1704: IDm は認証クレデンシャルのためログにはマスクして出力する
            _logger.LogWarning("FelicaCardReader: {Phase}にIDmが一致しません（カードが離れた・載せ替えられた）。期待={Expected}, 実際={Actual}",
                phase, IdmMasker.Mask(idm), IdmMasker.Mask(currentIdm));
            return false;
        }

        /// <summary>
        /// カードの残高を読み取ります。
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        /// <returns>残高（円）。読み取り失敗時は null</returns>
        /// <remarks>
        /// <para>
        /// 読み取り前にカードのIDmを検証し、指定されたカードと一致することを確認します。
        /// カードが載せ替えられた場合は null を返します。
        /// </para>
        /// <para>
        /// 残高は履歴サービス(0x090F)の最新レコード（ブロック0）から取得します。
        /// バイト10-11にリトルエンディアンで残高が格納されています。
        /// </para>
        /// <para>
        /// 注: 残高専用サービス(0x008B)は使用しません。一部のカードでは
        /// バイト位置が異なる場合があるためです (Issue #465)。
        /// </para>
        /// </remarks>
        public async Task<int?> ReadBalanceAsync(string idm)
        {
            return await Task.Run<int?>(() =>
            {
                lock (_felicaLock)
                {
                    try
                    {
                        // IDm検証: 読み取り対象のカードが載っていることを確認
                        var currentIdmBytes = _device.GetIDm(FelicaSystemCode.Suica);
                        var currentIdm = GetIdmString(currentIdmBytes);
                        if (!string.Equals(currentIdm, idm, StringComparison.OrdinalIgnoreCase))
                        {
                            // Issue #1704: IDm は認証クレデンシャルのためログにはマスクして出力する
                            _logger.LogWarning("FelicaCardReader: 残高読み取り時にIDmが一致しません。期待={Expected}, 実際={Actual}", IdmMasker.Mask(idm), IdmMasker.Mask(currentIdm));
                            return null;
                        }

                        // 履歴サービスコードの最新レコードから残高を取得
                        // 注: 残高サービス(0x008B)はバイト位置が異なる場合があるため、
                        //     履歴サービス(0x090F)を使用する（Issue #465対応）
                        _logger.LogDebug("FelicaCardReader: 履歴サービス(0x{ServiceCode:X4})から読み取り試行", (int)FelicaServiceCode.SuicaHistory);
                        var historyData = _device.ReadWithoutEncryption(
                            FelicaSystemCode.Suica,
                            FelicaServiceCode.SuicaHistory,
                            0);

                        if (historyData != null && historyData.Length >= 12)
                        {
                            _logger.LogDebug("FelicaCardReader: 履歴サービスデータ: {Data}", BitConverter.ToString(historyData));
                            // バイト10-11が残高（リトルエンディアン）
                            var balance = historyData[10] + (historyData[11] << 8);
                            _logger.LogDebug("FelicaCardReader: 残高読み取り成功（履歴サービス）: {Balance}円 (byte10=0x{Byte10:X2}, byte11=0x{Byte11:X2})",
                                balance, historyData[10], historyData[11]);
                            return balance;
                        }
                        else
                        {
                            _logger.LogDebug("FelicaCardReader: 履歴サービスからのデータがnullまたは短い: Length={Length}", historyData?.Length ?? -1);
                        }

                        _logger.LogWarning("FelicaCardReader: 残高データを取得できませんでした");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "FelicaCardReader: 残高読み取りエラー");
                        Error?.Invoke(this, CardReaderException.BalanceReadFailed(ex.Message, ex));
                        return null;
                    }
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 接続状態を確認します。
        /// </summary>
        /// <returns>接続中の場合 true</returns>
        public Task<bool> CheckConnectionAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    return IsFelicaLibLoaded() && IsReaderConnected();
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// 再接続を試行します。
        /// </summary>
        public async Task ReconnectAsync()
        {
            _logger.LogInformation("FelicaCardReader: 再接続を試行します");

            await StopReadingAsync().ConfigureAwait(false);

            try
            {
                await StartReadingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FelicaCardReader: 再接続に失敗しました");
            }
        }

        #region Private Methods

        /// <summary>
        /// felicalib.dll がロード可能かチェック (Issue #1428)
        /// </summary>
        /// <remarks>
        /// DLL の存在チェックのみを行い、PaSoRi の物理接続は判定しない。
        /// PaSoRi の物理接続を判定するには <see cref="IsReaderConnected"/> を併用すること。
        /// </remarks>
        private bool IsFelicaLibLoaded()
        {
            lock (_felicaLock)
            {
                try
                {
                    _ = _device.GetIDm(WildcardSystemCode);
                    return true;
                }
                catch (DllNotFoundException)
                {
                    _logger.LogError("FelicaCardReader: felicalib.dll が見つかりません");
                    return false;
                }
                catch (Exception ex)
                {
                    // PaSoRi 未接続・カードなし等は DLL 自体は存在する。
                    _logger.LogDebug("FelicaCardReader: DLL ロード判定中の例外: {Message}", ex.Message);
                    return true;
                }
            }
        }

        /// <summary>
        /// PaSoRi が物理接続されているかチェック (Issue #1428)
        /// </summary>
        /// <remarks>
        /// DLL ロード可否を兼ねて判定する。<see cref="FelicaUtility.GetIDm"/> の例外メッセージから
        /// 「リーダー未接続」相当を判別し、それ以外（カードなし等）は接続中として扱う。
        /// </remarks>
        private bool IsReaderConnected()
        {
            lock (_felicaLock)
            {
                try
                {
                    _ = _device.GetIDm(WildcardSystemCode);
                    return true;
                }
                catch (Exception ex) when (IsReaderUnavailableException(ex))
                {
                    _logger.LogDebug("FelicaCardReader: PaSoRi 未接続と判定: {Message}", ex.Message);
                    return false;
                }
                catch (Exception ex)
                {
                    // カードなし等は接続中扱い。
                    _logger.LogDebug("FelicaCardReader: 接続判定中の例外（カードなし等）: {Message}", ex.Message);
                    return true;
                }
            }
        }

        /// <summary>
        /// 例外メッセージから「PaSoRi 物理未接続」を示すパターンを判定する純粋関数 (Issue #1428)。
        /// </summary>
        /// <remarks>
        /// felicalib の例外メッセージは将来変わる可能性があるため、
        /// 表示層 (<c>MainWindow.xaml</c>) のデフォルト Setter「切断」によるフォールバックと併用する。
        /// internal 公開でテストから直接検証可能にする。
        /// </remarks>
        internal static bool IsReaderUnavailableException(Exception? ex)
        {
            if (ex is DllNotFoundException)
            {
                return true;
            }

            var message = ex?.Message;
            if (message is null || string.IsNullOrEmpty(message))
            {
                return false;
            }

            // 大文字小文字を無視して PaSoRi 物理未接続を示すキーワードを判定。
            return message.IndexOf("pasori", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("device", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("reader", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// ポーリングタイマーを開始
        /// </summary>
        private void StartPollingTimer()
        {
            StopPollingTimer();

            _pollingTimer = new Timer(PollingIntervalMs);
            _pollingTimer.Elapsed += OnPollingTimerElapsed;
            _pollingTimer.AutoReset = true;
            _pollingTimer.Start();

            _logger.LogDebug("FelicaCardReader: ポーリングタイマー開始 ({Interval}ms)", PollingIntervalMs);
        }

        /// <summary>
        /// ポーリングタイマーを停止
        /// </summary>
        private void StopPollingTimer()
        {
            if (_pollingTimer != null)
            {
                _pollingTimer.Stop();
                _pollingTimer.Elapsed -= OnPollingTimerElapsed;
                _pollingTimer.Dispose();
                _pollingTimer = null;
            }
        }

        /// <summary>
        /// ポーリングタイマーのイベントハンドラ
        /// </summary>
        private void OnPollingTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 再入防止: 前回のポーリングが完了していない場合はスキップ
            if (_isPolling)
            {
                return;
            }

            try
            {
                _isPolling = true;

                string? idm;

                // FelicaUtility へのアクセスはすべてロックで保護
                lock (_felicaLock)
                {
                    // ワイルドカードシステムコード（0xFFFF）ですべてのFeliCaカードを検出
                    // これにより、交通系ICカードだけでなく職員証なども検出できる
                    var idmBytes = _device.GetIDm(WildcardSystemCode);
                    if (idmBytes == null || idmBytes.Length == 0)
                    {
                        // カードが検出されない = カードが離された
                        // Issue #323: 次回同じカードが検出されたときにイベントを発火するためフラグを立てる
                        _cardWasLifted = true;
                        return;
                    }

                    idm = GetIdmString(idmBytes);
                }

                if (idm is null || string.IsNullOrEmpty(idm))
                {
                    _cardWasLifted = true;
                    return;
                }

                // 同一カードの連続読み取りを防止（スレッドセーフ）
                // Issue #323: カードを置きっぱなしにした場合の連続読み取りを防止
                lock (_lastReadLock)
                {
                    var now = DateTime.Now;

                    // 同一カードの場合
                    if (idm == _lastReadIdm)
                    {
                        // 【重要】まずカード離脱をチェック
                        // カードが離されていない場合（置きっぱなし）は常に無視
                        if (!_cardWasLifted)
                        {
                            return;
                        }

                        // カードが離された場合：
                        // 30秒ルール（誤操作キャンセル機能）を正しく動作させるため、
                        // 時間制限は設けない。
                        // ポーリング間隔（PollingIntervalMs = 500ms）が自然なチャタリング防止となる。
                    }

                    // 新しいカード、または離されてから再度置かれたカードとして処理
                    _lastReadIdm = idm;
                    _lastReadTime = now;
                    _cardWasLifted = false;  // カードが検出されたのでフラグをリセット
                }

                // Issue #1704: IDm は認証クレデンシャルのためログにはマスクして出力する
                _logger.LogInformation("FelicaCardReader: カード検出 IDm={Idm}", IdmMasker.Mask(idm));

                // イベントを発火（UIスレッドで処理されるため、ここでは即座に返る）
                CardRead?.Invoke(this, new CardReadEventArgs
                {
                    Idm = idm,
                    SystemCode = FelicaSystemCode.Suica.ToString("X4", CultureInfo.InvariantCulture)
                });
            }
            catch (Exception ex)
            {
                // Polling 失敗はカードが載っていない場合も含むので、Traceレベルでログ出力
                // 通常運用時は出力されず、詳細デバッグ時のみ確認可能
                _logger.LogTrace("FelicaCardReader: ポーリング例外（カードなし等）: {Message}", ex.Message);

                // Issue #323: 例外発生時もカード離脱とみなす
                // FelicaUtility.GetIDm() はカードがない場合に null を返すこともあれば
                // 例外をスローすることもある（ライブラリの実装による）
                // どちらの場合でもカードが離された状態として扱う
                _cardWasLifted = true;
            }
            finally
            {
                _isPolling = false;
            }
        }

        /// <summary>
        /// ヘルスチェックタイマーを開始
        /// </summary>
        private void StartHealthCheckTimer()
        {
            StopHealthCheckTimer();

            // Issue #1819: 読み取り開始（再接続ボタン等）は新しい監視セッションの始まり。
            // 連続失敗回数を持ち越すと「初回は必ず本番ログへ残す」保証が効かず
            // （持ち越した回数が間隔の倍数に当たるまで無言になる）、
            // 「連続N回目」が再接続をまたいだ値になって継続期間の追跡もできなくなる。
            _consecutiveHealthCheckFailures = 0;

            _healthCheckTimer = new Timer(HealthCheckIntervalMs);
            _healthCheckTimer.Elapsed += OnHealthCheckTimerElapsed;
            _healthCheckTimer.AutoReset = true;
            _healthCheckTimer.Start();
        }

        /// <summary>
        /// ヘルスチェックタイマーを停止
        /// </summary>
        private void StopHealthCheckTimer()
        {
            if (_healthCheckTimer != null)
            {
                _healthCheckTimer.Stop();
                _healthCheckTimer.Elapsed -= OnHealthCheckTimerElapsed;
                _healthCheckTimer.Dispose();
                _healthCheckTimer = null;
            }
        }

        /// <summary>
        /// ヘルスチェック例外の連続発生回数（Issue #1819）
        /// </summary>
        private int _consecutiveHealthCheckFailures;

        /// <summary>
        /// ヘルスチェック失敗を本番ログへ残す間隔（回数）。
        /// </summary>
        /// <remarks>
        /// ヘルスチェックは <see cref="HealthCheckIntervalMs"/> 周期で走るため、30 回はおよそ 5 分に相当する。
        /// </remarks>
        internal const int HealthCheckFailureLogIntervalCount = 30;

        /// <summary>
        /// ヘルスチェック失敗を本番ログ（Warning）へ残すべきかを判定する（Issue #1819）。
        /// </summary>
        /// <param name="consecutiveFailureCount">連続失敗回数（1 始まり）</param>
        /// <returns>初回、および <see cref="HealthCheckFailureLogIntervalCount"/> 回ごとに true</returns>
        /// <remarks>
        /// 10 秒周期で毎回出すとログが肥大化して他の事象が埋もれるため、
        /// 「発生した事実」（初回）と「続いている事実」（一定間隔）だけを残す。
        /// 実機 felicalib に依存せず検証できるよう純粋関数として切り出している。
        /// </remarks>
        internal static bool ShouldLogHealthCheckFailure(int consecutiveFailureCount)
        {
            if (consecutiveFailureCount <= 0)
            {
                return false;
            }

            return consecutiveFailureCount == 1
                || consecutiveFailureCount % HealthCheckFailureLogIntervalCount == 0;
        }

        /// <summary>
        /// <paramref name="sender"/> が現役のヘルスチェックタイマーかを返す（Issue #2298）。
        /// </summary>
        private bool IsCurrentHealthCheckTimer(object sender)
        {
            lock (_lifecycleLock)
            {
                return ReferenceEquals(sender, _healthCheckTimer);
            }
        }

        /// <summary>
        /// 起動時に PaSoRi が見つからなかったときの案内（Issue #1428 / #2298）。
        /// </summary>
        /// <remarks>
        /// メイン画面の警告エリアに「カードリーダー切断: 」に続けて表示される。
        /// 未接続でもヘルスチェックが接続を監視しているので、接続すれば再接続ボタンを押さなくても認識される。
        /// </remarks>
        internal const string ReaderNotFoundMessage =
            "PaSoRi が見つかりません。USB で接続してください（接続すると自動で認識します）。";

        /// <summary>
        /// ヘルスチェック 1 回の結果として行う処理（Issue #2298）。
        /// </summary>
        internal enum HealthCheckAction
        {
            /// <summary>状態を変えない</summary>
            None,

            /// <summary>接続中だったカードリーダーが見えなくなったので切断として通知する</summary>
            MarkDisconnected,

            /// <summary>読み取り中に切断されていたカードリーダーが見えるようになったので回復として通知する</summary>
            MarkRecovered,

            /// <summary>読み取りを始めていない（起動時に未接続だった）カードリーダーが見えるようになったので、ポーリングを起動して接続中として通知する</summary>
            StartReading,
        }

        /// <summary>
        /// ヘルスチェックの結果から、行う処理を決める（Issue #2298）。
        /// </summary>
        /// <param name="isAvailable">felicalib が読み込め、PaSoRi が接続されているか</param>
        /// <param name="state">現在通知している接続状態</param>
        /// <param name="isReading">ポーリングを起動済みか（起動時に未接続だった場合は false）</param>
        /// <remarks>
        /// <para>
        /// 起動時に未接続だった場合はポーリングが動いていないため、使用中の抜き差し（<see cref="HealthCheckAction.MarkRecovered"/>）と
        /// 同じく Connected を通知するだけでは「接続中」なのにカードを読まない。ポーリングを起動する必要があるのはこの組だけで、
        /// 読み取り中に起動し直すとポーリングを作り直すことになる。
        /// </para>
        /// <para>
        /// 接続状態は Connected / Disconnected の 2 つだけ（Issue #2327 で到達しない「再接続中」を撤去した）。
        /// 実機 felicalib に依存せず検証できるよう純粋関数として切り出している（<see cref="ShouldLogHealthCheckFailure"/> と同じ形）。
        /// </para>
        /// </remarks>
        internal static HealthCheckAction DecideHealthCheckAction(
            bool isAvailable, CardReaderConnectionState state, bool isReading)
        {
            if (!isAvailable)
            {
                return state == CardReaderConnectionState.Connected
                    ? HealthCheckAction.MarkDisconnected
                    : HealthCheckAction.None;
            }

            if (state != CardReaderConnectionState.Disconnected)
            {
                return HealthCheckAction.None;
            }

            return isReading ? HealthCheckAction.MarkRecovered : HealthCheckAction.StartReading;
        }

        /// <summary>
        /// ヘルスチェックタイマーのイベントハンドラ
        /// </summary>
        private void OnHealthCheckTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // ポーリング中はヘルスチェックをスキップ（デッドロック防止）
            if (_isPolling)
            {
                return;
            }

            try
            {
                // Issue #1428: DLL ロードと PaSoRi 物理接続を別々に判定し、
                // 「DLL 不在」と「PaSoRi 未接続」を統一して切断扱いにする。
                // felicalib を呼ぶ判定は _lifecycleLock の外で行う（ロックの内側で felicalib を待たない）。
                var isAvailable = IsFelicaLibLoaded() && IsReaderConnected();

                HealthCheckAction action;
                lock (_lifecycleLock)
                {
                    // Issue #2298: 停止済みのタイマーから遅れて届いたコールバックは無視する。
                    // System.Timers.Timer は Stop() の後もキュー済みの Elapsed を配ることがあり、
                    // 再接続（停止 → 開始）の途中で古いコールバックが読み取りを始めると、停止したはずのポーリングが生き残る。
                    if (!ReferenceEquals(sender, _healthCheckTimer))
                    {
                        return;
                    }

                    action = DecideHealthCheckAction(isAvailable, _connectionState, _isReading);

                    // Issue #2298: 起動時に未接続だった場合は、接続を検出した時点でポーリングを起動する。
                    // 起動せずに Connected を通知すると「接続中」なのにカードを読まない状態になる。
                    if (action == HealthCheckAction.StartReading)
                    {
                        StartPollingTimer();
                        _isReading = true;
                    }
                }

                // 判定から通知までの間に停止・再接続が割り込むと、遅れた通知が新しい状態を上書きする
                // （再接続が Connected を通知した後に、古い確認の Disconnected が届く等）。通知の直前にも現役のタイマーかを確かめる。
                // 通知そのものはロックの外で行う（購読者をロックの内側で呼ばない）。
                if (action != HealthCheckAction.None && !IsCurrentHealthCheckTimer(sender))
                {
                    return;
                }

                switch (action)
                {
                    case HealthCheckAction.MarkDisconnected:
                        _logger.LogWarning("FelicaCardReader: 接続が失われました");
                        SetConnectionState(CardReaderConnectionState.Disconnected, "接続が失われました");
                        break;

                    case HealthCheckAction.MarkRecovered:
                        _logger.LogInformation("FelicaCardReader: 接続が回復しました");
                        SetConnectionState(CardReaderConnectionState.Connected, "接続が回復しました");
                        break;

                    case HealthCheckAction.StartReading:
                        _logger.LogInformation("FelicaCardReader: PaSoRi の接続を検出したため、カード読み取りを開始しました");
                        SetConnectionState(CardReaderConnectionState.Connected, "カードリーダーの接続を検出しました");
                        break;
                }

                // Issue #1819: 例外が止んだことも本番ログへ残す（失敗の Warning と対で調査できるようにする）。
                if (_consecutiveHealthCheckFailures > 0)
                {
                    _logger.LogInformation(
                        "FelicaCardReader: ヘルスチェックが回復しました（連続失敗={FailureCount}回で終了, 接続状態={State}）",
                        _consecutiveHealthCheckFailures, _connectionState);
                    _consecutiveHealthCheckFailures = 0;
                }
            }
            catch (Exception ex)
            {
                // ヘルスチェックの例外はアプリをクラッシュさせない。
                // Issue #1730: LogTrace は本番（Logging:LogLevel:Default=Information）でファイル出力されないが、
                // 例外オブジェクトごと残せる開発時の価値があるため消さずに併設する。
                _logger.LogTrace("FelicaCardReader: ヘルスチェック例外: {Message}", ex.Message);

                _consecutiveHealthCheckFailures++;

                // Issue #1819: 10 秒周期のため毎回 Warning を出すとログが肥大化する。
                // 初回と一定間隔ごとに限って本番ログへ残す（判定は ShouldLogHealthCheckFailure）。
                if (ShouldLogHealthCheckFailure(_consecutiveHealthCheckFailures))
                {
                    _logger.LogWarning(ex,
                        "FelicaCardReader: ヘルスチェックに失敗しました（連続{FailureCount}回目, 接続状態={State}, 例外={ExceptionType}）。" +
                        "接続状態の表示が実際のカードリーダーと食い違っている可能性があります",
                        _consecutiveHealthCheckFailures, _connectionState, ex.GetType().Name);
                }
            }
        }

        /// <summary>
        /// 接続状態を設定し、状態が変化した場合のみイベントを発火する。
        /// </summary>
        private void SetConnectionState(CardReaderConnectionState state, string? message = null)
        {
            if (_connectionState == state)
            {
                return;
            }

            _connectionState = state;
            _logger.LogDebug("FelicaCardReader: 接続状態変更 {State} ({Message})", state, message);
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, message));
        }

        /// <summary>
        /// 接続状態を設定しイベントを必ず発火する (Issue #1428)。
        /// </summary>
        /// <remarks>
        /// 初期化時は内部状態と通知すべき状態が一致するケース（初期値 Disconnected → Disconnected 確定）が
        /// あり、<see cref="SetConnectionState"/> では通知が抑止されてしまう。
        /// 起動時の初回確定通知に限り本メソッドを使用し、ViewModel に確実に状態を伝播させる。
        /// </remarks>
        private void SetConnectionStateForceNotify(CardReaderConnectionState state, string? message = null)
        {
            _connectionState = state;
            _logger.LogDebug("FelicaCardReader: 接続状態確定 {State} ({Message})", state, message);
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, message));
        }

        /// <summary>
        /// IDm バイト配列を16進数文字列に変換
        /// </summary>
        private static string? GetIdmString(byte[]? idmBytes)
        {
            if (idmBytes == null || idmBytes.Length == 0)
            {
                return null;
            }
            return BitConverter.ToString(idmBytes).Replace("-", "");
        }

        /// <summary>
        /// 履歴データをパースして LedgerDetail に変換
        /// </summary>
        /// <param name="currentData">現在のレコードデータ</param>
        /// <param name="previousData">前回のレコードデータ（金額計算用）</param>
        /// <param name="cardType">カード種別</param>
        /// <remarks>
        /// 純粋関数部分は <see cref="FelicaHistoryBlockDecoder.Decode"/> に抽出されている。
        /// このメソッドは駅名解決サービスとロガー（Issue #942 のフォールバック診断）の責務を保持する。
        /// </remarks>
        private LedgerDetail? ParseHistoryData(byte[] currentData, byte[]? previousData, CardType cardType)
        {
            try
            {
                var detail = FelicaHistoryBlockDecoder.Decode(
                    currentData,
                    previousData,
                    (lineCode, stationNum) => _stationMasterService.GetStationNameOrNull(lineCode, stationNum, cardType),
                    out var pointRedemptionFallbackTriggered);

                // Issue #942: フォールバック判定が発生した場合は診断ログを出力
                if (pointRedemptionFallbackTriggered && detail != null)
                {
                    // 既存の Debug ログは置き換えず併設する（#1730。既存テストの期待値も維持される）
                    _logger.LogDebug(
                        "FelicaCardReader: 利用種別0x{UsageType:X2}で残高増加を検出、ポイント還元として処理（金額: {Amount}円）",
                        currentData[1], detail.Amount);

                    // Issue #1948: このフォールバックは isPointRedemption を立てるだけでなく isBus も取り下げる
                    // （バスとポイント還元は排他）。誤発動すると、本来バス利用だった明細のバス停名入力が
                    // 画面から消え、摘要も「ポイント還元」になる。原因調査には「どの利用種別・金額で
                    // 発動したか」が要るが、LogDebug は本番の LogLevel:Default = Information で
                    // ファイルへ出力されない（#1716）ため、Information でも 1 行残す。
                    // フォールバック発動時のみの出力なので、正常運用中のログ肥大化は起きない。
                    _logger.LogInformation(
                        "FelicaCardReader: 利用種別0x{UsageType:X2}で残高増加を検出したため、ポイント還元として処理しバス判定を取り下げました（金額: {Amount}円）",
                        currentData[1], detail.Amount);
                }

                return detail;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FelicaCardReader: 履歴データのパースエラー");
                return null;
            }
        }

        /// <summary>
        /// 駅コードから駅名を取得
        /// </summary>
        private string GetStationName(int stationCode, CardType cardType)
        {
            return _stationMasterService.GetStationName(stationCode, cardType);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    lock (_lifecycleLock)
                    {
                        StopPollingTimer();
                        StopHealthCheckTimer();
                    }
                }
                _disposed = true;
            }
        }

        #endregion
    }
}
