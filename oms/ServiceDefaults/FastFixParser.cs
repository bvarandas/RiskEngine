using ServiceDefaults.events;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace ServiceDefaults;

public static class FastFixParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void ParseToStruct(ReadOnlySpan<byte> fixMsg, ref OrderEvent order)
    {
        // Parse sem conversão para string. Varre o span procurando as tags FIX chave
        // 37=OrderId, 11=ClOrdID, 44=Price, 38=OrderQty, 55=Symbol

        int position = 0;
        while (position < fixMsg.Length)
        {
            int equalIdx = fixMsg.Slice(position).IndexOf((byte)'=');
            if (equalIdx < 0) break;

            int tag = ParseIntFast(fixMsg.Slice(position, equalIdx));
            int valueStart = position + equalIdx + 1;

            int sohIdx = fixMsg.Slice(valueStart).IndexOf((byte)0x01);
            int valueLength = (sohIdx >= 0) ? sohIdx : fixMsg.Length - valueStart;

            ReadOnlySpan<byte> valueSpan = fixMsg.Slice(valueStart, valueLength);

            switch (tag)
            {
                case 37: // OrderId
                case 11: // ClOrdID
                    order.OrderId = ParseLongFast(valueSpan);
                    break;
                case 44: // Price
                    order.Price = ParseDoubleFast(valueSpan);
                    break;
                case 38: // Quantity
                    order.Quantity = ParseIntFast(valueSpan);
                    break;
                case 54: // Side (1=Buy, 2=Sell)
                    order.Side = (byte)(valueSpan[0] - '0');
                    break;
                case 55: // Symbol
                    unsafe
                    {
                        fixed (byte* pSymbol = order.Symbol)
                        {
                            int copyLength = Math.Min(valueSpan.Length, 11);
                            valueSpan.Slice(0, copyLength).CopyTo(new Span<byte>(pSymbol, copyLength));
                            pSymbol[copyLength] = 0; // Null terminator
                        }
                    }
                    break;
            }

            position = valueStart + valueLength + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParseIntFast(ReadOnlySpan<byte> span)
    {
        int result = 0;
        for (int i = 0; i < span.Length; i++)
        {
            result = result * 10 + (span[i] - '0');
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParseLongFast(ReadOnlySpan<byte> span)
    {
        long result = 0;
        for (int i = 0; i < span.Length; i++)
        {
            result = result * 10 + (span[i] - '0');
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ParseDoubleFast(ReadOnlySpan<byte> span)
    {
        // Versão rápida sem boxing/allocations do Double.Parse
        System.Buffers.Text.Utf8Parser.TryParse(span, out double value, out _);
        return value;
    }
}
