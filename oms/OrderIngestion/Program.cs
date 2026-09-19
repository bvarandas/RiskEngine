using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceDefaults;
using ServiceDefaults.interfaces;
using System;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;
using FixSessionManager;

public static class Program
{
    public static int Main(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .ConfigureServices((ctx, services) =>
            {
                // Core in-memory ring buffer
                services.AddSingleton<IFastRingBuffer, FastRingBuffer>();

                // B3 FIX Fast Sender (requires Socket to B3)
                services.AddSingleton<B3FixFastSender>(sp =>
                {
                    // Para desenvolvimento/teste, podemos usar um mock ou null socket
                    // Em produção, seria conectado ao B3
                    var fixHostCfg = ctx.Configuration["B3:Host"] ?? "127.0.0.1";
                    var fixPortCfg = ctx.Configuration["B3:Port"] ?? "8080";
                    if (!int.TryParse(fixPortCfg, out var fixPort)) fixPort = 8080;

                    Console.WriteLine($"[DI] Configurando B3FixFastSender para {fixHostCfg}:{fixPort}");
                    // TODO: Criar socket real quando conectar ao B3
                    // Por enquanto, criar um mock/dummy socket
                    return new B3FixFastSender(null!); // Placeholder
                });

                // Order ingestion engine. Config from appsettings.json
                services.AddSingleton<OrderIngestionEngine>(sp =>
                {
                    var ring = sp.GetRequiredService<IFastRingBuffer>();
                    var ip = ctx.Configuration["Ingestion:IP"] ?? "127.0.0.1";
                    int port = 9000;
                    var portCfg = ctx.Configuration["Ingestion:Port"];
                    if (!string.IsNullOrEmpty(portCfg) && int.TryParse(portCfg, out var portVal)) port = portVal;
                    int core = 2;
                    var coreCfg = ctx.Configuration["Ingestion:Core"];
                    if (!string.IsNullOrEmpty(coreCfg) && int.TryParse(coreCfg, out var coreVal)) core = coreVal;
                    return new OrderIngestionEngine(ip, port, core, (ServiceDefaults.interfaces.IRingBuffer)ring);
                });

                // Hosted services
                services.AddHostedService<OrderIngestionHostedService>();
            });

        var host = builder.Build();
        host.Run();
        return 0;
    }
}

internal sealed class OrderIngestionHostedService : IHostedService, IDisposable
{
    private readonly OrderIngestionEngine _ingestion;

    public OrderIngestionHostedService(OrderIngestionEngine ingestion)
    {
        _ingestion = ingestion;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[OrderIngestion] Iniciando Order Ingestion Engine...");
        _ingestion.Start();
        Console.WriteLine("[OrderIngestion] Order Ingestion Engine iniciado e aguardando conexões");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[OrderIngestion] Parando Order Ingestion Engine...");
        _ingestion.Stop();
        Console.WriteLine("[OrderIngestion] Order Ingestion Engine parado");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _ingestion?.Dispose();
    }
}

