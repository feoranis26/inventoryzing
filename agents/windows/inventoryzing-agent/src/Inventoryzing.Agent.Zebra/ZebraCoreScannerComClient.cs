using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Inventoryzing.Agent.Zebra;

public sealed class ZebraCoreScannerComClient : IZebraCoreScannerClient
{
    internal const int RegisterForEventsOpcode = 1001;
    internal const int SetActionOpcode = 6000;
    internal const int MaximumDevices = 255;

    private const string InteropEnvironmentVariable = "ZEBRA_CORE_SCANNER_INTEROP_PATH";
    private readonly Func<object> coreScannerFactory;
    private object? coreScanner;
    private EventInfo? barcodeEvent;
    private EventInfo? pnpEvent;
    private Delegate? barcodeHandler;
    private Delegate? pnpHandler;
    private bool disposed;

    public ZebraCoreScannerComClient(string? interopAssemblyPath = null)
    {
        coreScannerFactory = () => CreateCoreScanner(interopAssemblyPath);
    }

    internal ZebraCoreScannerComClient(Func<object> coreScannerFactory)
    {
        this.coreScannerFactory = coreScannerFactory ??
            throw new ArgumentNullException(nameof(coreScannerFactory));
    }

    public event EventHandler<ZebraCoreScannerEventArgs>? BarcodeReceived;

    public event EventHandler<ZebraCoreScannerEventArgs>? PnpChanged;

    public void Open(IReadOnlyCollection<short> scannerTypes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(scannerTypes);
        if (scannerTypes.Count == 0)
        {
            throw new ArgumentException("At least one scanner type is required.", nameof(scannerTypes));
        }

        if (coreScanner is not null)
        {
            throw new InvalidOperationException("CoreScanner is already open.");
        }

        coreScanner = coreScannerFactory();
        try
        {
            AttachEventHandlers();
            object?[] arguments = [0, scannerTypes.ToArray(), checked((short)scannerTypes.Count), -1];
            Invoke("Open", arguments);
            EnsureSuccess("Open", arguments[3]);
        }
        catch
        {
            ReleaseCoreScanner();
            throw;
        }
    }

    public string GetScanners()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureOpen();

