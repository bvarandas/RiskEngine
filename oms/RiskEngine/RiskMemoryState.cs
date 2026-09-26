using ServiceDefaults;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PreTradeRisk;

public unsafe sealed class RiskMemoryState : IDisposable
{
    private readonly AccountRiskState* _accounts;
    // Tabela contígua de Custódia: MaxAccounts x MaxSymbols
    // Indexação aritmética pura O(1)
    private readonly PositionState* _positions;
    private const long MaxAccounts = 200_000;
    //private const int MaxSymbols = 256; // Suporta até 256 ativos no OMS (reduzido de 1024 para ~4 GB footprint)
    private const int MaxSymbols = 2048;

    public RiskMemoryState()
    {
        // Calcula tamanho em MB para diagnostics
        long accountsBytes = MaxAccounts * sizeof(AccountRiskState);
        long positionsBytes = (long)MaxAccounts * MaxSymbols * sizeof(PositionState);
        long totalBytes = accountsBytes + positionsBytes;
        long totalMB = totalBytes / (1024 * 1024);

        Console.WriteLine($"[RiskMemoryState] Alocando memória: {totalMB} MB total");
        Console.WriteLine($"  - Accounts: {accountsBytes / (1024 * 1024)} MB ({MaxAccounts:N0} contas × {sizeof(AccountRiskState)} bytes)");
        Console.WriteLine($"  - Positions: {positionsBytes / (1024 * 1024)} MB ({MaxAccounts:N0} contas × {MaxSymbols} símbolos × {sizeof(PositionState)} bytes)");

        // Aloca bloco contínuo de memória nativa totalmente alinhada
        _accounts = (AccountRiskState*)NativeMemory.AllocZeroed((nuint)(MaxAccounts * sizeof(AccountRiskState)));

        _positions = (PositionState*)NativeMemory.AllocZeroed((nuint)((long)MaxAccounts * MaxSymbols * sizeof(PositionState)));

        if (_accounts == null || _positions == null)
        {
            Console.WriteLine($"[RiskMemoryState] ERRO: Falha ao alocar {totalMB} MB de memória nativa");
            Cleanup();
            throw new OutOfMemoryException($"Falha ao alocar memória nativa para o motor de risco ({totalMB} MB solicitados).");
        }

        Console.WriteLine($"[RiskMemoryState] Memória alocada com sucesso");
    }

    // O AccountId serve como índice direto (O(1) absoluto sem overhead de hash)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref AccountRiskState GetAccount(long accountId)
    {
        if (accountId < 0 || accountId >= MaxAccounts)
            throw new ArgumentOutOfRangeException(nameof(accountId), $"Account ID {accountId} fora do intervalo [0, {MaxAccounts})");
        return ref _accounts[accountId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PositionState GetPosition(long accountId, int symbolId)
    {
        // Validação de bounds
        if (accountId < 0 || accountId >= MaxAccounts)
            throw new ArgumentOutOfRangeException(nameof(accountId), $"Account ID {accountId} fora do intervalo [0, {MaxAccounts})");
        if (symbolId < 0 || symbolId >= MaxSymbols)
            throw new ArgumentOutOfRangeException(nameof(symbolId), $"Symbol ID {symbolId} fora do intervalo [0, {MaxSymbols})");

        // Cálculo do deslocamento no bloco contíguo de memória nativa
        long offset = (accountId * MaxSymbols) + symbolId;
        return ref _positions[offset];
    }
    /// <summary>
    /// Alimenta ou sobrescreve os limites de risco de uma conta usando a in-place copy da memória nativa.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LoadAccountState(in AccountRiskState state)
    {
        long accountId = state.AccountId;

        if (accountId < 0 || accountId >= MaxAccounts)
            throw new ArgumentOutOfRangeException(nameof(state.AccountId), $"ID {accountId} excede o limite {MaxAccounts}.");

        // Cópia em bloco direta na memória unmanaged
        _accounts[accountId] = state;
    }
    /// <summary>
    /// Alimenta ou sobrescreve a posição de um ativo específico para uma conta.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LoadPositionState(long accountId, int symbolId, in PositionState state)
    {
        if (accountId < 0 || accountId >= MaxAccounts)
            throw new ArgumentOutOfRangeException(nameof(accountId));

        if (symbolId < 0 || symbolId >= MaxSymbols)
            throw new ArgumentOutOfRangeException(nameof(symbolId));

        long offset = (accountId * MaxSymbols) + symbolId;

        // Cópia em bloco direta na memória unmanaged
        _positions[offset] = state;
    }

    private void Cleanup()
    {
        if (_accounts != null) NativeMemory.Free(_accounts);
        if (_positions != null) NativeMemory.Free(_positions);
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }



    ~RiskMemoryState() => Cleanup();
}
