using ServiceDefaults.events;
using ServiceDefaults.interfaces;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ServiceDefaults;

public sealed class FastRingBuffer : IFastRingBuffer
{
    private const int BufferSize = 1024 * 64; // Deve ser potência de 2 (65536)
    private const int Mask = BufferSize - 1;
    public ref long GetProducerSequence() => ref _producerSequence.Value;

    // Array contínuo pré-alocado de structs no Heap (uma única alocação na inicialização)
    private readonly OrderEvent[] _entries = new OrderEvent[BufferSize];

    // Padding para isolar a linha de cache do Produtor (64 bytes)
    private PaddedLong _producerSequence;

    public FastRingBuffer()
    {
        _producerSequence.Value = -1L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long NextSequence()
    {
        // Para Single-Producer (Thread-Safe sem interlocked):
        return ++_producerSequence.Value;

        // Se fosse Multi-Producer:
        // return Interlocked.Increment(ref _producerSequence.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref OrderEvent Get(long sequence)
    {
        // Retorno por referência (ref) evita copiar a struct para a stack
        return ref _entries[sequence & Mask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence)
    {
        // Notifica consumidores via barreira de memória de escrita sem travar a CPU
        Volatile.Write(ref _producerSequence.Value, sequence);
    }


}

// Struct de padding para evitar False Sharing entre os núcleos da CPU
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedLong
{
    [FieldOffset(24)] public long Value;
}