using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace ServiceDefaults;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SymbolKey16 : IEquatable<SymbolKey16>
{
    public readonly ulong High; // Primeiros 8 caracteres
    public readonly ulong Low;  // Próximos 8 caracteres

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SymbolKey16(ReadOnlySpan<byte> bytes)
    {
        // Lê os primeiros 8 bytes (se existirem)
        High = bytes.Length >= 8
            ? Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(bytes))
            : ReadPartialUlong(bytes, 0);

        // Lê os próximos 8 bytes (se existirem)
        Low = bytes.Length > 8
            ? ReadPartialUlong(bytes.Slice(8), 0)
            : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadPartialUlong(ReadOnlySpan<byte> bytes, int offset)
    {
        ulong result = 0;
        int length = Math.Min(bytes.Length, 8);
        for (int i = 0; i < length; i++)
        {
            result |= ((ulong)bytes[i]) << (i * 8);
        }
        return result;
    }

    // A comparação vira apenas 2 instruções Assembly de igualdade numérica!
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(SymbolKey16 other) => High == other.High && Low == other.Low;

    public override bool Equals(object? obj) => obj is SymbolKey16 other && Equals(other);

    // Hash rápido combinando as duas metades
    public override int GetHashCode() => HashCode.Combine(High, Low);
}
