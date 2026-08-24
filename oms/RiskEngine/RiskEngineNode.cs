using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RiskEngine.Ha;

public enum NodeRole
{
    Passive,
    Active
}

/// <summary>
/// Motor de Risco com suporte a Promoção de Estado em Runtime
/// </summary>
public sealed class RiskEngineNode : BackgroundService
{
    private readonly ILogger<RiskEngineNode> _logger;

    // Estado do Nó mantido em memória
    private volatile NodeRole _currentRole = NodeRole.Passive;

    // Armazena o timestamp do último Heartbeat recebido do Nó Ativo (ticks)
    private long _lastHeartbeatTicks;

    // Threshold para considerar o Nó Ativo morto (ex: 50 milissegundos)
    private readonly long _heartbeatTimeoutTicks = TimeSpan.FromMilliseconds(50).Ticks;

    public NodeRole Role => _currentRole;

    public RiskEngineNode(ILogger<RiskEngineNode> logger)
    {
        _logger = logger;
        _lastHeartbeatTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Chamado continuamente pelo Subscriber de Rede (Aeron/IPC) ao receber eventos do Nó Ativo
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHeartbeatReceived()
    {
        // Atualiza a marca temporal atomicamente sem travas
        Interlocked.Exchange(ref _lastHeartbeatTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Tenta processar uma ordem. Só executa envio à B3 se o nó for o ATIVO.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ProcessOrderPreTrade(long accountId, double orderAmount)
    {
        if (_currentRole != NodeRole.Active)
        {
            // Nó Passivo: ignora o envio externo (apenas mantém o saldo sincronizado via replicação)
            return false;
        }

        // --- LÓGICA DE RISCO EM MEMÓRIA LOCAL ---
        // ValidateRiskAndDeductBalance(accountId, orderAmount);

        // SendToB3FixEngine();
        return true;
    }

    /// <summary>
    /// Loop de monitoramento de alta frequência (Watchdog)
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Iniciando Nó de Risco no modo PASSIVO...");

        // .NET 10 PeriodicTimer de alta precisão
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_currentRole == NodeRole.Active)
            {
                // Se já é o nó ativo, ele pode emitir seu próprio heartbeat para a rede
                PublishHeartbeatToCluster();
                continue;
            }

            // Verifica se o nó Ativo parou de responder
            long elapsedTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastHeartbeatTicks);
            long elapsedMs = (long)TimeSpan.FromTicks(elapsedTicks).TotalMilliseconds;

            if (elapsedTicks > _heartbeatTimeoutTicks)
            {
                PromoteToActive(elapsedMs);
            }
        }
    }

    /// <summary>
    /// Promove o Nó de Passivo para Ativo (Failover)
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    private void PromoteToActive(long elapsedMs)
    {
        if (_currentRole == NodeRole.Active) return;

        _logger.LogWarning("CRITICAL: Heartbeat do Nó Ativo ausente por {ElapsedMs}ms! Promovendo nó para ATIVO...", elapsedMs);

        // 1. Alterna o papel do nó para Ativo
        _currentRole = NodeRole.Active;

        // 2. Notifica a infraestrutura de rede / Router de Entrada (Ingress)
        // Exemplo: Dispara um sinal Gratuitous ARP, registra no Consul/etcd, ou assume o VIP (Virtual IP)
        NotifyIngressRouterOfPromotion();

        _logger.LogInformation("Nó promovido com SUCESSO. Assumindo o envio de ordens para a B3.");
    }

    private void PublishHeartbeatToCluster()
    {
        // Envia um pacote UDP/Aeron leve para informar aos nós passivos que este nó está vivo
    }

    private void NotifyIngressRouterOfPromotion()
    {
        // Lógica para avisar o roteador de ordens que a partir de agora as novas ordens devem vir para este IP
    }
}