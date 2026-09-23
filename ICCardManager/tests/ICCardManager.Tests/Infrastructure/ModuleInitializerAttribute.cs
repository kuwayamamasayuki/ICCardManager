// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// C# 9 のモジュール初期化子を .NET Framework 4.8 で使うための Polyfill（Issue #2098）。
/// </summary>
/// <remarks>
/// .NET 5 以降は標準で提供されるが、.NET Framework 4.8 には無いためコンパイラ向けに定義する。
/// 本体の <c>IsExternalInit</c> と同じ扱い。
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class ModuleInitializerAttribute : Attribute
{
}
