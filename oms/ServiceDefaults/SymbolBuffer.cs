using System.Runtime.CompilerServices;

namespace ServiceDefaults;
// Struct auxiliar nativa do .NET moderno para buffers fixos sem 'unsafe'
[InlineArray(12)]
public struct SymbolBuffer
{
    private byte _element0;
}
