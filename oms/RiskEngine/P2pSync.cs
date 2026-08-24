using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RiskEngine.P2PSync;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AccountBalanceChunk
{
    public long AccountId;
    public double Balance;
}

public sealed class ActiveNodeSnapshotServer : BackgroundService
{
    private readonly ILogger<ActiveNodeSnapshotServer> _logger;

    // Tabela de Risco em Memória do Pod Ativo
    private readonly Dictionary<long, double> _activeMemory = new()
    {
        { 1001, 150000.50 },
        { 1002, 85000.00 },
        { 1003, 3200.75 }
    };

    public ActiveNodeSnapshotServer(ILogger<ActiveNodeSnapshotServer> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ouve em TODAS as interfaces de rede do Pod na porta 5005
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, 5005));
        listener.Listen(10);

        _logger.LogInformation("[POD-A ATIVO] Servidor P2P Snapshot ouvindo na porta 5005...");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Aguarda conexão de um Novo Pod (Pod B)
            Socket clientSocket = await listener.AcceptAsync(stoppingToken);
            _logger.LogInformation("[POD-A ATIVO] Novo Pod conectado para sync: {RemoteIP}", clientSocket.RemoteEndPoint);

            // Envia o Snapshot sem bloquear a thread principal de validação de ordens
            _ = Task.Run(() => SendMemoryToPeerAsync(clientSocket, stoppingToken), stoppingToken);
        }
    }

    private async Task SendMemoryToPeerAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        {
            var pipe = new Pipe();
            Task fillTask = FillPipeFromMemoryAsync(_activeMemory, pipe.Writer, ct);
            Task sendTask = SendPipeToNetworkAsync(socket, pipe.Reader, ct);

            await Task.WhenAll(fillTask, sendTask);
            _logger.LogInformation("[POD-A ATIVO] Transferência de Snapshot concluída com sucesso.");
        }
    }

    private async Task FillPipeFromMemoryAsync(Dictionary<long, double> memory, PipeWriter writer, CancellationToken ct)
    {
        foreach (var entry in memory)
        {
            Memory<byte> memoryBuffer = writer.GetMemory(Unsafe.SizeOf<AccountBalanceChunk>());
            var chunk = new AccountBalanceChunk { AccountId = entry.Key, Balance = entry.Value };

            MemoryMarshal.Write(memoryBuffer.Span, ref chunk);
            writer.Advance(Unsafe.SizeOf<AccountBalanceChunk>());

            FlushResult result = await writer.FlushAsync(ct);
            if (result.IsCanceled || result.IsCompleted) break;
        }

        await writer.CompleteAsync();
    }

    private async Task SendPipeToNetworkAsync(Socket socket, PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length > 0)
            {
                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    await socket.SendAsync(segment, SocketFlags.None, ct);
                }
            }

            reader.AdvanceTo(buffer.End);
            if (result.IsCompleted) break;
        }

        await reader.CompleteAsync();
    }
}