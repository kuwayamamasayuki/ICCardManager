using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ICCardManager.ViewModels
{
/// <summary>
    /// ViewModelの基底クラス
    /// </summary>
    public abstract partial class ViewModelBase : ObservableObject
    {
        private bool _isBusy;
        private string _busyMessage;
        private bool _canCancel;
        private double _progressValue;
        private double _progressMax = 100;
        private bool _isIndeterminate = true;
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// <see cref="BusyScope"/> の入れ子の深さ（Issue #1836）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「書き込み → 一覧再読込 → 結果表示」（Issue #1753 / #1759 で確立した順序）では、
        /// <see cref="BeginBusy"/> スコープの内側から、自身も <see cref="BeginBusy"/> を開く
        /// 一覧再読込メソッドを呼ぶ形が生まれる。深さを数えないと<b>内側スコープの
        /// <see cref="BusyScope.Dispose"/> が外側の処理中状態まで解除</b>してしまい、
        /// 外側の処理（操作ログ記録・結果表示）が続いている間にオーバーレイが消える。
        /// </para>
        /// <para>
        /// UI スレッド専用のため排他は不要（<see cref="_cancellationTokenSource"/> と同じ前提）。
        /// </para>
        /// </remarks>
        private int _busyDepth;

        /// <summary>
        /// 処理中かどうか
        /// </summary>
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        /// <summary>
        /// 処理中のメッセージ
        /// </summary>
        public string BusyMessage
        {
            get => _busyMessage;
            set => SetProperty(ref _busyMessage, value);
        }

        /// <summary>
        /// キャンセル可能かどうか
        /// </summary>
        public bool CanCancel
        {
            get => _canCancel;
            set => SetProperty(ref _canCancel, value);
        }

        /// <summary>
        /// 進捗値
        /// </summary>
        public double ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        /// <summary>
        /// 進捗最大値
        /// </summary>
        public double ProgressMax
        {
            get => _progressMax;
            set => SetProperty(ref _progressMax, value);
        }

        /// <summary>
        /// 不定プログレス（進捗不明）かどうか
        /// </summary>
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => SetProperty(ref _isIndeterminate, value);
        }

        /// <summary>
        /// キャンセルが要求されているか
        /// </summary>
        public bool IsCancellationRequested => _cancellationTokenSource?.IsCancellationRequested ?? false;

        /// <summary>
        /// 処理中状態を設定
        /// </summary>
        protected void SetBusy(bool isBusy, string message = null)
        {
            IsBusy = isBusy;
            BusyMessage = message;

            if (!isBusy)
            {
                ResetProgress();
            }
        }

        /// <summary>
        /// 進捗をリセット
        /// </summary>
        protected void ResetProgress()
        {
            ProgressValue = 0;
            ProgressMax = 100;
            IsIndeterminate = true;
            CanCancel = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        /// <summary>
        /// 進捗を設定（確定プログレス）
        /// </summary>
        protected void SetProgress(double value, double max, string message = null)
        {
            IsIndeterminate = false;
            ProgressValue = value;
            ProgressMax = max;
            if (message != null)
            {
                BusyMessage = message;
            }
        }

        /// <summary>
        /// 処理中にスコープを作成
        /// </summary>
        /// <remarks>
        /// Issue #1836: 入れ子に対応する。既にスコープが開いている場合、本スコープは
        /// <b>表示にもキャンセル機構にも触れない</b>（<paramref name="message"/> は無視され、
        /// 外側のメッセージが表示され続ける）。処理中状態が解除されるのは最も外側の
        /// スコープが Dispose されたときだけ。
        /// </remarks>
        protected IDisposable BeginBusy(string message = null)
        {
            return new BusyScope(this, message, false);
        }

        /// <summary>
        /// キャンセル可能な処理中スコープを作成
        /// </summary>
        /// <remarks>
        /// Issue #1836: 入れ子の内側で呼ばれた場合は新しい
        /// <see cref="CancellationTokenSource"/> を作らず、外側のトークンを引き継ぐ
        /// （外側がキャンセル不可なら <see cref="CancellationToken.None"/>）。
        /// </remarks>
        protected BusyScope BeginCancellableBusy(string message = null)
        {
            return new BusyScope(this, message, true);
        }

        /// <summary>
        /// 処理中スコープを一時的に中断する（Issue #1793）。
        /// <b><see cref="BeginBusy"/> スコープの内側からモーダルダイアログを表示するとき、必ずこれで囲む。</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>IDialogService</c> の実装は同期モーダル（<c>MessageBox.Show</c>）で、職員が閉じるまで
        /// 呼び出しスレッドをブロックする。そのため <see cref="BusyScope.Dispose"/> が走らず
        /// <see cref="IsBusy"/> が true のまま残り、各画面の全面オーバーレイと不確定
        /// <c>ProgressBar</c> が<b>モーダル表示の背後で回り続ける</b>。とくに確認ダイアログでは、
        /// 職員は「処理が続いているのか」を判断できない状態で決定を迫られる。
        /// </para>
        /// <para>
        /// <b><see cref="SetBusy"/>(false) で代用してはいけない。</b> <see cref="SetBusy"/> は
        /// <see cref="ResetProgress"/> を呼び <see cref="CancellationTokenSource"/> を Dispose するため、
        /// ダイアログを閉じたあとキャンセルが効かなくなり、確定プログレスの進捗も巻き戻る。
        /// 本メソッドは表示状態を退避して復元するだけで、キャンセルの仕組みには触れない。
        /// </para>
        /// <para>
        /// 結果表示のみのダイアログ（戻り値を使わないもの）も本メソッドで囲む。スコープを抜けてから
        /// 実行する遅延 <c>Action</c> 方式（Issue #1784 の <c>DataExportImportViewModel</c>）でも
        /// 同じ結果になるが、戻り値で分岐する確認ダイアログはその方式では扱えないため、
        /// <b>本 Issue の対象画面はイディオムを本メソッドへ統一している</b>。
        /// </para>
        /// <para>
        /// ダイアログ表示を内包するヘルパーメソッドでは、<b>ヘルパーの内側</b>で囲むこと。
        /// 呼び出し側で囲むと、ヘルパーが行う DB 読み取り等までオーバーレイが外れる。
        /// </para>
        /// </remarks>
        /// <returns>Dispose で中断前の表示状態へ戻すスコープ</returns>
        protected IDisposable SuspendBusy()
        {
            return new BusySuspension(this);
        }

        /// <summary>
        /// キャンセルコマンド
        /// </summary>
        [RelayCommand]
        public void CancelOperation()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                BusyMessage = "キャンセル中...";
#if DEBUG
                System.Diagnostics.Debug.WriteLine("[UI] 操作がキャンセルされました");
#endif
            }
        }

        /// <summary>
        /// Busy状態のスコープ
        /// </summary>
        protected class BusyScope : IDisposable
        {
            private readonly ViewModelBase _viewModel;
            private bool _disposed;

            /// <summary>
            /// キャンセルトークン
            /// </summary>
            public CancellationToken CancellationToken { get; }

            public BusyScope(ViewModelBase viewModel, string message, bool canCancel)
            {
                _viewModel = viewModel;
                var isOutermost = _viewModel._busyDepth == 0;

                if (isOutermost)
                {
                    _viewModel.SetBusy(true, message);
                    _viewModel.CanCancel = canCancel;

                    if (canCancel)
                    {
                        _viewModel._cancellationTokenSource = new CancellationTokenSource();
                    }
                }

                // 深さの加算は状態設定のあと。SetBusy が PropertyChanged 経由で例外を投げると
                // コンストラクタは戻らず `using` も成立しないため、先に加算すると深さが恒久的に
                // 漏れ、以後この ViewModel では最外の Dispose が二度と来ない（長命な
                // MainViewModel ではオーバーレイが出たままになる）。あとから加算すれば、
                // 漏れは「次のスコープが最外として扱われて復旧する」従来どおりの挙動に留まる。
                _viewModel._busyDepth++;

                // Issue #1836: 内側スコープは表示にもキャンセル機構にも触れない（外側優先）。
                // 触れると「削除中...」が「読み込み中...」で上書きされ、Dispose 側だけ直しても
                // 外側の処理が終わるまで内側のメッセージが残ることになる。
                // キャンセル可能を要求された内側スコープは、外側が持つトークンを引き継ぐ
                // （外側がキャンセル不可なら None ＝ キャンセル手段が無い従来どおりの状態）。
                CancellationToken = canCancel
                    ? _viewModel._cancellationTokenSource?.Token ?? CancellationToken.None
                    : CancellationToken.None;
            }

            /// <summary>
            /// キャンセルが要求されているかチェック（例外をスロー）
            /// </summary>
            public void ThrowIfCancellationRequested()
            {
                CancellationToken.ThrowIfCancellationRequested();
            }

            /// <summary>
            /// 進捗を更新
            /// </summary>
            public void ReportProgress(double value, double max, string message = null)
            {
                _viewModel.SetProgress(value, max, message);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                // Issue #1836: 最も外側のスコープが閉じたときだけ処理中状態を解除する。
                // 内側で解除すると、外側の処理が続いている間オーバーレイが消えて画面が
                // 操作可能になり（Issue #1761: オーバーレイが塞ぐのはマウスのヒットテストだけ）、
                // さらに SetBusy(false) → ResetProgress() が外側のキャンセル機構
                // （_cancellationTokenSource）まで破棄する。
                if (--_viewModel._busyDepth == 0)
                {
                    _viewModel.SetBusy(false);
                }
            }
        }

        /// <summary>
        /// <see cref="SuspendBusy"/> が返す中断スコープ（Issue #1793）
        /// </summary>
        /// <remarks>
        /// 退避するのは<b>表示状態だけ</b>（<see cref="IsBusy"/> / <see cref="BusyMessage"/> /
        /// <see cref="CanCancel"/> / 進捗 3 値）。<see cref="_cancellationTokenSource"/> には触れないため、
        /// 中断をまたいでもキャンセル要求は生き続ける。
        /// </remarks>
        private sealed class BusySuspension : IDisposable
        {
            private readonly ViewModelBase _viewModel;
            private readonly bool _isBusy;
            private readonly string _busyMessage;
            private readonly bool _canCancel;
            private readonly double _progressValue;
            private readonly double _progressMax;
            private readonly bool _isIndeterminate;
            private bool _disposed;

            public BusySuspension(ViewModelBase viewModel)
            {
                _viewModel = viewModel;

                _isBusy = viewModel.IsBusy;
                _busyMessage = viewModel.BusyMessage;
                _canCancel = viewModel.CanCancel;
                _progressValue = viewModel.ProgressValue;
                _progressMax = viewModel.ProgressMax;
                _isIndeterminate = viewModel.IsIndeterminate;

                // オーバーレイを隠すのは IsBusy だけで足りるが、BusyMessage / CanCancel も伏せる。
                // 前者は別の表示領域に「保存中...」が残るのを防ぐため、後者はオーバーレイごと
                // 退避する以上その中のキャンセルボタンも押せない状態にするため。
                viewModel.IsBusy = false;
                viewModel.BusyMessage = null;
                viewModel.CanCancel = false;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                // IsBusy を最後に戻す。オーバーレイが出る時点で中身（メッセージ・進捗）が
                // 揃っているようにするため。
                _viewModel.ProgressValue = _progressValue;
                _viewModel.ProgressMax = _progressMax;
                _viewModel.IsIndeterminate = _isIndeterminate;
                _viewModel.CanCancel = _canCancel;
                _viewModel.BusyMessage = _busyMessage;
                _viewModel.IsBusy = _isBusy;
            }
        }
    }
}
