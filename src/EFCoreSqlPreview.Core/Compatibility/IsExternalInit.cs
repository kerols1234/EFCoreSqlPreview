#if NETSTANDARD2_0

// The C# compiler requires this type to emit `init` accessors and positional records.
// netstandard2.0 does not ship it, so we declare it locally rather than taking a PolySharp dependency.

namespace System.Runtime.CompilerServices;

using System.ComponentModel;

/// <summary>
/// Reserved for use by a compiler for tracking metadata. This type is not intended to be used directly.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}

#endif
