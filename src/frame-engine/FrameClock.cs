using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ovrix.Engine;

/// <summary>Uses an opt-in high-resolution Windows timer without changing the system timer period.</summary>
internal sealed class FrameClock : IDisposable
{
    private readonly WaitHandle? timer;
    private readonly WaitHandle[]? handles;

    public FrameClock(CancellationToken token)
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x001F0003);
        if (handle.IsInvalid) { handle.Dispose(); return; }
        timer = new TimerHandle(handle);
        handles = [timer, token.WaitHandle];
    }

    public void WaitUntil(long target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), target).Ticks;
        if (remaining <= 0) return;
        if (timer is not null)
        {
            long delay = -remaining;
            if (SetWaitableTimer(timer.SafeWaitHandle, ref delay, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                if (WaitHandle.WaitAny(handles!) == 1) token.ThrowIfCancellationRequested();
                return;
            }
        }
        while (Stopwatch.GetTimestamp() < target)
        {
            token.ThrowIfCancellationRequested();
            Thread.SpinWait(40);
        }
    }

    public void Dispose() => timer?.Dispose();

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle handle, ref long dueTime, int period,
        IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
