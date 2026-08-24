using System.Runtime.InteropServices;

namespace ServiceDefaults;

// Posição de Custódia por Ativo (Ocupa 16 bytes)
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct PositionState
{
    [FieldOffset(0)] public int TotalQuantity;    // Custódia total disponível
    [FieldOffset(4)] public int BlockedQuantity;  // Quantidade retida em ordens abertas de venda
    [FieldOffset(8)] public int TradedQuantity;   // Execuções do dia
}