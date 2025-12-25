using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
/// <summary>
    /// C# 9.0のinit accessorを.NET Framework 4.8で使用するためのPolyfill
    /// </summary>
    /// <remarks>
    /// このクラスは.NET 5.0以降では標準で提供されますが、
    /// .NET Framework 4.8ではコンパイラが必要とするため手動で定義します。
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
