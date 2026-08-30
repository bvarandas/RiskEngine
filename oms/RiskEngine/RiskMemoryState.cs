using ServiceDefaults;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PreTradeRisk;

public unsafe sealed class RiskMemoryState
{
    private readonly AccountRiskState* _accounts;
    // Tabela contígua de Custódia: MaxAccounts x MaxSymbols
    // Indexação aritmética pura O(1)
    private readonly PositionState* _positions;
    private const long MaxAccounts = 1_000_000;
    private const int MaxSymbols = 1024; // Suporta até 1024 ativos ativos no OMS(Cada cliente pode ter posições em até 1024 ativos diferentes)

    public RiskMemoryState()
    {
        // Aloca bloco contínuo de memória nativa totalmente alinhada
        _accounts = (AccountRiskState*)NativeMemory.Alloc((nuint)(MaxAccounts * sizeof(AccountRiskState)));

        _positions = (PositionState*)NativeMemory.AllocZeroed((nuint)((long)MaxAccounts * MaxSymbols * sizeof(PositionState)));

        if (_accounts == null || _positions == null)
        {
            Cleanup();
            throw new OutOfMemoryException("Falha ao alocar memória nativa para o motor de risco.");
        }
    }

    // O AccountId serve como índice direto (O(1) absoluto sem overhead de hash)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref AccountRiskState GetAccount(long accountId)
    {
        return ref _accounts[accountId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PositionState GetPosition(long accountId, int symbolId)
    {
        // Cálculo do deslocamento no bloco contíguo de memória nativa
        long offset = (accountId * MaxSymbols) + symbolId;
        return ref _positions[offset];
    }

    private void Cleanup()
    {
        if (_accounts != null) NativeMemory.Free(_accounts);
        if (_positions != null) NativeMemory.Free(_positions);
    }

    ~RiskMemoryState() => Cleanup();
}
