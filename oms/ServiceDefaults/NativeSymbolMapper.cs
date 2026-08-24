using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ServiceDefaults;

public unsafe struct NativeSymbolMapper
{
    private const int Capacity = 32768; // Deve ser potência de 2 (cobre os 20k+ símbolos da B3)
    private const int Mask = Capacity - 1;

    // Ponteiros para memória nativa (fora do Managed Heap)
    private readonly SymbolKey16* _keys;
    private readonly int* _ids;

    public NativeSymbolMapper()
    {
        // Aloca os buffers na memória unmanaged limpos com zero
        _keys = (SymbolKey16*)NativeMemory.AllocZeroed((nuint)(Capacity * sizeof(SymbolKey16)));
        _ids = (int*)NativeMemory.AllocZeroed((nuint)(Capacity * sizeof(int)));
    }

    /// <summary>
    /// Cadastra o símbolo durante a inicialização (Startup).
    /// </summary>
    public void RegisterSymbol(in SymbolKey16 key, int symbolId)
    {
        // Garante que o ID 0 seja reservado para posições não preenchidas
        int internalId = symbolId + 1;
        int index = key.GetHashCode() & Mask;

        while (_ids[index] != 0)
        {
            // Tratamento de colisão via Linear Probing
            index = (index + 1) & Mask;
        }

        _keys[index] = key;
        _ids[index] = internalId;
    }

    /// <summary>
    /// Método de busca no Hot Path.
    /// Zero Allocation, Zero Heap, Zero GC, < 2 nanosegundos.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetSymbolId(in SymbolKey16 key)
    {
        int index = key.GetHashCode() & Mask;

        while (true)
        {
            int slotId = _ids[index];

            // 1. Posição vazia: símbolo não cadastrado
            if (slotId == 0) return -1;

            ref readonly SymbolKey16 candidate = ref _keys[index];

            // 2. Comparação direta dos dois ulongs da struct
            if (candidate.High == key.High && candidate.Low == key.Low)
            {
                return slotId - 1; // Retorna o SymbolId original
            }

            // 3. Colisão: avança para o próximo slot
            index = (index + 1) & Mask;
        }
    }

    public void Free()
    {
        if (_keys != null) NativeMemory.Free(_keys);
        if (_ids != null) NativeMemory.Free(_ids);
    }
}