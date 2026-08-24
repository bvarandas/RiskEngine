using ServiceDefaults;
using ServiceDefaults.events;
using ServiceDefaults.interfaces;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public sealed class OrderIngestionEngine : IDisposable
{
    private const int ReceiveBufferSize = 65536; // 64KB Ring Buffer de Rede Nativo
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly int _cpuCoreId;
    private readonly IRingBuffer _ringBuffer;

    private Socket? _listenerSocket;
    private Socket? _clientSocket;
    private Thread? _ingestionThread;
    private volatile bool _isRunning;
    private static readonly NativeSymbolMapper _symbolMapper = new NativeSymbolMapper();

    public OrderIngestionEngine(string ipAddress, int port, int cpuCoreId, IRingBuffer ringBuffer)
    {
        _ipAddress = ipAddress;
        _port = port;
        _cpuCoreId = cpuCoreId;
        _ringBuffer = ringBuffer;
    }

    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _ingestionThread = new Thread(RunIngestionLoop)
        {
            Name = "OrderIngestionWorker",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _ingestionThread.Start();
    }

    public void Stop()
    {
        _isRunning = false;
        _clientSocket?.Close();
        _listenerSocket?.Close();
        _ingestionThread?.Join();
    }

    private unsafe void RunIngestionLoop()
    {
        // 1. Thread Affinity (Fixa a thread em um núcleo físico exclusivo de CPU)
        SetCpuAffinity(_cpuCoreId);

        // 2. Alocação de buffer não gerenciado (Fora do GC)
        IntPtr nativeBufferPointer = (IntPtr) NativeMemory.Alloc(ReceiveBufferSize);

        try
        {
            unsafe
            {
                Span<byte> receiveBuffer = new Span<byte>(nativeBufferPointer.ToPointer(), ReceiveBufferSize);

                // 3. Setup de Sockets de Baixa Latência
                SetupSocket();

                while (_isRunning)
                {
                    if (_clientSocket == null || !_clientSocket.Connected)
                    {
                        AcceptClient();
                        continue;
                    }

                    // 4. Recebe dados sem alocação
                    int bytesRead = ReceiveDataNonBlocking(_clientSocket, receiveBuffer);

                    if (bytesRead > 0)
                    {
                        // Processa o frame completo recebido da rede
                        ProcessNetworkFrame(receiveBuffer.Slice(0, bytesRead));
                    }
                    else if (bytesRead == 0)
                    {
                        // Conexão encerrada pelo cliente
                        _clientSocket.Dispose();
                        _clientSocket = null;
                    }
                    else
                    {
                        // Sem dados no socket (EWOULDBLOCK): Busy Spin para evitar Context Switch
                        Thread.SpinWait(1);
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free((void*)nativeBufferPointer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessNetworkFrame(ReadOnlySpan<byte> frameData)
    {
        int offset = 0;

        // Suporta multiplexação de várias mensagens no mesmo pacote TCP
        while (offset < frameData.Length)
        {
            // Assumindo pacote de tamanho fixo ou delimitado para o exemplo (ex: 64 bytes por ordem binária)
            const int messageSize = 64;

            if (offset + messageSize > frameData.Length) break;

            ReadOnlySpan<byte> messageBytes = frameData.Slice(offset, messageSize);

            // A. Reserva o próximo slot no Ring Buffer
            long seq = _ringBuffer.NextSequence();

            // B. Obtém a referência direta da struct pré-alocada no Ring Buffer (Zero Copy)
            ref OrderEvent eventSlot = ref _ringBuffer.Get(seq);

            // C. Grava o Timestamp de Ingestão imediatamente (nanosegundos)
            eventSlot.IngestionTimestampNs = System.Diagnostics.Stopwatch.GetTimestamp();

            // D. Parseia os bytes do protocolo diretamente para a memória da struct no Ring Buffer
            ParseBinaryProtocol(messageBytes, ref eventSlot);

            // E. Libera a mensagem para os consumidores do Ring Buffer (Risk Engine / Engine Hot Path)
            _ringBuffer.Publish(seq);
            
            offset += messageSize;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ParseBinaryProtocol(ReadOnlySpan<byte> source, ref OrderEvent target)
    {
        // Exemplo de extração de protocolo binário via MemoryMarshal/Unsafe (sem alocações)
        target.OrderId = MemoryMarshal.Read<long>(source.Slice(0, 8));
        target.AccountId = MemoryMarshal.Read<long>(source.Slice(8, 8));
        target.Price = MemoryMarshal.Read<double>(source.Slice(16, 8));
        target.Quantity = MemoryMarshal.Read<int>(source.Slice(24, 4));
        target.Side = source[28];
        target.OrderType = source[29];

        // Copia o Symbol (16 bytes UTF8/ASCII) diretamente para o ponteiro fixo da struct
        fixed (byte* symbolPtr = target.Symbol)
        {
            ReadOnlySpan<byte> symbolBytes = source.Slice(30, 16);
            SymbolKey16 key = new SymbolKey16(symbolBytes);
            target.SymbolId = _symbolMapper.GetSymbolId(key);
            symbolBytes.CopyTo(new Span<byte>(symbolPtr, 16));
        }
    }

    private void SetupSocket()
    {
        _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Desativa Algoritmo de Nagle (envio imediato) e ativa reuso de porta
        _listenerSocket.NoDelay = true;
        _listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        _listenerSocket.Bind(new IPEndPoint(IPAddress.Parse(_ipAddress), _port));
        _listenerSocket.Listen(100);
    }

    private void AcceptClient()
    {
        try
        {
            _clientSocket = _listenerSocket?.Accept();
            if (_clientSocket != null)
            {
                _clientSocket.NoDelay = true; // Desativa Nagle na conexão aceita
                _clientSocket.Blocking = false; // Define socket como não-bloqueante
                _clientSocket.ReceiveBufferSize = ReceiveBufferSize;
            }
        }
        catch
        {
            Thread.SpinWait(10);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReceiveDataNonBlocking(Socket socket, Span<byte> buffer)
    {
        try
        {
            return socket.Receive(buffer, SocketFlags.None, out SocketError errorCode);

            if (errorCode == SocketError.WouldBlock)
                return -1; // Sem dados no momento
        }
        catch
        {
            return 0; // Conexão com erro/fechada
        }
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
        Stop();
        _listenerSocket?.Dispose();
        _clientSocket?.Dispose();
    }
}