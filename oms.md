+----------------------------------------------------------------------------------------------------------------------------+
|                                    OMS DE ALTA PERFORMANCE (BAIXA LATÊNCIA & PÓS-TRADE)                                    |
+----------------------------------------------------------------------------------------------------------------------------+
                                                                                                                              
 [ FRONT / EMS / ALGO ]                                                                                      [ B3 BOLSA ]    
         │                                                                                                         ▲         
         │ (FIX / Protocolo Binário)                                                                               │ (FIX)   
         ▼                                                                                                         │         
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────┼────────┐
│ HOT PATH (Caminho Crítico Síncrono - In-Memory / Single-Threaded Event Loop)                                     │        │
│                                                                                                                  │        │
│   ┌───────────────────────────┐       ┌───────────────────────────┐       ┌───────────────────────────┐          │        │
│   │    Order Ingestion        │──────>│   Pre-Trade Risk Engine   │──────>│     FIX Session Manager   │──────────┘        │
│   │   (FIX Engine / Binary)   │       │   (Matrizes em Memória)   │       │   (Sessão Act/Pass PUMA)  │                   │
│   └───────────────────────────┘       └───────────────────────────┘       └───────────────────────────┘                   │
│                 │                                   │                                   │                                 │
└─────────────────┼───────────────────────────────────┼───────────────────────────────────┼─────────────────────────────────┘
                  │ (Disruptor Ring Buffer /          │ (Event Data)                      │ (Execution Reports)             
                  ▼ Zero-Copy Bus)                    ▼                                   ▼                                 
┌───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ WARM / COLD PATH (Assíncrono - Orientado a Eventos / Event Sourcing)                                                      │
│                                                                                                                           │
│   ┌───────────────────────────┐       ┌───────────────────────────┐       ┌───────────────────────────┐                   │
│   │   Journaler / Ledger      │       │     Real-Time Pricer      │       │      Drop Copy Route      │                   │
│   │ (Persistência Raft/Local) │       │   (Cálculo MTM/Taxas)     │       │   (Distribuição Telas)    │                   │
│   └───────────────────────────┘       └───────────────────────────┘       └───────────────────────────┘                   │
│                 │                                   │                                   │                                 │
│                 ▼                                   ▼                                   ▼                                 │
│   ┌───────────────────────────┐       ┌───────────────────────────┐       ┌───────────────────────────┐                   │
│   │   State Store / DB        │       │    Allocation & MTR       │       │    Sistemas Externos      │                   │
│   │   (Auditoria/Snapshot)    │       │   (Pós-Trade / Give-up)   │       │    (Backoffice / Risco)   │                   │
│   └───────────────────────────┘       └───────────────────────────┘       └───────────────────────────┘                   │
└─────────────────────────────────────────────────────────────────────┬─────────────────────────────────────────────────────┘
                                                                      │                                                      
                                                                      ▼ (Arquivos de Alocação)                               
                                                            ┌───────────────────────────┐                                    
                                                            │    Sinacor / Custódia     │                                    
                                                            └───────────────────────────┘


O Hot Path (Caminho Crítico Síncrono)
O objetivo único deste bloco é validar e despachar a ordem para o PUMA Trading System da B3 o mais rápido possível. Ele roda de forma single-threaded (geralmente fixado em uma CPU dedicada via thread affinity) para evitar trocas de contexto (context switch).

Order Ingestion (FIX/Binary Engine): Remove-se completamente o API Gateway genérico. O cliente institucional ou o motor de algoritmos conecta-se diretamente a um componente de ingestão nativo via protocolo FIX ou protocolo binário proprietário de ultra-baixa latência. Ele faz o parsing da mensagem diretamente na memória do processo.

Pre-Trade Risk Engine (Acoplado em Memória): Em vez de uma chamada de rede para outro microsserviço, o Risco Pre-Trade opera como uma biblioteca in-memory dentro do mesmo core do ciclo de vida da ordem. Ele valida limites de posição, garantias disponíveis, travas de fat-finger e regras BSM (spoofing, layering) consultando matrizes pré-alocadas em memória RAM (estruturas de dados primitive arrays, sem garbage collection). Se aprovado, atualiza o saldo provisório instantaneamente.

FIX Session Manager (Roteador PUMA): Gerencia ativamente a sessão física com a B3 (sessões de envio ordenadas, controle estrito da Tag 34 e retransmissões). Ele recebe a ordem já validada pelo risco através de passagem de ponteiro em memória e a serializa no padrão FIX exigido pela B3, jogando-a diretamente na placa de rede (usando técnicas como Kernel Bypass / Solarflare OpenOnload).

O Warm / Cold Path (Processamento Assíncrono)
Tudo o que não impede a ordem de ir para a bolsa é jogado para fora do Hot Path imediatamente. O elo entre os dois mundos é um barramento de eventos de altíssima vazão e zero-copy (como o LMAX Disruptor Ring Buffer ou tópicos de memória compartilhada).

Journaler / Ledger: A persistência da ordem para fins regulatórios e recuperação de desastres é feita de forma assíncrona. O Journaler lê os eventos do Ring Buffer e os escreve em disco sequencialmente (append-only log), garantindo a durabilidade sem bloquear a thread principal de negociação.

Real-Time Pricer & MTM: O cálculo de emolumentos B3, taxas de corretagem e a Marcação a Mercado (MtM) da carteira rodam aqui. Eles consomem os Execution Reports gerados quando a B3 executa uma ordem e atualizam assincronamente a visão de risco global. Se uma conta estourar o limite aqui, o Pricer envia um evento para o Pre-Trade Risk Engine no Hot Path trancar novas ordens daquele player na memória.

Drop Copy Route: Módulo assíncrono encarregado de ler as execuções da bolsa e publicar em sistemas de mensageria de alta vazão (como Apache Kafka ou RabbitMQ) para atualizar telas de clientes, sistemas de Compliance de segundo nível e plataformas de EMS externas.

O Pós-Trade (Cold Path Retardado)
Allocation & MTR Engine: Funciona em janelas Intraday e no Post-Market. Ele consolida os negócios efetuados pelas contas Master ao longo do pregão e aplica as regras de repasse (Give-up) e alocação final por investidor participante.

Sinacor / Custódia: Ao fim do dia, este módulo gera e transmite os arquivos normatizados e layouts específicos de liquidação para os custodiantes e para o Sinacor, operando de forma totalmente isolada da infraestrutura de negociação em tempo real.