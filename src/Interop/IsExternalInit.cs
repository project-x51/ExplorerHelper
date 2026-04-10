#nullable enable

// Polyfill to enable C# 9+ record types on .NET Framework 4.8
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
