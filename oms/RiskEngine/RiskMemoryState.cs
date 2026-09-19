using ServiceDefaults;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PreTradeRisk;

public unsafe sealed class RiskMemoryState : IDisposable
{
    private readonly AccountRiskState* _accounts;
    // Tabela contígua de Custódia: MaxAccounts x MaxSymbols
    // Indexação aritmética pura O(1)
    private readonly PositionState* _positions;
    private const long MaxAccounts = 1_000_000;
    private const int MaxSymbols = 256; // Suporta até 256 ativos no OMS (reduzido de 1024 para ~4 GB footprint)

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

    /// <summary>
    /// Load accounts and positions using a repository implementation.
    /// </summary>
    public void LoadFromRepository<TRepository>(TRepository repository) 
        where TRepository : class
    {
        if (repository == null) throw new ArgumentNullException(nameof(repository));

        // Use reflection to call LoadAccounts and LoadPositions methods on the repository
        var loadAccountsMethod = repository.GetType().GetMethod("LoadAccounts");
        var loadPositionsMethod = repository.GetType().GetMethod("LoadPositions");

        if (loadAccountsMethod == null || loadPositionsMethod == null)
            throw new InvalidOperationException("Repository must implement LoadAccounts() and LoadPositions() methods");

        // Load accounts
        var accountsResult = loadAccountsMethod.Invoke(repository, null);
        if (accountsResult is System.Collections.IList accountsList)
        {
            foreach (var acc in accountsList)
            {
                if (acc is AccountRiskState a)
                {
                    long id = a.AccountId;
                    if (id < 0 || id >= MaxAccounts) continue;
                    ref var slot = ref GetAccount(id);
                    slot = a;
                }
            }
        }

        // Load positions
        var positionsResult = loadPositionsMethod.Invoke(repository, null);
        if (positionsResult is System.Collections.IList positionsList)
        {
            foreach (var pos in positionsList)
            {
                var tupleType = pos.GetType();
                if (tupleType.IsGenericType && tupleType.GetGenericTypeDefinition() == typeof(ValueTuple<,,>))
                {
                    var accountIdProp = tupleType.GetProperty("Item1");
                    var symbolIdProp = tupleType.GetProperty("Item2");
                    var positionProp = tupleType.GetProperty("Item3");

                    if (accountIdProp != null && symbolIdProp != null && positionProp != null)
                    {
                        var accountId = (long)accountIdProp.GetValue(pos)!;
                        var symbolId = (int)symbolIdProp.GetValue(pos)!;
                        var positionState = (PositionState)positionProp.GetValue(pos)!;

                        if (accountId < 0 || accountId >= MaxAccounts) continue;
                        if (symbolId < 0 || symbolId >= MaxSymbols) continue;
                        ref var posSlot = ref GetPosition(accountId, symbolId);
                        posSlot = positionState;
                    }
                }
            }
        }
    }

    ~RiskMemoryState() => Cleanup();
}
