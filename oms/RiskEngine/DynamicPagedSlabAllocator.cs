using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PreTradeRisk;

public sealed unsafe class DynamicPagedSlabAllocator<T> : IDisposable where T : unmanaged
{
    private readonly int _blocksPerPage;
    private readonly int _blockSizeInBytes;

    // Máscara e Shift para evitar operações lentas de divisão/módulo
    private readonly int _pageShift;
    private readonly int _indexMask;

    // Lista estrita de ponteiros base de cada página alocada
    private readonly IntPtr[] _pages = new IntPtr[1024]; // Limite físico do alocador
    private volatile int _pageCount = 0; // Garante visibilidade entre threads

    // Fila de índices livres
    private readonly ConcurrentQueue<int> _freeIndices = new();
    private int _totalAllocatedBlocks = 0;

    public DynamicPagedSlabAllocator(int blocksPerPagePowerOfTwo)
    {
        // Validação estrita para garantir operação bit a bit
        if ((blocksPerPagePowerOfTwo & (blocksPerPagePowerOfTwo - 1)) != 0)
            throw new ArgumentException("Deve ser potência de 2 (ex: 1048576).");

        _blocksPerPage = blocksPerPagePowerOfTwo;
        _blockSizeInBytes = sizeof(T);

        _indexMask = _blocksPerPage - 1;
        _pageShift = (int)Math.Log2(_blocksPerPage);

        AllocateNewPage();
    }

    private void AllocateNewPage()
    {
        nuint totalBytes = (nuint)(_blocksPerPage * _blockSizeInBytes);
        void* newPage = NativeMemory.AllocZeroed(totalBytes);

        int currentIndex = _pageCount;
        if (currentIndex >= _pages.Length)
        {
            // Falha catastrófica: O volume excedeu a capacidade extrema do sistema.
            throw new OutOfMemoryException("Limite máximo de páginas alcançado.");
        }

        _pages[currentIndex] = (IntPtr)newPage;

        int startIndex = currentIndex * _blocksPerPage;
        for (int i = 0; i < _blocksPerPage; i++)
        {
            _freeIndices.Enqueue(startIndex + i);
        }

        // Volatile write: publica a nova página para todas as threads lerem com segurança
        _pageCount = currentIndex + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RentIndex()
    {
        if (!_freeIndices.TryDequeue(out int index))
        {
            // Penalidade de latência ocorre aqui se a fila esvaziar
            AllocateNewPage();
            _freeIndices.TryDequeue(out index);
        }
        return index;
    }

    // === AQUI ESTÁ A IMPLEMENTAÇÃO DO GETREF ===
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetRef(int index)
    {
        int pageIndex = index >> _pageShift;
        int offset = index & _indexMask;

        // 2. Leitura Wait-Free: Acesso direto ao array sem nenhum lock
        // A CPU executa isso em ~1 ciclo de clock
        IntPtr pageBase = _pages[pageIndex];

        byte* ptr = (byte*)pageBase + (offset * _blockSizeInBytes);
        return ref Unsafe.AsRef<T>(ptr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReturnIndex(int index)
    {
        _freeIndices.Enqueue(index);
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            NativeMemory.Free(page.ToPointer());
        }
    }
}