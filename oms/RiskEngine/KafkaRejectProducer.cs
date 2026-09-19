using Confluent.Kafka;
using ServiceDefaults.events;

namespace PreTradeRisk;

public sealed class KafkaRejectProducer : IDisposable
{
    private readonly IProducer<Null, string> _producer;
    private readonly string _topic;
    private readonly Thread _workerThread;
    private readonly CancellationTokenSource _cts;
    // Campo que estava ausente
    private readonly OutboundRejectRingBuffer _rejectBuffer;

    public KafkaRejectProducer(string bootstrapServers, string topic, OutboundRejectRingBuffer rejectBuffer)
    {
        _topic = topic;
        _rejectBuffer = rejectBuffer;
        _cts = new CancellationTokenSource();

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            LingerMs = 1, // Baixa latência no envio em lote
            Acks = Acks.None // 'Fire and forget' para não estrangular a thread
        };

        _producer = new ProducerBuilder<Null, string>(config).Build();

        _workerThread = new Thread(ProcessRejects)
        {
            Name = "KafkaRejectPublisher",
            IsBackground = true,
            Priority = ThreadPriority.Normal // Menor prioridade que o RiskLoop
        };
    }
    private void ProcessRejects()
    {
        long nextSequenceToProcess = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            long availableSequence = _rejectBuffer.GetProducerSequence();

            if (nextSequenceToProcess < availableSequence)
            {
                while (nextSequenceToProcess < availableSequence)
                {
                    ref RejectEvent rejectEvent = ref _rejectBuffer.Get(nextSequenceToProcess);

                    // Fora do Hot Path: Agora você pode alocar string e pagar o custo
                    string fixMessage = BuildFix44Reject(ref rejectEvent);

                    _producer.Produce(_topic, new Message<Null, string> { Value = fixMessage }, DeliveryHandler);

                    nextSequenceToProcess++;
                }

                // Avisa o Ring Buffer que esses espaços estão livres
                _rejectBuffer.UpdateConsumerSequence(nextSequenceToProcess);
            }
            else
            {
                // Como rejeições não ocorrem em 99% das ordens, não faz sentido um SpinWait agressivo aqui.
                // Um Thread.Sleep(0) ou Yield poupa a CPU e afeta apenas a latência da rejeição (que já falhou no risco).
                Thread.Yield();
            }
        }
    }

    private string BuildFix44Reject(ref RejectEvent reject)
    {
        unsafe
        {
            // Congela os endereços de memória dos buffers, 
            // garantindo que o GC não mova a struct durante a leitura.
            fixed (byte* pClOrdId = reject.ClOrdId)
            fixed (byte* pSymbol = reject.Symbol)
            {
                string clOrdId = System.Text.Encoding.ASCII.GetString(pClOrdId, 20).Trim('\0');
                string symbol = System.Text.Encoding.ASCII.GetString(pSymbol, 10).Trim('\0');

                return $"8=FIX.4.4\x019=000\x0135=8\x0137={reject.OrderId}\x0111={clOrdId}\x01150=8\x0139=8\x0155={symbol}\x0154={reject.Side}\x0158=Risco:{reject.ReasonCode}\x0110=000\x01";
            }
        }
    }

    public void Start() => _workerThread.Start();

    private void DeliveryHandler(DeliveryReport<Null, string> report)
    {
        if (report.Error.IsError)
        {
            // Apenas log de erro em background, não afeta o hot path
            Console.WriteLine($"Erro ao enviar Reject pro Kafka: {report.Error.Reason}");
        }
    }
    public void Dispose()
    {
        _cts.Cancel();
        _workerThread.Join();
        _producer.Flush(TimeSpan.FromSeconds(2));
        _producer.Dispose();
    }
}
