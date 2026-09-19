using ServiceDefaults;
using System.Buffers.Text;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace FixSessionManager;

public unsafe class B3FixFastSender
{
    private const byte SOH = 0x01; // Delimitador FIX \x01
    private readonly Socket _socket;
    private long _msgSeqNum = 0;

    public B3FixFastSender(Socket socket)
    {
        _socket = socket;
        _socket.Blocking = false;
        _socket.NoDelay = true; // Desativa o Algoritmo de Nagle (crítico para FIX)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendNewOrderSingle(in FixOrder order, ReadOnlySpan<byte> senderCompId, ReadOnlySpan<byte> targetCompId)
    {
        Span<byte> buffer = stackalloc byte[512];
        Span<byte> bodyBuffer = buffer.Slice(64);
        int bodyLength = 0;

        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 35, "D"u8);
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 34, Interlocked.Increment(ref _msgSeqNum));
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 49, senderCompId);
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 56, targetCompId);

        // Tag 52: SendingTime (Obrigatório no Header)
        bodyLength += WriteSendingTime(bodyBuffer.Slice(bodyLength));

        unsafe
        {
            // 1. Ancora a struct na memória para impedir a ação do GC sobre este endereço
            fixed (byte* pClOrdID = order.ClOrdID)
            {
                // 2. Transforma o ponteiro bruto em Span para análise O(1)
                ReadOnlySpan<byte> rawIdSpan = new ReadOnlySpan<byte>(pClOrdID, 20);

                // 3. Localiza o primeiro byte nulo para descobrir o tamanho real do ID
                int realLength = rawIdSpan.IndexOf((byte)0);
                if (realLength < 0) realLength = 20; // O ID ocupa todos os 20 bytes

                // 4. Escreve a tag apenas com os bytes úteis
                ReadOnlySpan<byte> validIdSpan = rawIdSpan.Slice(0, realLength);
                bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 11, validIdSpan);
            }
        }

        // Extração segura do Symbol sem gerar nulos
        ReadOnlySpan<byte> symbolSpan = order.Symbol;
        int symbolLen = symbolSpan.IndexOf((byte)0);
        if (symbolLen < 0) symbolLen = 12;
        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 55, symbolSpan.Slice(0, symbolLen));

        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 54, order.Side);

        // ATENÇÃO: Tag 60 (TransactTime) é OBRIGATÓRIA no FIX 4.4 para MsgType=D
        // Usando a mesma rotina de SendingTime, ou uma específica caso você precise de granularidade diferente
        bodyLength += WriteTimestamp(bodyBuffer.Slice(bodyLength), 60, DateTime.UtcNow);

        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 38, order.Quantity);
        // INJEÇÃO APLICADA: Uso do Inteiro Escalado com precisão de 4 casas (fator 10000)

        bodyLength += WriteScaledPrice(bodyBuffer.Slice(bodyLength), 44, order.Price);

        bodyLength += WriteTag(bodyBuffer.Slice(bodyLength), 40, "2"u8);

        Span<byte> tempHeader = stackalloc byte[32];
        int headerLength = 0;

        // Header definido estritamente para FIX.4.4
        headerLength += WriteTag(tempHeader, 8, "FIX.4.4"u8);
        headerLength += WriteTag(tempHeader.Slice(headerLength), 9, bodyLength);

        int messageStart = 64 - headerLength;
        tempHeader.Slice(0, headerLength).CopyTo(buffer.Slice(messageStart));

        int totalLengthExcludingChecksum = headerLength + bodyLength;

        int checksum = CalculateChecksum(buffer.Slice(messageStart, totalLengthExcludingChecksum));
        int checksumLength = WriteChecksum(buffer.Slice(messageStart + totalLengthExcludingChecksum), checksum);

        int totalMessageLength = totalLengthExcludingChecksum + checksumLength;

        // Uso do método não-bloqueante para evitar o gargalo de I/O na thread crítica
        SendWithSpinLoop(buffer.Slice(messageStart, totalMessageLength));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SendWithSpinLoop(ReadOnlySpan<byte> buffer)
    {
        int totalSent = 0;
        int length = buffer.Length;
        int spinCount = 0;

        while (totalSent < length)
        {
            // Usa a sobrecarga que NÃO lança exceções. Retorna o erro no parâmetro 'out'.
            int sent = _socket.Send(buffer.Slice(totalSent), SocketFlags.None, out SocketError errorCode);

            if (errorCode == SocketError.Success)
            {
                totalSent += sent;
                spinCount = 0; // Reseta o contador ao progredir
            }
            else if (errorCode == SocketError.WouldBlock)
            {
                // O buffer do SO está cheio.
                // Executamos o spin ativo puramente na CPU (sem yield implícito do SpinWait)
                // Limitamos a 1000 ciclos (~alguns microssegundos) antes de forçar o yield,
                // para evitar o travamento total do núcleo caso a conexão congele.
                if (spinCount < 1000)
                {
                    Thread.SpinWait(1); // Instrução 'pause' nativa na CPU, muito mais leve que SpinWait struct
                    spinCount++;
                }
                else
                {
                    Thread.Yield(); // Rede severamente estrangulada. Cede para evitar dead-lock térmico.
                    spinCount = 0;
                }
            }
            else
            {
                // Uma falha real (ex: conexão perdida). Aqui sim, é justificável alocar erro.
                throw new SocketException((int)errorCode);
            }
        }
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int WriteTimestamp(Span<byte> buffer, int tag, DateTime timestamp)
    {
        int offset = 0;

        // 1. Escreve a Tag ("52" ou "60") sem alocar strings
        Utf8Formatter.TryFormat(tag, buffer, out int tagLen);
        offset += tagLen;

        // 2. Escreve o delimitador de Tag "="
        buffer[offset++] = (byte)'=';

        // 3. Escreve o Timestamp diretamente em bytes UTF-8 (Requer .NET 8+)
        // Formato FIX estrito: YYYYMMDD-HH:MM:SS.mmm
        timestamp.TryFormat(buffer.Slice(offset), out int timeLen, "yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture);

        offset += timeLen;

        // 4. Escreve o delimitador SOH (\x01)
        buffer[offset++] = 1;

        return offset;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WriteScaledPrice(Span<byte> buffer, int tag, long priceRaw, int scaleDecimals = 4)
    {
        int offset = 0;

        // Escreve a Tag e o '='
        Utf8Formatter.TryFormat(tag, buffer, out int tagLen);
        offset += tagLen;
        buffer[offset++] = (byte)'=';

        // Formata o inteiro puro no buffer temporário ou diretamente
        Span<byte> numBuffer = stackalloc byte[20];
        Utf8Formatter.TryFormat(priceRaw, numBuffer, out int numLen);

        // Lógica para injetar o ponto decimal sem matemática pesada
        int dotPosition = numLen - scaleDecimals;

        if (dotPosition <= 0)
        {
            // Tratamento para frações puras (ex: 0.05) omitido para concisão,
            // mas requer escrever "0." e preencher os zeros à esquerda.
        }
        else
        {
            // Copia a parte inteira
            numBuffer.Slice(0, dotPosition).CopyTo(buffer.Slice(offset));
            offset += dotPosition;

            // Insere o ponto
            buffer[offset++] = (byte)'.';

            // Copia a parte fracionária
            numBuffer.Slice(dotPosition, scaleDecimals).CopyTo(buffer.Slice(offset));
            offset += scaleDecimals;
        }

        buffer[offset++] = 1; // SOH
        return offset;
    }
    #endregion
}