using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Inventoryzing.Agent.Runtime.Printing;

internal sealed partial class WindowsPrintSpoolerNative : IWindowsPrintSpoolerNative
{
    private const uint ErrorFileNotFound = 2;
    private const uint ErrorInvalidParameter = 87;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint JobControlResume = 2;
    private const uint PrinterChangeFilter = 0x0000FF0A;
    private const uint PrinterNotifyInfoDiscarded = 0x00000001;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private static readonly TimeSpan CancellationPollInterval = TimeSpan.FromMilliseconds(100);

    public IWindowsPrinterHandle OpenPrinter(
        string printerName,
        WindowsPrinterAccess desiredAccess)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        var defaults = new PrinterDefaults { DesiredAccess = (uint)desiredAccess };
        if (NativeMethods.OpenPrinter(printerName, out var handle, ref defaults))
        {
            return handle;
        }

        var error = (uint)Marshal.GetLastPInvokeError();
        handle?.Dispose();
        throw Error("OpenPrinterW", error);
    }

    public IWindowsPrinterChangeHandle Subscribe(IWindowsPrinterHandle printerHandle)
    {
        var handle = GetPrinterHandle(printerHandle);
        using var options = NotificationOptionsBuffer.CreateSubscription();
        var changeHandle = NativeMethods.FindFirstPrinterChangeNotification(
            handle,
            PrinterChangeFilter,
            0,
            options.Pointer);
        if (!changeHandle.IsInvalid)
        {
            return changeHandle;
        }

        var error = (uint)Marshal.GetLastPInvokeError();
        changeHandle.Dispose();
        throw Error("FindFirstPrinterChangeNotification", error);
    }

    public WindowsSpoolerQueueSnapshot Snapshot(IWindowsPrinterHandle printerHandle)
    {
        var handle = GetPrinterHandle(printerHandle);
        var printer = GetPrinterInfo(handle);
        var jobs = EnumerateJobs(handle, printer.JobCount);
        return new WindowsSpoolerQueueSnapshot(printer.Status, jobs);
    }

    public WindowsSpoolerJob? GetJob(IWindowsPrinterHandle printerHandle, uint jobId)
    {
        var handle = GetPrinterHandle(printerHandle);
        _ = NativeMethods.GetJob(handle, jobId, 2, 0, 0, out var needed);
        var error = (uint)Marshal.GetLastPInvokeError();
        if (IsMissingJob(error))
        {
            return null;
        }
        if (needed == 0 || error != ErrorInsufficientBuffer)
        {
            throw Error("GetJobW", error);
        }

        var buffer = Allocate(needed);
        try
        {
            if (!NativeMethods.GetJob(handle, jobId, 2, buffer, needed, out _))
            {
                error = (uint)Marshal.GetLastPInvokeError();
                if (IsMissingJob(error))
                {
                    return null;
                }

                throw Error("GetJobW", error);
            }

            return ReadJob(Marshal.PtrToStructure<JobInfo2>(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public WindowsSpoolerWaitOutcome WaitForChange(
        IWindowsPrinterChangeHandle changeHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var handle = GetChangeHandle(changeHandle);
        var startedAt = Stopwatch.GetTimestamp();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wait = WaitSlice(timeout, Stopwatch.GetElapsedTime(startedAt));
            var result = NativeMethods.WaitForSingleObject(handle, ToMilliseconds(wait));
            if (result == WaitObject0)
            {
                return WindowsSpoolerWaitOutcome.Signaled;
            }
            if (result == WaitFailed)
            {
                throw Error("WaitForSingleObject", (uint)Marshal.GetLastPInvokeError());
            }
            if (result != WaitTimeout)
            {
                throw new InvalidOperationException($"Unexpected Win32 wait result 0x{result:X8}.");
            }
        }
        while (timeout == Timeout.InfiniteTimeSpan || Stopwatch.GetElapsedTime(startedAt) < timeout);

        return WindowsSpoolerWaitOutcome.TimedOut;
    }

    public WindowsSpoolerNotification NextNotification(
        IWindowsPrinterChangeHandle changeHandle,
        bool refresh)
    {
        var handle = GetChangeHandle(changeHandle);
        using var options = refresh ? NotificationOptionsBuffer.CreateRefresh() : null;
        if (!NativeMethods.FindNextPrinterChangeNotification(
                handle,
                out var changes,
                options?.Pointer ?? 0,
                out nint info))
        {
            throw Error(
                "FindNextPrinterChangeNotification",
                (uint)Marshal.GetLastPInvokeError());
        }

        try
        {
            var discarded = info != 0 &&
                ((uint)Marshal.ReadInt32(info, sizeof(uint)) & PrinterNotifyInfoDiscarded) != 0;
            return new WindowsSpoolerNotification(changes, discarded);
        }
        finally
        {
            if (info != 0)
            {
                _ = NativeMethods.FreePrinterNotifyInfo(info);
            }
        }
    }

    public WindowsSpoolerResumeOutcome Resume(
        IWindowsPrinterHandle printerHandle,
        uint jobId)
    {
        var handle = GetPrinterHandle(printerHandle);
        if (NativeMethods.SetJob(handle, jobId, 0, 0, JobControlResume))
        {
            return WindowsSpoolerResumeOutcome.Succeeded;
        }

        var error = (uint)Marshal.GetLastPInvokeError();
        if (IsMissingJob(error))
        {
            return WindowsSpoolerResumeOutcome.JobMissing;
        }

        throw Error("SetJobW(JOB_CONTROL_RESUME)", error);
    }

    private static PrinterState GetPrinterInfo(SafePrinterHandle handle)
    {
        _ = NativeMethods.GetPrinter(handle, 2, 0, 0, out var needed);
        var error = (uint)Marshal.GetLastPInvokeError();
        if (needed == 0 || error != ErrorInsufficientBuffer)
        {
            throw Error("GetPrinterW", error);
        }

        var buffer = Allocate(needed);
        try
        {
            if (!NativeMethods.GetPrinter(handle, 2, buffer, needed, out _))
            {
                throw Error("GetPrinterW", (uint)Marshal.GetLastPInvokeError());
            }

            var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
            return new PrinterState(info.Status, info.JobCount);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<WindowsSpoolerJob> EnumerateJobs(
        SafePrinterHandle handle,
        uint jobCount)
    {
        if (jobCount == 0)
        {
            return Array.AsReadOnly(Array.Empty<WindowsSpoolerJob>());
        }

        _ = NativeMethods.EnumJobs(handle, 0, jobCount, 2, 0, 0, out var needed, out _);
        var error = (uint)Marshal.GetLastPInvokeError();
        if (needed == 0)
        {
            return Array.AsReadOnly(Array.Empty<WindowsSpoolerJob>());
        }
        if (error != ErrorInsufficientBuffer)
        {
            throw Error("EnumJobsW", error);
        }

        var buffer = Allocate(needed);
        try
        {
            if (!NativeMethods.EnumJobs(
                    handle,
                    0,
                    jobCount,
                    2,
                    buffer,
                    needed,
                    out _,
                    out var returned))
            {
                throw Error("EnumJobsW", (uint)Marshal.GetLastPInvokeError());
            }

            var jobs = new List<WindowsSpoolerJob>(checked((int)returned));
            var structureSize = Marshal.SizeOf<JobInfo2>();
            for (var index = 0; index < returned; index++)
            {
                var address = buffer + checked((int)index * structureSize);
                jobs.Add(ReadJob(Marshal.PtrToStructure<JobInfo2>(address)));
            }

            return jobs.AsReadOnly();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static WindowsSpoolerJob ReadJob(JobInfo2 info)
    {
        var documentName = Marshal.PtrToStringUni(info.DocumentName);
        if (string.IsNullOrWhiteSpace(documentName))
        {
            throw new InvalidDataException(
                $"Winspool job {info.JobId} did not provide a document name.");
        }

        DateTimeOffset submittedAt;
        try
        {
            submittedAt = new DateTimeOffset(
                info.Submitted.Year,
                info.Submitted.Month,
                info.Submitted.Day,
                info.Submitted.Hour,
                info.Submitted.Minute,
                info.Submitted.Second,
                info.Submitted.Milliseconds,
                TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"Winspool job {info.JobId} reported an invalid UTC submission time.",
                exception);
        }

        return new WindowsSpoolerJob(
            info.JobId,
            documentName,
            submittedAt,
            info.Status,
            Marshal.PtrToStringUni(info.StatusText),
            Marshal.PtrToStringUni(info.UserName),
            Marshal.PtrToStringUni(info.MachineName),
            info.TotalPages,
            info.PagesPrinted);
    }

    private static TimeSpan WaitSlice(TimeSpan timeout, TimeSpan elapsed)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return CancellationPollInterval;
        }

        var remaining = timeout - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining < CancellationPollInterval ? remaining : CancellationPollInterval;
    }

    private static uint ToMilliseconds(TimeSpan timeout) =>
        checked((uint)Math.Ceiling(timeout.TotalMilliseconds));

    private static nint Allocate(uint byteCount)
    {
        if (byteCount > int.MaxValue)
        {
            throw new InvalidDataException("Winspool requested an oversized native buffer.");
        }

        return Marshal.AllocHGlobal(checked((int)byteCount));
    }

    private static bool IsMissingJob(uint error) =>
        error is ErrorFileNotFound or ErrorInvalidParameter;

    private static Win32Exception Error(string operation, uint error) =>
        new(checked((int)error), $"{operation} failed with Win32 error {error}.");

    private static SafePrinterHandle GetPrinterHandle(IWindowsPrinterHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle as SafePrinterHandle ??
            throw new ArgumentException("The printer handle was not created by this native API.", nameof(handle));
    }

    private static SafePrinterChangeHandle GetChangeHandle(IWindowsPrinterChangeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle as SafePrinterChangeHandle ??
            throw new ArgumentException("The change handle was not created by this native API.", nameof(handle));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Winspool monitoring requires Windows.");
        }
    }

    private sealed class NotificationOptionsBuffer : IDisposable
    {
        private const ushort PrinterNotifyType = 0x00;
        private const ushort JobNotifyType = 0x01;
        private const ushort PrinterNotifyFieldStatus = 0x12;
        private const ushort JobNotifyFieldStatus = 0x0A;
        private const uint PrinterNotifyOptionsRefresh = 0x00000001;
        private nint printerFields;
        private nint jobFields;
        private nint types;
        private nint pointer;

        private NotificationOptionsBuffer(bool refresh)
        {
            try
            {
                if (refresh)
                {
                    pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PrinterNotifyOptions>());
                    Marshal.StructureToPtr(
                        new PrinterNotifyOptions(2, PrinterNotifyOptionsRefresh, 0, 0),
                        pointer,
                        fDeleteOld: false);
                    return;
                }

                printerFields = Marshal.AllocHGlobal(sizeof(ushort));
                Marshal.WriteInt16(printerFields, unchecked((short)PrinterNotifyFieldStatus));
                jobFields = Marshal.AllocHGlobal(sizeof(ushort));
                Marshal.WriteInt16(jobFields, unchecked((short)JobNotifyFieldStatus));

                var typeSize = Marshal.SizeOf<PrinterNotifyOptionsType>();
                types = Marshal.AllocHGlobal(checked(typeSize * 2));
                Marshal.StructureToPtr(
                    new PrinterNotifyOptionsType(PrinterNotifyType, printerFields),
                    types,
                    fDeleteOld: false);
                Marshal.StructureToPtr(
                    new PrinterNotifyOptionsType(JobNotifyType, jobFields),
                    types + typeSize,
                    fDeleteOld: false);

                pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PrinterNotifyOptions>());
                Marshal.StructureToPtr(
                    new PrinterNotifyOptions(2, 0, 2, types),
                    pointer,
                    fDeleteOld: false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public nint Pointer => pointer;

        public static NotificationOptionsBuffer CreateSubscription() => new(refresh: false);

        public static NotificationOptionsBuffer CreateRefresh() => new(refresh: true);

        public void Dispose()
        {
            Free(ref pointer);
            Free(ref types);
            Free(ref jobFields);
            Free(ref printerFields);
        }

        private static void Free(ref nint address)
        {
            if (address != 0)
            {
                Marshal.FreeHGlobal(address);
                address = 0;
            }
        }
    }

    private sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid, IWindowsPrinterHandle
    {
        public SafePrinterHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.ClosePrinter(handle);
    }

    private sealed class SafePrinterChangeHandle : SafeHandleMinusOneIsInvalid, IWindowsPrinterChangeHandle
    {
        public SafePrinterChangeHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() =>
            NativeMethods.FindClosePrinterChangeNotification(handle);
    }

    private readonly record struct PrinterState(uint Status, uint JobCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults
    {
        public nint DataType;
        public nint DeviceMode;
        public uint DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PrinterNotifyOptions(uint version, uint flags, uint count, nint types)
    {
        public readonly uint Version = version;
        public readonly uint Flags = flags;
        public readonly uint Count = count;
        public readonly nint Types = types;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PrinterNotifyOptionsType(ushort type, nint fields)
    {
        public readonly ushort Type = type;
        public readonly ushort Reserved0;
        public readonly uint Reserved1;
        public readonly uint Reserved2;
        public readonly uint Count = 1;
        public readonly nint Fields = fields;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public nint ServerName;
        public nint PrinterName;
        public nint ShareName;
        public nint PortName;
        public nint DriverName;
        public nint Comment;
        public nint Location;
        public nint DeviceMode;
        public nint SeparatorFile;
        public nint PrintProcessor;
        public nint DataType;
        public nint Parameters;
        public nint SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint JobCount;
        public uint AveragePagesPerMinute;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo2
    {
        public uint JobId;
        public nint PrinterName;
        public nint MachineName;
        public nint UserName;
        public nint DocumentName;
        public nint NotifyName;
        public nint DataType;
        public nint PrintProcessor;
        public nint Parameters;
        public nint DriverName;
        public nint DeviceMode;
        public nint StatusText;
        public nint SecurityDescriptor;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint StartTime;
        public uint UntilTime;
        public uint TotalPages;
        public uint Size;
        public NativeSystemTime Submitted;
        public uint Time;
        public uint PagesPrinted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    private static partial class NativeMethods
    {
        [LibraryImport(
            "winspool.drv",
            EntryPoint = "OpenPrinterW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenPrinter(
            string printerName,
            out SafePrinterHandle printerHandle,
            ref PrinterDefaults defaults);

        [LibraryImport("winspool.drv", SetLastError = true)]
        internal static partial SafePrinterChangeHandle FindFirstPrinterChangeNotification(
            SafePrinterHandle printerHandle,
            uint filter,
            uint options,
            nint printerNotifyOptions);

        [LibraryImport("winspool.drv", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool FindNextPrinterChangeNotification(
            SafePrinterChangeHandle changeHandle,
            out uint change,
            nint printerNotifyOptions,
            out nint printerNotifyInfo);

        [LibraryImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetPrinter(
            SafePrinterHandle printerHandle,
            uint level,
            nint printer,
            uint bufferSize,
            out uint needed);

        [LibraryImport("winspool.drv", EntryPoint = "EnumJobsW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EnumJobs(
            SafePrinterHandle printerHandle,
            uint firstJob,
            uint numberOfJobs,
            uint level,
            nint jobs,
            uint bufferSize,
            out uint needed,
            out uint returned);

        [LibraryImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetJob(
            SafePrinterHandle printerHandle,
            uint jobId,
            uint level,
            nint job,
            uint bufferSize,
            out uint needed);

        [LibraryImport("winspool.drv", EntryPoint = "SetJobW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetJob(
            SafePrinterHandle printerHandle,
            uint jobId,
            uint level,
            nint job,
            uint command);

        [LibraryImport("winspool.drv")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ClosePrinter(nint printerHandle);

        [LibraryImport("winspool.drv")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool FindClosePrinterChangeNotification(nint changeHandle);

        [LibraryImport("winspool.drv")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool FreePrinterNotifyInfo(nint printerNotifyInfo);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(
            SafePrinterChangeHandle handle,
            uint milliseconds);
    }
}
