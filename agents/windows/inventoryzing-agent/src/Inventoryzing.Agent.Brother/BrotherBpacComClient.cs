using System.Globalization;
using System.Runtime.InteropServices;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Brother;

public sealed class BrotherBpacComClient : IBrotherBpacClient, IBrotherBpacPrintEventClient
{
    internal const string DocumentProgId = "bpac.Document";
    internal const string PrinterProgId = "bpac.Printer";

    private readonly Func<string, object> comFactory;
    private object? document;
    private object? printer;
    private object? documentPrinter;
    private bool documentOpened;
    private bool disposed;
    private int latestPrinterErrorCode;
    private string? latestPrinterErrorMessage;
    private BrotherBpacPrintEventSubscription? printEventSubscription;

    public BrotherBpacComClient()
        : this(CreateComObject)
    {
    }

    internal BrotherBpacComClient(Func<string, object> comFactory)
    {
        this.comFactory = comFactory ?? throw new ArgumentNullException(nameof(comFactory));
    }

    public int DocumentErrorCode
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (document is null)
            {
                return 0;
            }

            dynamic bpacDocument = document;
            return Convert.ToInt32(bpacDocument.ErrorCode, CultureInfo.InvariantCulture);
        }
    }

    public int PrinterErrorCode
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RefreshDocumentPrinterError();
            return latestPrinterErrorCode;
        }
    }

    public string? PrinterErrorMessage
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RefreshDocumentPrinterError();
            return latestPrinterErrorMessage;
        }
    }

    public IReadOnlyList<BrotherBpacPrinterDiscovery> DiscoverPrinters()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        dynamic bpacPrinter = Printer;
        var names = ToStrings(bpacPrinter.GetInstalledPrinters());
        var selectedPrinterName = Convert.ToString(
            bpacPrinter.Name,
            CultureInfo.InvariantCulture);
        List<BrotherBpacPrinterDiscovery> discoveries = [];
        foreach (var name in names)
        {
            var isOnline = Convert.ToBoolean(
                bpacPrinter.IsPrinterOnline(name),
                CultureInfo.InvariantCulture);
            var isSelected = string.Equals(name, selectedPrinterName, StringComparison.Ordinal);
            string[] supportedMediaNames = isSelected
                ? ToStrings(bpacPrinter.GetSupportedMediaNames())
                : [];
            int[] supportedMediaIds = isSelected
                ? ToIntegers(bpacPrinter.GetSupportedMediaIds())
                : [];
            string? loadedMediaName = null;
            int? loadedMediaId = null;
            if (isOnline && isSelected)
            {
                loadedMediaName = Convert.ToString(
                    bpacPrinter.GetMediaName(),
                    CultureInfo.InvariantCulture);
                loadedMediaId = Convert.ToInt32(
                    bpacPrinter.GetMediaId(),
                    CultureInfo.InvariantCulture);
            }

            latestPrinterErrorCode = Convert.ToInt32(
                bpacPrinter.ErrorCode,
                CultureInfo.InvariantCulture);
            latestPrinterErrorMessage = Convert.ToString(
                bpacPrinter.ErrorString,
                CultureInfo.InvariantCulture);
            discoveries.Add(new BrotherBpacPrinterDiscovery(
                name,
                isOnline,
                isSelected,
                supportedMediaNames,
                supportedMediaIds,
                loadedMediaName,
                loadedMediaId,
                latestPrinterErrorCode,
                latestPrinterErrorMessage));
        }

        return discoveries.AsReadOnly();
    }

    public BrotherBpacPrinterAvailability GetPrinterAvailability(string printerName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);

        dynamic bpacPrinter = Printer;
        var isSupported = Convert.ToBoolean(
            bpacPrinter.IsPrinterSupported(printerName),
            CultureInfo.InvariantCulture);
        var isOnline = isSupported && Convert.ToBoolean(
            bpacPrinter.IsPrinterOnline(printerName),
            CultureInfo.InvariantCulture);
        latestPrinterErrorCode = Convert.ToInt32(
            bpacPrinter.ErrorCode,
            CultureInfo.InvariantCulture);
        latestPrinterErrorMessage = Convert.ToString(
            bpacPrinter.ErrorString,
            CultureInfo.InvariantCulture);
        return new BrotherBpacPrinterAvailability(
            isSupported,
            isOnline,
            latestPrinterErrorCode,
            latestPrinterErrorMessage);
    }

    public BrotherBpacPrinterStatus GetSelectedPrinterStatus(BrotherMediaSelection media)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(media);

        dynamic bpacPrinter = OpenDocumentPrinter;
        var printerName = Convert.ToString(
            bpacPrinter.Name,
            CultureInfo.InvariantCulture);
        var isSupported = !string.IsNullOrWhiteSpace(printerName) && Convert.ToBoolean(
            bpacPrinter.IsPrinterSupported(printerName),
            CultureInfo.InvariantCulture);
        var isOnline = isSupported && Convert.ToBoolean(
            bpacPrinter.IsPrinterOnline(printerName),
            CultureInfo.InvariantCulture);
        var isMediaSupported = isSupported && (media.Name is not null
            ? Convert.ToBoolean(
                bpacPrinter.IsMediaNameSupported(media.Name),
                CultureInfo.InvariantCulture)
            : Convert.ToBoolean(
                bpacPrinter.IsMediaIdSupported(media.Id!.Value),
                CultureInfo.InvariantCulture));

        string? loadedMediaName = null;
        int? loadedMediaId = null;
        if (isOnline)
        {
            loadedMediaName = Convert.ToString(
                bpacPrinter.GetMediaName(),
                CultureInfo.InvariantCulture);
            loadedMediaId = Convert.ToInt32(
                bpacPrinter.GetMediaId(),
                CultureInfo.InvariantCulture);
        }

        latestPrinterErrorCode = Convert.ToInt32(
            bpacPrinter.ErrorCode,
            CultureInfo.InvariantCulture);
        latestPrinterErrorMessage = Convert.ToString(
            bpacPrinter.ErrorString,
            CultureInfo.InvariantCulture);
        return new BrotherBpacPrinterStatus(
            isSupported,
            isOnline,
            isMediaSupported,
            loadedMediaName,
            loadedMediaId,
            latestPrinterErrorCode,
            latestPrinterErrorMessage);
    }

    public bool Open(string templatePath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        if (documentOpened)
        {
            throw new InvalidOperationException("A b-PAC document is already open.");
        }

        dynamic bpacDocument = Document;
        documentOpened = Convert.ToBoolean(
            bpacDocument.Open(templatePath),
            CultureInfo.InvariantCulture);
        return documentOpened;
    }

    public bool SetPrinter(string printerName, bool fitPage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        dynamic bpacDocument = OpenDocument;
        return Convert.ToBoolean(
            bpacDocument.SetPrinter(printerName, fitPage),
            CultureInfo.InvariantCulture);
    }

    public bool SetMedia(BrotherMediaSelection media, bool fitPage)
    {
        ArgumentNullException.ThrowIfNull(media);
        dynamic bpacDocument = OpenDocument;
        return media.Name is not null
            ? Convert.ToBoolean(
                bpacDocument.SetMediaByName(media.Name, fitPage),
                CultureInfo.InvariantCulture)
            : Convert.ToBoolean(
                bpacDocument.SetMediaById(media.Id!.Value, fitPage),
                CultureInfo.InvariantCulture);
    }

    public bool SetObjectText(string objectName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentNullException.ThrowIfNull(value);
        dynamic bpacDocument = OpenDocument;
        object? templateObject = bpacDocument.GetObject(objectName);
        if (templateObject is null)
        {
            return false;
        }

        try
        {
            dynamic bpacObject = templateObject;
            bpacObject.Text = value;
            return true;
        }
        finally
        {
            ReleaseComObject(templateObject);
        }
    }

    public bool SetObjectImage(string objectName, string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        dynamic bpacDocument = OpenDocument;
        object? templateObject = bpacDocument.GetObject(objectName);
        if (templateObject is null)
        {
            return false;
        }

        try
        {
            dynamic bpacObject = templateObject;
            return Convert.ToBoolean(
                bpacObject.SetData(0, imagePath, 4),
                CultureInfo.InvariantCulture);
        }
        finally
        {
            ReleaseComObject(templateObject);
        }
    }

    public bool StartPrint(string documentName, int options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        dynamic bpacDocument = OpenDocument;
        return Convert.ToBoolean(
            bpacDocument.StartPrint(documentName, options),
            CultureInfo.InvariantCulture);
    }

    public bool PrintOut(int copies, int options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(copies);
        dynamic bpacDocument = OpenDocument;
        return Convert.ToBoolean(
            bpacDocument.PrintOut(copies, options),
            CultureInfo.InvariantCulture);
    }

    public bool EndPrint()
    {
        dynamic bpacDocument = OpenDocument;
        return Convert.ToBoolean(bpacDocument.EndPrint(), CultureInfo.InvariantCulture);
    }

    public IPrinterMonitorSubscription ArmPrintedEvents(
        TimeProvider? timeProvider = null,
        Func<Guid>? createId = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (printEventSubscription is not null)
        {
            throw new InvalidOperationException("b-PAC print events are already armed.");
        }

        var subscription = new BrotherBpacPrintEventSubscription(
            this,
            timeProvider ?? TimeProvider.System,
            createId ?? Guid.NewGuid);
        dynamic bpacDocument = Document;
        if (!Convert.ToBoolean(
            bpacDocument.SetPrintedCallback(subscription.Callback),
            CultureInfo.InvariantCulture))
        {
            subscription.Complete();
            throw new InvalidOperationException("b-PAC rejected the print event callback.");
        }

        printEventSubscription = subscription;
        return subscription;
    }

    public bool Close()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!documentOpened)
        {
            return true;
        }

        try
        {
            dynamic bpacDocument = Document;
            return Convert.ToBoolean(bpacDocument.Close(), CultureInfo.InvariantCulture);
        }
        finally
        {
            ReleaseComObject(documentPrinter);
            documentPrinter = null;
            documentOpened = false;
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
            ClearPrintEventSubscription(printEventSubscription);
            if (documentOpened)
            {
                try
                {
                    _ = Close();
                }
                catch (COMException)
                {
                }
            }
        }
        finally
        {
            ReleaseComObject(documentPrinter);
            ReleaseComObject(document);
            ReleaseComObject(printer);
            documentPrinter = null;
            document = null;
            printer = null;
            disposed = true;
        }
    }

    internal void ClearPrintEventSubscription(BrotherBpacPrintEventSubscription? subscription)
    {
        if (subscription is null || !ReferenceEquals(printEventSubscription, subscription))
        {
            return;
        }

        printEventSubscription = null;
        try
        {
            if (!disposed && document is not null)
            {
                dynamic bpacDocument = document;
                _ = bpacDocument.SetPrintedCallback(null);
            }
        }
        finally
        {
            subscription.Complete();
        }
    }

    private object Document =>
        document ??= comFactory(DocumentProgId) ??
            throw new InvalidOperationException("b-PAC document activation returned null.");

    private object Printer =>
        printer ??= comFactory(PrinterProgId) ??
            throw new InvalidOperationException("b-PAC printer activation returned null.");

    private object OpenDocument
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!documentOpened)
            {
                throw new InvalidOperationException("No b-PAC document is open.");
            }

            return Document;
        }
    }

    private object OpenDocumentPrinter
    {
        get
        {
            dynamic bpacDocument = OpenDocument;
            return documentPrinter ??= bpacDocument.Printer ??
                throw new InvalidOperationException("The open b-PAC document returned no printer.");
        }
    }

    private static object CreateComObject(string progId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Brother b-PAC is available only on Windows.");
        }

        var type = Type.GetTypeFromProgID(progId, throwOnError: false) ??
            throw new InvalidOperationException($"Brother b-PAC ProgID '{progId}' is not registered.");
        return Activator.CreateInstance(type) ??
            throw new InvalidOperationException($"Brother b-PAC ProgID '{progId}' returned no object.");
    }

    private void RefreshDocumentPrinterError()
    {
        if (!documentOpened)
        {
            return;
        }

        dynamic bpacPrinter = OpenDocumentPrinter;
        latestPrinterErrorCode = Convert.ToInt32(
            bpacPrinter.ErrorCode,
            CultureInfo.InvariantCulture);
        latestPrinterErrorMessage = Convert.ToString(
            bpacPrinter.ErrorString,
            CultureInfo.InvariantCulture);
    }

    private static void ReleaseComObject(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static string[] ToStrings(object? values)
    {
        if (values is not Array array)
        {
            return [];
        }

        return [.. array
            .Cast<object?>()
            .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()];
    }

    private static int[] ToIntegers(object? values)
    {
        if (values is not Array array)
        {
            return [];
        }

        return [.. array
            .Cast<object?>()
            .Select(value => Convert.ToInt32(value, CultureInfo.InvariantCulture))];
    }
}