        object?[] arguments = [(short)0, new int[MaximumDevices], string.Empty, -1];
        Invoke("GetScanners", arguments);
        EnsureSuccess("GetScanners", arguments[3]);
        return arguments[2] as string ?? string.Empty;
    }

    public void RegisterForEvents(IReadOnlyCollection<short> eventIds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(eventIds);
        EnsureOpen();
        if (eventIds.Count == 0)
        {
            throw new ArgumentException("At least one event ID is required.", nameof(eventIds));
        }

        var inXml = new XElement(
            "inArgs",
            new XElement(
                "cmdArgs",
                new XElement("arg-int", eventIds.Count),
                new XElement("arg-int", string.Join(',', eventIds))))
            .ToString(SaveOptions.DisableFormatting);
        object?[] arguments = [RegisterForEventsOpcode, inXml, string.Empty, -1];
        Invoke("ExecCommand", arguments);
        EnsureSuccess("REGISTER_FOR_EVENTS", arguments[3]);
    }

    public string GetDeviceTopology()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureOpen();
        object?[] arguments = [5006, "<inArgs></inArgs>", string.Empty, -1];
        Invoke("ExecCommand", arguments);
        EnsureSuccess("GET_DEVICE_TOPOLOGY", arguments[3]);
        return arguments[2] as string ?? string.Empty;
    }

    public void ExecuteAction(int scannerId, int action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureOpen();
        if (scannerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scannerId));
        }

        var inXml = new XElement(
            "inArgs",
            new XElement("scannerID", scannerId),
            new XElement("cmdArgs", new XElement("arg-int", action)))
            .ToString(SaveOptions.DisableFormatting);
        object?[] arguments = [SetActionOpcode, inXml, string.Empty, -1];
        Invoke("ExecCommand", arguments);
        EnsureSuccess("SET_ACTION", arguments[3]);
    }

    public void Close()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (coreScanner is null)
        {
            return;
        }

        try
        {
            object?[] arguments = [0, -1];
            Invoke("Close", arguments);
            EnsureSuccess("Close", arguments[1]);
        }
        finally
        {
            ReleaseCoreScanner();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            Close();
        }
        finally
        {
            disposed = true;
        }
    }

    private static object CreateCoreScanner(string? configuredPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Zebra CoreScanner is available only on Windows.");
        }

        var interopPath = ResolveInteropPath(configuredPath);
        var assembly = Assembly.LoadFrom(interopPath);
        var coreScannerType = assembly.GetType("Interop.CoreScanner.CCoreScannerClass", throwOnError: false) ??
            assembly.GetType("CoreScanner.CCoreScannerClass", throwOnError: false) ??
            throw new InvalidOperationException(
                $"'{interopPath}' does not expose a supported CoreScanner class.");
        return Activator.CreateInstance(coreScannerType) ??
            throw new InvalidOperationException("CoreScanner COM activation returned no object.");
    }

    private static string ResolveInteropPath(string? configuredPath)
    {
        var explicitPath = configuredPath ?? Environment.GetEnvironmentVariable(InteropEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            return File.Exists(fullPath)
                ? fullPath
                : throw new FileNotFoundException("The configured Zebra CoreScanner interop assembly was not found.", fullPath);
        }

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Zebra Technologies",
                "Barcode Scanners",
                "Common",
                "Interop.CoreScanner.dll"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Zebra Technologies",
                "Barcode Scanners",
                "Common",
                "Interop.CoreScanner.dll"),
        };
        return candidates.FirstOrDefault(File.Exists) ??
            throw new FileNotFoundException(
                $"Zebra CoreScanner interop was not found. Set {InteropEnvironmentVariable} to its full path.");
    }

    private void AttachEventHandlers()
    {
        var scannerType = coreScanner!.GetType();
        barcodeEvent = RequiredEvent(scannerType, "BarcodeEvent");
        pnpEvent = RequiredEvent(scannerType, "PNPEvent");
        barcodeHandler = CreateHandler(barcodeEvent, nameof(HandleBarcodeEvent));
        pnpHandler = CreateHandler(pnpEvent, nameof(HandlePnpEvent));
        barcodeEvent.AddEventHandler(coreScanner, barcodeHandler);
        try
        {
            pnpEvent.AddEventHandler(coreScanner, pnpHandler);
        }
        catch
        {
            barcodeEvent.RemoveEventHandler(coreScanner, barcodeHandler);
            throw;
        }
    }

    private void ReleaseCoreScanner()
    {
        var scanner = coreScanner;
        coreScanner = null;
        if (scanner is null)
        {
            return;
        }

        try
        {
            if (barcodeEvent is not null && barcodeHandler is not null)
            {
                barcodeEvent.RemoveEventHandler(scanner, barcodeHandler);
            }

            if (pnpEvent is not null && pnpHandler is not null)
            {
                pnpEvent.RemoveEventHandler(scanner, pnpHandler);
            }
        }
        finally
        {
            barcodeEvent = null;
            pnpEvent = null;
            barcodeHandler = null;
            pnpHandler = null;
            if (OperatingSystem.IsWindows() && Marshal.IsComObject(scanner))
            {
                Marshal.FinalReleaseComObject(scanner);
            }
        }
    }

    private void Invoke(string methodName, object?[] arguments)
    {
        var method = coreScanner!
            .GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public) ??
            throw new MissingMethodException(coreScanner.GetType().FullName, methodName);
        try
        {
            method.Invoke(coreScanner, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private Delegate CreateHandler(EventInfo eventInfo, string methodName)
    {
        var handlerType = eventInfo.EventHandlerType ??
            throw new MissingMemberException(eventInfo.DeclaringType?.FullName, eventInfo.Name);
        var method = GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(GetType().FullName, methodName);
        return Delegate.CreateDelegate(handlerType, this, method);
    }

    private static EventInfo RequiredEvent(Type scannerType, string eventName) =>
        scannerType.GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public) ??
        throw new MissingMemberException(scannerType.FullName, eventName);

    private static void EnsureSuccess(string operation, object? statusValue)
    {
        var status = Convert.ToInt32(statusValue, System.Globalization.CultureInfo.InvariantCulture);
        if (status != 0)
        {
            throw new InvalidOperationException($"CoreScanner {operation} failed with status {status}.");
        }
    }

    private void EnsureOpen()
    {
        if (coreScanner is null)
        {
            throw new InvalidOperationException("CoreScanner is not open.");
        }
    }

    private void HandleBarcodeEvent(short eventType, ref string scanData) =>
        BarcodeReceived?.Invoke(this, new ZebraCoreScannerEventArgs(eventType, scanData));

    private void HandlePnpEvent(short eventType, ref string pnpData) =>
        PnpChanged?.Invoke(this, new ZebraCoreScannerEventArgs(eventType, pnpData));
}
