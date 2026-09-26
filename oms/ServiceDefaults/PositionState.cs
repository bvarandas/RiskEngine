using System.Runtime.InteropServices;

namespace ServiceDefaults;

// Posição de Custódia por Ativo (Ocupa 24 bytes)
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct PositionState
{
    [FieldOffset(0)] public long TotalQuantity;    // Custódia total disponível
    [FieldOffset(8)] public long BlockedQuantity;  // Quantidade retida em ordens abertas de venda
    [FieldOffset(16)] public long TradedQuantity;   // Execuções do dia
}