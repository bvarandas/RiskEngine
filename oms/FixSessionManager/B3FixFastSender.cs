using System;
using System.Buffers;
using System.Buffers.Text;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace FixSessionManager;


// Estrutura leve para a ordem (Stack-only, Zero Heap)
public readonly record struct FixOrder(
    long ClOrdID,
    ReadOnlyMemory<byte> Symbol, // Ex: "PETR4"
    byte Side,                   // '1' = Buy, '2' = Sell
    long Quantity,
    decimal Price);

public unsafe class B3FixFastSender
{
    private const byte SOH = 0x01; // Delimitador FIX \x01
    private readonly Socket _socket;
    private long _msgSeqNum = 0;

    public B3FixFastSender(Socket socket)
    {
        _socket = socket;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendNewOrderSingle(in FixOrder order, ReadOnlySpan<byte> senderCompId, ReadOnlySpan<byte> targetCompId)
    {
        // Allocation na Stack: suficiente para uma mensagem New Order Single (D) da B3
        Span<byte> buffer = stackalloc byte[512];

        // Reservar espaço para o Header fixo (Tag 8 e Tag 9)
        int bodyStart = 0;

        // --- CONSTRUÇÃO DO CORPO DA MENSAGEM ---
        Span<byte> bodyBuffer = buffer.Slice(64); // Reserva 64 bytes para o Header
        int bodyLength = 0;

        // Tag 35: MsgType = D (New Order Single)
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 35, "D"u8);

        // Tag 34: MsgSeqNum
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 34, Interlocked.Increment(ref _msgSeqNum));

        // Tag 49: SenderCompID & Tag 56: TargetCompID
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 49, senderCompId);
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 56, targetCompId);

        // Tag 52: SendingTime (YYYYMMDD-HH:MM:SS.mmm)
        bodyLength += WriteSendingTime(bodyBuffer.Slice(bodyLength));

        // Tag 11: ClOrdID
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 11, order.ClOrdID);

        // Tag 55: Symbol
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 55, order.Symbol.Span);

        // Tag 54: Side
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 54, order.Side);

        // Tag 38: OrderQty
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 38, order.Quantity);

        // Tag 44: Price
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 44, order.Price);

        // Tag 40: OrdType = 2 (Limit)
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 40, "2"u8);

        // --- CONSTRUÇÃO DO HEADER (Tags 8 e 9) ---
        int headerLength = 0;
        // Tag 8: BeginString = FIXT.1.1 ou FIX.4.4
        headerLength += WriteTag(buffer, 8, "FIXT.1.1"u8);
        // Tag 9: BodyLength
        headerLength += WriteTag(buffer.Slice(headerLength), 9, bodyLength);

        // Copiar o corpo para logo após o header
        bodyBuffer.Slice(0, bodyLength).CopyTo(buffer.Slice(headerLength));
        int totalLengthExcludingChecksum = headerLength + bodyLength;

        // --- CONSTRUÇÃO DO TRAILER (Tag 10: CheckSum) ---
        int checksum = CalculateChecksum(buffer.Slice(0, totalLengthExcludingChecksum));
        int checksumLength = WriteChecksum(buffer.Slice(totalLengthExcludingChecksum), checksum);

        int totalMessageLength = totalLengthExcludingChecksum + checksumLength;

        // --- ENVIO DIRETO NO SOQUETE ---
        // Envio direto via ReadOnlySpan<byte> (zero-copy)
        _socket.Send(buffer.Slice(0, totalMessageLength), SocketFlags.None);
    }

    #region Formatting Helpers (Zero Allocation)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteTag(Span<byte> buffer, int tag, ReadOnlySpan<byte> value)
    {
        Utf8Formatter.TryFormat(tag, buffer, out int bytesWritten);
        buffer[bytesWritten++] = (byte)'=';
        value.CopyTo(buffer.Slice(bytesWritten));
        bytesWritten += value.Length;
        buffer[bytesWritten++] = SOH;
        return bytesWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteTag(Span<byte> buffer, int tag, long value)
    {
        Utf8Formatter.TryFormat(tag, buffer, out int bytesWritten);
        buffer[bytesWritten++] = (byte)'=';
        Utf8Formatter.TryFormat(value, buffer.Slice(bytesWritten), out int valWritten);
        bytesWritten += valWritten;
        buffer[bytesWritten++] = SOH;
        return bytesWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteTag(Span<byte> buffer, int tag, decimal value)
    {
        Utf8Formatter.TryFormat(tag, buffer, out int bytesWritten);
        buffer[bytesWritten++] = (byte)'=';
        Utf8Formatter.TryFormat(value, buffer.Slice(bytesWritten), out int valWritten);
        bytesWritten += valWritten;
        buffer[bytesWritten++] = SOH;
        return bytesWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteTag(Span<byte> buffer, int tag, byte value)
    {
        Utf8Formatter.TryFormat(tag, buffer, out int bytesWritten);
        buffer[bytesWritten++] = (byte)'=';
        buffer[bytesWritten++] = value;
        buffer[bytesWritten++] = SOH;
        return bytesWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteSendingTime(Span<byte> buffer)
    {
        // Gera o Timestamp no formato FIX de forma ultra-otimizada
        DateTime utcNow = DateTime.UtcNow;
        // Preenchimento manual/Utf8Formatter simplificado do DateTime em ISO/FIX UTF8...
        // Exemplo simplificado:
        ReadOnlySpan<byte> tagPrefix = "52="u8;
        tagPrefix.CopyTo(buffer);
        int offset = tagPrefix.Length;

        // Em produção, formate AAAAAMMDD-HH:MM:SS.mmm diretamente via Utf8Formatter ou Bitwise
        // Exemplo omitido por concisão, retornando o tamanho do payload gravado.
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateChecksum(ReadOnlySpan<byte> buffer)
    {
        Configuration
        uint sum = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            sum += buffer[i];
        }
        return (int)(sum % 256);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteChecksum(Span<byte> buffer, int checksum)
    {
        ReadOnlySpan<byte> tagPrefix = "10="u8;
        tagPrefix.CopyTo(buffer);
        int offset = tagPrefix.Length;

        // O Checksum FIX sempre exige 3 dígitos com zeros à esquerda
        buffer[offset++] = (byte)('0' + (checksum / 100));
        buffer[offset++] = (byte)('0' + ((checksum / 10) % 10));
        buffer[offset++] = (byte)('0' + (checksum % 10));
        buffer[offset++] = SOH;

        return offset;
    }

    #endregion
}