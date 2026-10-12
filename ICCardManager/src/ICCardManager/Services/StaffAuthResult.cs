#nullable enable
using System;
using System.Threading;

namespace ICCardManager.Services
{
    /// <summary>
    /// 職員認証の結果。認証した 1 操作の間だけ有効な操作者セッションのスコープを兼ねる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2260: 認証を要求する画面は、この結果を <see cref="Enter"/> してから <c>using</c> で受けて操作を行う。
    /// <see cref="Enter"/> がその画面の非同期の流れで操作者セッション（<see cref="ICurrentOperatorContext"/>）を開始し、
    /// 破棄でセッションが終わって以後の操作ログは GUI 操作として記録される。成功・失敗・キャンセル・例外のいずれでも
    /// 操作の完了時に破棄されるよう、必ず次の形で書くこと（静的検査 <c>StaffAuthScopeConventionTests</c>）。
    /// </para>
    /// <code>
    /// using var authResult = (await _staffAuthService.RequestAuthenticationAsync("職員の削除"))?.Enter();
    /// if (authResult == null)
    /// {
    ///     return;
    /// }
    /// // ここからメソッドを抜けるまで（この流れの中）の書き込みが、認証した職員の操作として記録される
    /// </code>
    /// <para>
    /// セッションの開始を認証サービスの内側ではなく <see cref="Enter"/> に置くのは、セッションが非同期の流れ
    /// （<see cref="System.Threading.AsyncLocal{T}"/>）に結び付いており、<c>async</c> メソッドの内側で開始した値は
    /// 呼び出し元へ戻らないため。<see cref="Enter"/> は同期メソッドなので、呼び出した画面のメソッドの流れに残る。
    /// </para>
    /// <para>
    /// 旧実装は認証の成功時にセッションを開始するだけで終える経路が無く、5 分間残ったセッションを
    /// 認証の無い操作（カードの編集・払い戻し・明細の保存など）が拾っていた。
    /// </para>
    /// </remarks>
    public sealed class StaffAuthResult : IDisposable
    {
        private readonly ICurrentOperatorContext? _operatorContext;
        private IDisposable? _session;
        private bool _entered;

        /// <summary>
        /// 操作者セッションを伴わない結果を作る（テストの代役など）。<see cref="Enter"/> は何もしない。
        /// </summary>
        public StaffAuthResult()
        {
        }

        /// <summary>
        /// 認証した職員の結果を作る。セッションは <see cref="Enter"/> で開始し、破棄で終了する。
        /// </summary>
        internal StaffAuthResult(string idm, string staffName, ICurrentOperatorContext operatorContext)
        {
            Idm = idm;
            StaffName = staffName;
            _operatorContext = operatorContext ?? throw new ArgumentNullException(nameof(operatorContext));
        }

        /// <summary>
        /// 認証された職員のIDm
        /// </summary>
        public string Idm { get; set; } = string.Empty;

        /// <summary>
        /// 認証された職員の氏名
        /// </summary>
        public string StaffName { get; set; } = string.Empty;

        /// <summary>
        /// 呼び出した非同期の流れで、認証した職員の操作者セッションを開始する（2 回目以降は何もしない）。
        /// </summary>
        /// <returns>この結果自身（<c>using var r = (await …)?.Enter();</c> と書けるように）。</returns>
        /// <remarks>
        /// 認証を要求した画面のメソッドの中で呼ぶこと。<c>async</c> のヘルパーの内側で呼ぶと、
        /// そのヘルパーから戻った画面のメソッドにはセッションが見えない。
        /// </remarks>
        public StaffAuthResult Enter()
        {
            if (_entered)
            {
                return this;
            }

            _entered = true;
            if (_operatorContext != null)
            {
                _session = _operatorContext.BeginSession(Idm, StaffName);
            }

            return this;
        }

        /// <summary>
        /// 認証した操作の終了。操作者セッションを終える（2 回目以降の呼び出しは何もしない）。
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _session, null)?.Dispose();
        }
    }
}
