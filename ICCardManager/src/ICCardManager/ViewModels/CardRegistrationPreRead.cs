#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;

namespace ICCardManager.ViewModels;

/// <summary>
/// 未登録のカードをタッチしたときに事前に読み取った残高・利用履歴（Issue #482 / #596）を、
/// 交通系ICカードの登録時にカードから読み直すかの判定と、その読み直し（Issue #2385）。
/// </summary>
/// <remarks>
/// <para>
/// 読み直す側（<see cref="CardManageViewModel"/> の保存）と、読み直すならカードを載せたままにするよう
/// 案内する側（<see cref="MainViewModel"/> のカード種別選択ダイアログの注意）の判定を 1 か所に置く。
/// 別々に書くと、片方だけ条件が変わったときに「読み直すのに案内しない」（登録の時点でカードが無く、
/// 残高や利用履歴を取り込めない）か「読み直さないのに離さないよう求める」（不要な注意）になる。
/// </para>
/// <para>
/// <b>読み直しそのもの（カードリーダーの呼び出し）もここに置く</b>（<see cref="ResolveBalanceAsync"/> /
/// <see cref="ResolveHistoryAsync"/>）。判定だけを共有すると、保存の経路で条件を書き直した
/// （<c>balance is null</c> などの綴り）呼び出しを静的検査で拾い切れない。保存の経路にカードリーダーの読み取りの
/// 直接の呼び出しが無いことは <c>CardRegistrationPreReadConventionTests</c> が固定する。
/// </para>
/// <para>
/// 利用履歴は後方互換の <c>ICardReader.ReadHistoryAsync</c> で読むため、読み取りの失敗も空のリストになり、
/// 「利用が本当に 0 件」と区別できない（Issue #1169）。登録時はどちらも読み直すので、案内も両方で出す。
/// </para>
/// <para>
/// 事前に読んだ値を交通系ICカード管理画面へ渡す <c>CardManageDialog</c> の Loaded は、残高が null・利用履歴が null のときは
/// ViewModel へ設定しない（<c>_presetBalance.HasValue</c> / <c>_presetHistory != null</c>）。設定しなくても ViewModel 側は
/// null のままなので、ここでの判定（null なら読み直す）と結果は変わらない。ただしそこで空の利用履歴を「渡さない」側へ
/// 寄せるなど、渡す条件を変えるときはこの判定と食い違わないか確かめること。
/// </para>
/// </remarks>
internal static class CardRegistrationPreRead
{
    /// <summary>
    /// 登録時にカードから残高を読み直す可能性があるか（事前に読み取れなかった）。
    /// 取り込む利用履歴があれば初期残高は履歴から逆算するので、実際には読み直さないこともある。
    /// </summary>
    public static bool RereadsBalance(int? balance) => !balance.HasValue;

    /// <summary>
    /// 登録時にカードから利用履歴を読み直すか（事前に読み取れなかった、または 0 件だった）。
    /// </summary>
    public static bool RereadsHistory(IReadOnlyCollection<LedgerDetail>? history) =>
        history == null || history.Count == 0;

    /// <summary>
    /// 交通系ICカードとして登録するなら、登録が完了するまでカードをリーダーに載せておく必要があるか。
    /// 残高と利用履歴のどちらか一方でも読み直す可能性があるなら true（片方だけ読み取れなかった場合も、その片方を読み直すことがあるため）。
    /// 残高を実際に読み直すかは登録方法（取り込みの開始日）と取り込む履歴の有無で決まり、種別選択の時点では分からないので、安全側に倒す。
    /// </summary>
    public static bool NeedsCardOnReaderUntilRegistered(int? balance, IReadOnlyCollection<LedgerDetail>? history) =>
        RereadsBalance(balance) || RereadsHistory(history);

    /// <summary>
    /// 登録に使う残高を決める。<paramref name="balance"/> があればそのまま返し、無ければ（<see cref="RereadsBalance"/>）カードから読み直す。
    /// </summary>
    /// <remarks>読み取りの例外はそのまま伝える（呼び出し元の <c>BuildInitialLedgerAsync</c> が受け止める）。</remarks>
    /// <param name="cardReader">カードリーダー</param>
    /// <param name="cardIdm">カードの IDm</param>
    /// <param name="balance">事前に読み取った残高（または利用者の入力・履歴からの逆算）。無ければ null</param>
    /// <returns>残高。読み直しても読めなければ null</returns>
    public static async Task<int?> ResolveBalanceAsync(ICardReader cardReader, string cardIdm, int? balance)
    {
        if (!RereadsBalance(balance))
        {
            return balance;
        }

        return await cardReader.ReadBalanceAsync(cardIdm);
    }

    /// <summary>
    /// 取り込みに使う利用履歴を決める。<paramref name="history"/> が 1 件以上あればそのまま返し、
    /// 無いか 0 件なら（<see cref="RereadsHistory"/>）カードから読み直す。
    /// </summary>
    /// <param name="cardReader">カードリーダー</param>
    /// <param name="cardIdm">カードの IDm</param>
    /// <param name="history">事前に読み取った利用履歴。無ければ null</param>
    /// <returns>利用履歴。読み直しに失敗したら null</returns>
    public static async Task<List<LedgerDetail>?> ResolveHistoryAsync(
        ICardReader cardReader, string cardIdm, List<LedgerDetail>? history)
    {
        if (!RereadsHistory(history))
        {
            return history;
        }

        try
        {
            return (await cardReader.ReadHistoryAsync(cardIdm))?.ToList();
        }
        catch
        {
            // 読み直せなくても登録は続ける（履歴の取り込みだけを諦める。従来の SaveAsync と同じ扱い）
            return null;
        }
    }
}
