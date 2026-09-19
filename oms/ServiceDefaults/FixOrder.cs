using System.Runtime.InteropServices;

namespace ServiceDefaults;

[StructLayout(LayoutKind.Explicit, Size = 128)]
public unsafe struct FixOrder
{
    // Tag 11: ClOrdID (Alfanumérico, 20 bytes) - OBRIGATÓRIO SER STRING/BYTE ARRAY
    [FieldOffset(0)] public fixed byte ClOrdID[20];

    // Tag 1: Account (Não ignore a menos que o Gateway injete)
    [FieldOffset(24)] public long AccountId;

    // Tag 44: Price (Preço com multiplicador implícito)
    [FieldOffset(32)] public long Price;

    // Tag 38: OrderQty (Mandatório)
    [FieldOffset(40)] public long Quantity;

    // Tag 60: TransactTime (Mandatório, enviado como nanosegundos ou ticks)
    [FieldOffset(48)] public long TransactTime;

    // Tag 54: Side (1 = Buy, 2 = Sell) - Mandatório
    [FieldOffset(56)] public byte Side;

    // Tag 40: OrdType (1 = Market, 2 = Limit) - Mandatório
    [FieldOffset(57)] public byte OrderType;

    // Tag 59: TimeInForce (0 = Day, 3 = IOC, 4 = FOK)
    [FieldOffset(58)] public byte TimeInForce;

    // Tag 55: Symbol (Buffer inline) - Mandatório
    [FieldOffset(64)] public SymbolBuffer Symbol;

    public FixOrder(
        byte* sourceClOrdId,
        long accountId,
        long price,
        long quantity,
        byte side,
        byte orderType,
        byte timeInForce,
        long transactTime,
        ReadOnlySpan<byte> symbolSpan)
    {
        this = default;

        AccountId = accountId;
        Price = price;
        Quantity = quantity;
        Side = side;
        OrderType = orderType;
        TimeInForce = timeInForce;
        TransactTime = transactTime;

        // Cópia SIMD do ClOrdId (20 bytes) preservando o identificador do cliente
        fixed (byte* destClOrdId = ClOrdID)
        {
            Buffer.MemoryCopy(sourceClOrdId, destClOrdId, 20, 20);
        }

        // Assumindo que SymbolBuffer comporta implicitamente um Span<byte>
        int length = Math.Min(symbolSpan.Length, 12);
        symbolSpan.Slice(0, length).CopyTo(Symbol);
    }
}