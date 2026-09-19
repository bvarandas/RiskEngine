using System.Runtime.InteropServices;

namespace ServiceDefaults.events;

/*
 * Em sistemas de ultrabaixa latência, fragmentar dados com espaços vazios destrói a densidade do cache L1, 
 * forçando a CPU a buscar cache lines adicionais desnecessariamente.
 */
[StructLayout(LayoutKind.Explicit, Size = 128)]
public unsafe struct OrderEvent
{
    // --- CACHE LINE 1 (0-63 bytes) ---

    [FieldOffset(0)] public long OrderId;                // 8 bytes (0-7)
    [FieldOffset(8)] public long AccountId;              // 8 bytes (8-15)
    [FieldOffset(16)] public long Price;                 // 8 bytes (16-23)

    // CORREÇÃO: Movido do offset 32 para 24. Elimina o buraco de memória.
    [FieldOffset(24)] public long Quantity;              // 8 bytes (24-31) 

    // CORREÇÃO: Movido para cima para manter os blocos de 8 bytes contíguos.
    // Servirá como base para a tag 60 (TransactTime) no FIX.
    [FieldOffset(32)] public long IngestionTimestampNs;  // 8 bytes (32-39) 

    [FieldOffset(40)] public int SymbolId;               // 4 bytes (40-43)
    [FieldOffset(44)] public byte Side;                  // 1 byte  (44)
    [FieldOffset(45)] public byte OrderType;             // 1 byte  (45)

    // ADICIONADO: Necessário para o FIX Order
    [FieldOffset(46)] public byte TimeInForce;           // 1 byte  (46)

    // Byte 47 livre (padding para alinhamento)

    [FieldOffset(48)] public SymbolBuffer Symbol;        // 12 bytes (48-59)
    // Bytes 60-63 livres (padding para alinhar o ClOrdId no próximo cache line)

    // --- CACHE LINE 2 (64-127 bytes) ---

    // Movido para o início da segunda linha de cache (offset 64).
    [FieldOffset(64)] public fixed byte ClOrdId[20];     // 20 bytes (64-83)

    // Bytes 84-127 servem de padding intencional para fechar exatamente 128 bytes.
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedSequence
{
    [FieldOffset(24)] public long Value;
}