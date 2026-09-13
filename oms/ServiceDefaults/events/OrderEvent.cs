using System.Runtime.InteropServices;

namespace ServiceDefaults.events;

[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct OrderEvent
{
    [FieldOffset(0)] public long OrderId;                // 8 bytes (0-7)
    [FieldOffset(8)] public long AccountId;              // 8 bytes (8-15)
    [FieldOffset(16)] public long Price;               // 16 bytes (16-31)
    [FieldOffset(32)] public int Quantity;               // 8 bytes (32-39) - Corrigido para long
    [FieldOffset(40)] public int SymbolId;                // 4 bytes (40-43)
    [FieldOffset(44)] public byte Side;                   // 1 byte  (44)
    [FieldOffset(45)] public byte OrderType;              // 1 byte  (45)
    // Bytes 46-47 estão vazios (padding natural para alinhamento)
    [FieldOffset(48)] public SymbolBuffer Symbol;         // 12 bytes (48-59)
    // Bytes 60-63 estão vazios
    [FieldOffset(64)] public long IngestionTimestampNs;   // 8 bytes (64-71)
                                                          // Bytes 72-127 estão vazios (Preenchimento intencional para evitar False Sharing no L1 Cache)
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedSequence
{
    [FieldOffset(24)] public long Value;
}