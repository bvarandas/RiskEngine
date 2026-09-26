using System.Runtime.InteropServices;

namespace OrderIngestion;

internal static class ThreadAffinity
{
    public static void Set(int coreId)
    {
        if (coreId < 0 || coreId >= 64)
            throw new ArgumentOutOfRangeException(nameof(coreId), "Suporte simplificado limitado a 64 cores. Ajuste o bitmask se necessário.");

        ulong mask = 1UL << coreId;

        if (OperatingSystem.IsWindows())
        {
            nuint result = SetThreadAffinityMask(GetCurrentThread(), (nuint)mask);
            if (result == 0)
                throw new InvalidOperationException($"Falha ao setar afinidade no Windows. Erro: {Marshal.GetLastPInvokeError()}");
        }
        else if (OperatingSystem.IsLinux())
        {
            int result = sched_setaffinity(0, (IntPtr)sizeof(ulong), ref mask);
            if (result != 0)
                throw new InvalidOperationException($"Falha ao setar afinidade no Linux. Erro: {Marshal.GetLastPInvokeError()}");
        }
        else
        {
            throw new PlatformNotSupportedException("Afinidade de CPU implementada apenas para Windows e Linux.");
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern nuint SetThreadAffinityMask(IntPtr hThread, nuint dwThreadAffinityMask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);
}


