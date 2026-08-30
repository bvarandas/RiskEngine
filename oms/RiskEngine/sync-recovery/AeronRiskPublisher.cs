using Adaptive.Aeron;
using Adaptive.Aeron.LogBuffer;
using Adaptive.Agrona;
using Adaptive.Agrona.Concurrent;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace sync;

/// <summary>
/// Publicador de Mutação na Instância Ativa via Aeron
/// </summary>
public sealed class AeronRiskPublisher
{
    private readonly Aeron _aeron;
    private readonly Publication _publication;
    private readonly UnsafeBuffer _buffer;

    public AeronRiskPublisher(string channel = "aeron:udp?endpoint=224.0.1.1:40123", int streamId = 1001)
    {
        // Conecta ao Media Driver do Aeron local
        var ctx = new Aeron.Context();
        _aeron = Aeron.Connect(ctx);

        // Canal Multicast ou UDP Unicast direto
        _publication = _aeron.AddPublication(channel, streamId);

        // Alloc de memória Off-Heap (Sem interferência do GC do .NET)
        _buffer = new UnsafeBuffer(BufferUtil.AllocateDirectAligned(128, 64));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReplicateBalanceChange(long accountId, double newBalance, long sequence)
    {
        // Serialização binária direta em Span/Buffer em nanosegundos
        _buffer.PutLong(0, accountId);
        _buffer.PutDouble(8, newBalance);
        _buffer.PutLong(16, sequence);

        // Oferta a mensagem no Ring Buffer do Aeron Media Driver
        long result = _publication.Offer(_buffer, 0, 24);

        if (result < 0)
        {
            // Trata backpressure de rede (Administering Buffer Full / Not Connected)
            HandleBackpressure(result);
        }
    }

    private static void HandleBackpressure(long result)
    {
        if (result == Publication.BACK_PRESSURED)
        {
            // Yield ou spin reduzido em estratégias de HFT
            Thread.SpinWait(10);
        }
    }
}

/// <summary>
/// Consumidor de Mutação na Instância Passiva (Hot-Standby)
/// </summary>
public sealed class AeronRiskSubscriber
{
    private readonly Subscription _subscription;
    private readonly FragmentHandler _fragmentHandler;
    private Action<long, double, long>? _onMutationReceived;

    public AeronRiskSubscriber(Aeron aeron, string channel = "aeron:udp?endpoint=224.0.1.1:40123", int streamId = 1001)
    {
        _subscription = aeron.AddSubscription(channel, streamId);

        // Instanciamos o FragmentHandler UMA ÚNICA VEZ no construtor.
        // Isso evita criar novos delegates a cada chamada de .Poll(), zerando alocações de GC no loop.
        _fragmentHandler = OnFragment;
    }

    public void PollMutations(Action<long, double, long> onMutationReceived)
    {
        _onMutationReceived = onMutationReceived;

        // Agora o tipo casa exatamente com o esperado pela biblioteca Aeron
        _subscription.Poll(_fragmentHandler, fragmentLimit: 10);
    }

    // Método que segue a assinatura exata do delegate FragmentHandler
    private void OnFragment(IDirectBuffer buffer, int offset, int length, Header header)
    {
        long accountId = buffer.GetLong(offset);
        double newBalance = buffer.GetDouble(offset + 8);
        long sequence = buffer.GetLong(offset + 16);

        _onMutationReceived?.Invoke(accountId, newBalance, sequence);
    }
}