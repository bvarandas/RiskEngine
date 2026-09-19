using ServiceDefaults.events;
using System.Runtime.CompilerServices;

namespace PreTradeRisk;

public sealed class OutboundRejectRingBuffer
{
    private readonly RejectEvent[] _buffer;
    private readonly int _mask;

    // Evita False Sharing separando as sequências
    private PaddedLong _producerSequence;
    private PaddedLong _consumerSequence;

    public OutboundRejectRingBuffer(int capacity = 1024 * 16)
    {
        // Garante potência de 2
        if ((capacity & (capacity - 1)) != 0)
            throw new ArgumentException("A capacidade deve ser potência de 2.");

        _buffer = new RejectEvent[capacity];
        _mask = capacity - 1;

        _producerSequence.Value = 0;
        _consumerSequence.Value = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref RejectEvent Claim(out long sequence)
    {
        sequence = Volatile.Read(ref _producerSequence.Value);

        // Ponto Cego Estratégico: O que fazer se o buffer encher?
        // Se o Kafka (Consumidor) for lento, o Motor de Risco vai travar aqui.
        long wrapPoint = sequence - _buffer.Length;
        while (wrapPoint >= Volatile.Read(ref _consumerSequence.Value))
        {
            Thread.SpinWait(1);
            // ALERTA: Se o Kafka cair, seu Hot Path congela aqui. 
            // Em HFT real, você deve considerar derrubar a sessão ou ter um kill-switch.
        }

        return ref _buffer[sequence & _mask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit(long sequence)
    {
        Volatile.Write(ref _producerSequence.Value, sequence + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetProducerSequence() => Volatile.Read(ref _producerSequence.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref RejectEvent Get(long sequence)
    {
        return ref _buffer[sequence & _mask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateConsumerSequence(long sequence)
    {
        Volatile.Write(ref _consumerSequence.Value, sequence);
    }
}
