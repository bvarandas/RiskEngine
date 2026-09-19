using FixSessionManager;
using ServiceDefaults;
using ServiceDefaults.events;
using ServiceDefaults.interfaces;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PreTradeRisk;

public sealed class PreTradeRiskEngine : IDisposable
{
    private readonly B3FixFastSender _fixSender;
    private readonly byte[] _senderCompIdBytes;
    private readonly byte[] _targetCompIdBytes;

    private readonly IRingBuffer _ringBuffer;
    private readonly RiskMemoryState _riskState;
    private readonly int _cpuCoreId;

    // Sequência atual que o Risk Engine já processou
    private PaddedLong _consumerSequence;

    // Ponto de publicação do Produtor (Ingestão)
    private PaddedLong _producerSequenceReference;

    private Thread? _workerThread;
    private volatile bool _isRunning;
    private readonly OutboundRejectRingBuffer _rejectBuffer;



    public PreTradeRiskEngine(IRingBuffer ringBuffer, RiskMemoryState riskState, int cpuCoreId, B3FixFastSender fixSender, OutboundRejectRingBuffer rejectBuffer)
    {
        _fixSender = fixSender;
        _ringBuffer = ringBuffer;
        _riskState = riskState;
        _cpuCoreId = cpuCoreId;
        _consumerSequence.Value = -1L;
        _producerSequenceReference.Value = -1L;
        _rejectBuffer = rejectBuffer;
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        _workerThread = new Thread(RunRiskLoop)
        {
            Name = "PreTradeRiskWorker",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _workerThread.Start();
    }

    private void RunRiskLoop()
    {
        SetCpuAffinity(_cpuCoreId);

        long nextSequenceToProcess = _consumerSequence.Value + 1;

        // Cache local dos spans de identificação FIX para evitar overhead em cada iteração
        ReadOnlySpan<byte> senderCompId = _senderCompIdBytes;
        ReadOnlySpan<byte> targetCompId = _targetCompIdBytes;

        while (_isRunning)
        {
            // O Consumidor pergunta: "A Ingestão já publicou a sequência que eu quero ler?"
            long availableSequence = Volatile.Read(ref _ringBuffer.GetProducerSequence());

            if (nextSequenceToProcess <= availableSequence)
            {
                // Processa o Batch: Limpa o backlog da rede se houver rajada (burst) de ordens
                while (nextSequenceToProcess <= availableSequence)
                {
                    ref OrderEvent orderToValidate = ref _ringBuffer.Get(nextSequenceToProcess);

                    // Executa a lógica de risco
                    bool passed = EvaluateRisk(ref orderToValidate);

                    if (passed)
                    {
                        unsafe
                        {
                            // Ancora o ponteiro de origem
                            fixed (byte* pClOrdId = orderToValidate.ClOrdId)
                            {
                                // Alocação stack-only (Heap = 0)
                                FixOrder fixOrder = new FixOrder(
                                    sourceClOrdId: pClOrdId,
                                    accountId: orderToValidate.AccountId,
                                    price: orderToValidate.Price,
                                    quantity: orderToValidate.Quantity,
                                    side: orderToValidate.Side,
                                    orderType: orderToValidate.OrderType,
                                    timeInForce: orderToValidate.TimeInForce,
                                    // Dependendo do seu FIX Engine, você pode precisar converter Ns para Ticks ou Epoch Ns
                                    transactTime: orderToValidate.IngestionTimestampNs,
                                    symbolSpan: orderToValidate.Symbol // Assumindo que você tem este método
                                );

                                _fixSender.SendNewOrderSingle(in fixOrder, senderCompId, targetCompId);
                            }
                        }
                    }
                    else
                    {
                        // ... dentro do Else de rejeição
                        ref RejectEvent rejectEvent = ref _rejectBuffer.Claim(out long sequence);

                        rejectEvent.OrderId = orderToValidate.OrderId;
                        rejectEvent.Side = orderToValidate.Side;
                        rejectEvent.ReasonCode = 1;

                        unsafe
                        {
                            // O bloco 'fixed' ancora os endereços na memória, impedindo o GC 
                            // de mover os arrays subjacentes durante a cópia dos bytes.
                            fixed (byte* pSrcClOrdId = orderToValidate.ClOrdId)
                            fixed (byte* pDestClOrdId = rejectEvent.ClOrdId)
                            {
                                Buffer.MemoryCopy(
                                source: pSrcClOrdId,
                                destination: pDestClOrdId,
                                destinationSizeInBytes: 20,
                                sourceBytesToCopy: 20);
                            }
                            /// HACK: Copiando o SymbolBuffer diretamente.
                            // Nota: Como não tenho a implementação do seu 'SymbolBuffer', 
                            // a cópia do ativo dependerá da estrutura interna dele.
                        }

                        _rejectBuffer.Commit(sequence);
                    }

                    nextSequenceToProcess++;
                }

                // Atualiza a posição do consumidor (caso haja estágios posteriores ao risco no ring buffer)
                Volatile.Write(ref _consumerSequence.Value, nextSequenceToProcess - 1);
            }
            else
            {
                // Sem novas ordens no Ring Buffer.
                // Spin wait agressivo para manter o cache aquecido
                Thread.SpinWait(1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EvaluateRisk(ref OrderEvent order)
    {
        // 1. Acesso O(1) direto em memória via referência
        ref AccountRiskState account = ref _riskState.GetAccount(order.AccountId);

        // 2. Kill Switch (Conta Bloqueada)
        if (account.IsBlocked) return false;

        // 3. Fat Finger Check (Quantidade máxima por boleta)
        if (order.Quantity > account.MaxOrderQuantity) return false;

        // -----------------------------------------------------------------
        // REGRA DE COMPRA (SIDE = 1) - VALIDAÇÃO DE SALDO / MARGEM
        // -----------------------------------------------------------------

        if (order.Side == 1) // 1 = Buy
        {
            long orderValue = order.Price * order.Quantity;
            long availableCash = account.AvailableCash - account.BlockedCash;

            if (availableCash < orderValue)
            {
                return false; // Rejeitado: Sem margem financeira
            }

            // Aprovação de Risco: Bloqueia a garantia imediatamente na memória.
            // Como esta thread é a ÚNICA escritora deste saldo, não precisamos de 'lock' nem Interlocked.
            account.BlockedCash += orderValue;
            return true;
        }

        // -----------------------------------------------------------------
        // REGRA DE VENDA (SIDE = 2) - VALIDAÇÃO DE CUSTÓDIA / POSIÇÃO
        // -----------------------------------------------------------------
        if (order.Side == 2)
        {
            // Resolução O(1) do ponteiro de custódia do ativo para este cliente específico
            ref PositionState position = ref _riskState.GetPosition(order.AccountId, order.SymbolId);

            // Quantidade disponível = Custódia total - Ordens de venda já enviadas que aguardam execução
            long availableQuantity = position.TotalQuantity - position.BlockedQuantity;

            if (availableQuantity < order.Quantity)
            {
                return false; // Rejeitado por falta de custódia suficiente (Venda a descoberto não permitida)
            }

            // APROVADO: Bloqueia a quantidade vendida na custódia do ativo em memória
            position.BlockedQuantity += order.Quantity;
            return true;
        }


        return false;
    }

    private static void SetCpuAffinity(int coreId)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            var thread = System.Diagnostics.Process.GetCurrentProcess().Threads
                .Cast<System.Diagnostics.ProcessThread>()
                .First(t => t.Id == Environment.CurrentManagedThreadId);

            thread.ProcessorAffinity = new IntPtr(1 << coreId);
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _workerThread?.Join();
    }
}

// Estrutura de Padding
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedLong
{
    [FieldOffset(24)] public long Value;
}