#nullable enable

// ReSharper disable once CheckNamespace
namespace System.Diagnostics.CodeAnalysis
{
    // Null 許容参照型のフロー解析に使う属性の Polyfill（Issue #2163）。
    //
    // .NET Core 3.0 以降は標準で提供されるが、.NET Framework 4.8 には無い。コンパイラは名前空間と型名で
    // 属性を認識するため、同じ名前の internal 型を 1 度だけ定義すれば net48 でも同じ解析が働く。
    // internal にするのは、本体を参照する一般のアセンブリへ型を公開しないため（公開すると、参照側が持つ同名の型と衝突する）。
    // ただし InternalsVisibleTo の相手（ICCardManager.Tests・DebugDataViewer）には internal でも見えるので、
    // そちらで同名の型を定義したり、同種の Polyfill を含むパッケージ（PolySharp 等）を入れたりすると CS0436 の警告になる。
    // それらのプロジェクトで属性が必要になったら、自前で定義せず本体のこの定義を使うこと。

    /// <summary>入力として null を許す（出力は非 null）。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
    internal sealed class AllowNullAttribute : Attribute
    {
    }

    /// <summary>入力として null を許さない（出力は null になり得る）。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
    internal sealed class DisallowNullAttribute : Attribute
    {
    }

    /// <summary>非 null の型であっても、出力が null になり得る。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class MaybeNullAttribute : Attribute
    {
    }

    /// <summary>null 許容の型であっても、出力は null にならない。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullAttribute : Attribute
    {
    }

    /// <summary>メソッドが <see cref="ReturnValue"/> を返したとき、この引数は null になり得る。</summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class MaybeNullWhenAttribute : Attribute
    {
        public MaybeNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }

    /// <summary>メソッドが <see cref="ReturnValue"/> を返したとき、この引数は null ではない（<c>TryGet…</c> の形）。</summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }

    /// <summary>指定した引数が null でなければ、戻り値（または対象）も null ではない。</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    internal sealed class NotNullIfNotNullAttribute : Attribute
    {
        public NotNullIfNotNullAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }

    /// <summary>このメソッドは呼び出し元へ戻らない（常に例外を投げる等）。</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class DoesNotReturnAttribute : Attribute
    {
    }

    /// <summary>この引数が <see cref="ParameterValue"/> のとき、メソッドは呼び出し元へ戻らない。</summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class DoesNotReturnIfAttribute : Attribute
    {
        public DoesNotReturnIfAttribute(bool parameterValue) => ParameterValue = parameterValue;

        public bool ParameterValue { get; }
    }

    /// <summary>メソッドから戻った時点で、指定したメンバーは null ではない。</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
    internal sealed class MemberNotNullAttribute : Attribute
    {
        public MemberNotNullAttribute(string member) => Members = new[] { member };

        public MemberNotNullAttribute(params string[] members) => Members = members;

        public string[] Members { get; }
    }

    /// <summary>メソッドが <see cref="ReturnValue"/> を返したとき、指定したメンバーは null ではない。</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
    internal sealed class MemberNotNullWhenAttribute : Attribute
    {
        public MemberNotNullWhenAttribute(bool returnValue, string member)
        {
            ReturnValue = returnValue;
            Members = new[] { member };
        }

        public MemberNotNullWhenAttribute(bool returnValue, params string[] members)
        {
            ReturnValue = returnValue;
            Members = members;
        }

        public bool ReturnValue { get; }

        public string[] Members { get; }
    }
}
