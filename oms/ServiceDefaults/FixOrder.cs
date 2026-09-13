using System.Runtime.InteropServices;

namespace ServiceDefaults;

// Estrutura leve para a ordem (Stack-only, Zero Heap)
[StructLayout(LayoutKind.Explicit, Size = 128)]
public readonly struct FixOrder
{
    [FieldOffset(0)] public readonly long ClOrdID;       // Espelha OrderId

    // Ignorando AccountId (offset 8), mas o espaço é preservado no layout.

    [FieldOffset(16)] public readonly long Price;      // Espelha Price
    [FieldOffset(32)] public readonly int Quantity;      // Espelha Quantity

    // Ignorando SymbolId, mapeando Side para o mesmo local exato
    [FieldOffset(44)] public readonly byte Side;          // Espelha Side

    // Substituindo ReadOnlyMemory<byte> (que é managed e destrói compatibilidade blittable)
    // pelo buffer inline no offset exato.
    [FieldOffset(48)] public readonly SymbolBuffer Symbol;

    // Método utilitário para facilitar injeção do span original (Ex: "PETR4")
    public FixOrder(long clOrdID, ReadOnlySpan<byte> symbolSpan, byte side, int quantity, long price)
    {
        this = default; // Obrigatório para structs com Explicit layout zerarem memória residual
        ClOrdID = clOrdID;
        Price = price;
        Quantity = quantity;
        Side = side;

        // Copia até 12 bytes do ReadOnlySpan para o InlineArray de forma eficiente
        int length = Math.Min(symbolSpan.Length, 12);
        symbolSpan.Slice(0, length).CopyTo(Symbol);
    }
}
