using System.Runtime.InteropServices;

namespace PreTradeRisk;

// 3. O RING BUFFER LOCK-FREE (Tamanho fixo, transporta apenas índices)
public sealed class LockFreeIndexRingBuffer
{
    private readonly int[] _buffer;
    private readonly int _mask;

    // Prevenção de False Sharing (Separando cabeçote e cauda em Cache Lines diferentes)
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCursor
    {
        [FieldOffset(64)] public long Value;
    }

    private PaddedCursor _writeCursor;
    private PaddedCursor _readCursor;

    public LockFreeIndexRingBuffer(int powerOfTwoSize)
    {
        if ((powerOfTwoSize & (powerOfTwoSize - 1)) != 0)
            throw new ArgumentException("Tamanho deve ser potência de 2.");

        _buffer = new int[powerOfTwoSize];
        _mask = powerOfTwoSize - 1;
        _writeCursor = new PaddedCursor { Value = 0 };
        _readCursor = new PaddedCursor { Value = 0 };
    }

    public bool TryEnqueue(int slabIndex)
    {
        long currentWrite = Volatile.Read(ref _writeCursor.Value);
        long currentRead = Volatile.Read(ref _readCursor.Value);

        // Se o buffer encheu
        if (currentWrite - currentRead >= _buffer.Length) return false;

        _buffer[currentWrite & _mask] = slabIndex;
        Volatile.Write(ref _writeCursor.Value, currentWrite + 1);
        return true;
    }

    public bool TryDequeue(out int slabIndex)
    {
        long currentRead = Volatile.Read(ref _readCursor.Value);
        long currentWrite = Volatile.Read(ref _writeCursor.Value);

        // Se o buffer está vazio
        if (currentRead >= currentWrite)
        {
            slabIndex = -1;
            return false;
        }

        slabIndex = _buffer[currentRead & _mask];
        Volatile.Write(ref _readCursor.Value, currentRead + 1);
        return true;
    }
}