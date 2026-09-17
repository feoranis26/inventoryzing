using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Inventoryzing.Agent.Brother;

public sealed partial class BrotherBpacStaDispatcher : IAsyncDisposable
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint MessageQueueOffset = 1;
    private const uint PeekMessageRemove = 0x0001;
    private const uint QueueStatusAllInput = 0x04FF;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitOptionInputAvailable = 0x0004;
    private readonly ConcurrentQueue<IWorkItem> workItems = new();
    private readonly AutoResetEvent workAvailable = new(initialState: false);
    private readonly TaskCompletionSource started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private int stopping;
    private int disposed;

    public BrotherBpacStaDispatcher(string threadName = "Inventoryzing Brother b-PAC STA")
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Brother b-PAC requires a Windows STA thread.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async ValueTask<T> InvokeAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        var item = new WorkItem<T>(action, cancellationToken);
        workItems.Enqueue(item);
        workAvailable.Set();
        return await item.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref stopping, 1);
        workAvailable.Set();
        if (Environment.CurrentManagedThreadId != thread.ManagedThreadId)
        {
            await Task.Run(thread.Join).ConfigureAwait(false);
            workAvailable.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private unsafe void Run()
    {
        try
        {
            started.TrySetResult();
            while (Volatile.Read(ref stopping) == 0)
            {
                DrainWork();
                PumpMessages();
                if (Volatile.Read(ref stopping) != 0)
                {
                    break;
                }

                var workHandle = workAvailable.SafeWaitHandle.DangerousGetHandle();
                var result = NativeMethods.MsgWaitForMultipleObjectsEx(
                    1,
                    &workHandle,
                    Infinite,
                    QueueStatusAllInput,
                    WaitOptionInputAvailable);
                if (result == WaitFailed)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                if (result is not WaitObject0 and not WaitObject0 + MessageQueueOffset)
                {
                    throw new InvalidOperationException(
                        $"Unexpected STA message wait result 0x{result:X8}.");
                }
            }
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
            FailPending(exception);
            return;
        }

        FailPending(new ObjectDisposedException(nameof(BrotherBpacStaDispatcher)));
    }

    private void DrainWork()
    {
        while (Volatile.Read(ref stopping) == 0 && workItems.TryDequeue(out var item))
        {
            item.Execute();
            PumpMessages();
        }
    }

    private static void PumpMessages()
    {
        while (NativeMethods.PeekMessage(out var message, 0, 0, 0, PeekMessageRemove))
        {
            _ = NativeMethods.TranslateMessage(in message);
            _ = NativeMethods.DispatchMessage(in message);
        }
    }

    private void FailPending(Exception exception)
    {
        while (workItems.TryDequeue(out var item))
        {
            item.Fail(exception);
        }
    }

    private interface IWorkItem
    {
        void Execute();

        void Fail(Exception exception);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> action;
        private readonly TaskCompletionSource<T> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration cancellationRegistration;

        public WorkItem(Func<T> action, CancellationToken cancellationToken)
        {
            this.action = action;
            cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
        }

        public Task<T> Task => completion.Task;

        public void Execute()
        {
            cancellationRegistration.Dispose();
            if (completion.Task.IsCompleted)
            {
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception)
        {
            cancellationRegistration.Dispose();
            completion.TrySetException(exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMessage(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter,
        uint time,
        Point point,
        uint privateValue)
    {
        public readonly nint Window = window;
        public readonly uint Message = message;
        public readonly nuint WordParameter = wordParameter;
        public readonly nint LongParameter = longParameter;
        public readonly uint Time = time;
        public readonly Point Point = point;
        public readonly uint Private = privateValue;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static unsafe partial uint MsgWaitForMultipleObjectsEx(
            uint count,
            nint* handles,
            uint milliseconds,
            uint wakeMask,
            uint flags);

        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PeekMessage(
            out NativeMessage message,
            nint window,
            uint minimumMessage,
            uint maximumMessage,
            uint removeMessage);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TranslateMessage(in NativeMessage message);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static partial nint DispatchMessage(in NativeMessage message);
    }
}