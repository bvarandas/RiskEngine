using System.Runtime.InteropServices;
namespace ServiceDefaults.events;
// Estrutura de transferência entre o Hot Path 
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct RejectEvent
{
    public long OrderId;
    public fixed byte ClOrdId[20];
    public fixed byte Symbol[12];
    public byte Side;
    public byte ReasonCode; // ex: 1 = Limite Excedido, 2 = Conta bloqueada, 3 = Sem Custódia
}