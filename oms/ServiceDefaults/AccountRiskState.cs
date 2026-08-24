using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ServiceDefaults;

[StructLayout(LayoutKind.Explicit, Size = 64)] // Ocupa exatamente 1 Cache Line
public struct AccountRiskState
{
    [FieldOffset(0)] public long AccountId;
    [FieldOffset(8)] public double AvailableCash;         // Saldo D-0
    [FieldOffset(16)] public double BlockedCash;           // Margem de ordens abertas
    [FieldOffset(24)] public int MaxOrderQuantity;         // Fat-finger limite
    [FieldOffset(28)] public int TotalTradedQuantityToday;
    [FieldOffset(32)] public bool IsBlocked;               // Status da conta (true = kill switch)
}
