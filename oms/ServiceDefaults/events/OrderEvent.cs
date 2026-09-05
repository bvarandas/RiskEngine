using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace ServiceDefaults.events;

// Layout explícito garante tamanho fixo e alinhamento previsível de memória
[StructLayout(LayoutKind.Explicit, Size = 128)]
public  struct OrderEvent
{
    [FieldOffset(0)] public long OrderId;
    [FieldOffset(8)] public long AccountId;
    [FieldOffset(16)] public decimal Price;
    [FieldOffset(24)] public int Quantity;
    [FieldOffset(28)] public byte Side; // 1 = Buy, 2 = Sell
    [FieldOffset(32)] public byte OrderType; // 1 = Market, 2 = Limit

    [FieldOffset(32)] public int SymbolId;   // <--- NOVO: ID numérico pré-resolvido (0 a N)

    // Symbol armazenado como ASCII/UTF-8 embutido (fixed buffer) para evitar string heap
    [FieldOffset(36)] public unsafe fixed byte Symbol[12];

    [FieldOffset(48)] public long IngestionTimestampNs; // Timestamp em nanosegundos
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedSequence
{
    [FieldOffset(24)] public long Value;
}
