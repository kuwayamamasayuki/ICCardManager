#nullable enable
using System.Collections.Generic;
using ICCardManager.Models;

namespace ICCardManager.ViewModels;

/// <summary>
/// 未登録のカードをタッチしたときに事前に読み取った残高・利用履歴（Issue #482 / #596）を、
/// 交通系ICカードの登録時にカードから読み直すかの判定（Issue #2385）。
/// </summary>
/// <remarks>
/// <para>
/// 読み直す側（<see cref="CardManageViewModel"/> の保存）と、読み直すならカードを載せたままにするよう
/// 案内する側（<see cref="MainViewModel"/> のカード種別選択ダイアログの注意）の判定を 1 か所に置く。
/// 別々に書くと、片方だけ条件が変わったときに「読み直すのに案内しない」（登録の時点でカードが無く、
/// 残高や利用履歴を取り込めない）か「読み直さないのに離さないよう求める」（不要な注意）になる。
/// </para>
/// <para>
/// 利用履歴は後方互換の <c>ICardReader.ReadHistoryAsync</c> で読むため、読み取りの失敗も空のリストになり、
/// 「利用が本当に 0 件」と区別できない（Issue #1169）。登録時はどちらも読み直すので、案内も両方で出す。
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
}
