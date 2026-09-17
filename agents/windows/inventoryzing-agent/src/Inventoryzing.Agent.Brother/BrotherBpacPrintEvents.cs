using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Brother;

internal sealed class BrotherBpacPrintEventSubscription : IPrinterMonitorSubscription
{
    private readonly BrotherBpacComClient owner;
    private readonly Channel<PrinterMonitorObservation> observations;
    private int disposed;

    public BrotherBpacPrintEventSubscription(
        BrotherBpacComClient owner,
        TimeProvider timeProvider,
        Func<Guid> createId)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(createId);
        observations = Channel.CreateUnbounded<PrinterMonitorObservation>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = false,
                SingleWriter = false,
            });
        Callback = new BrotherBpacPrintEventCallback(OnPrinted, timeProvider, createId);
    }

    public object Callback { get; }

    public ValueTask<PrinterMonitorObservation> WaitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        return observations.Reader.ReadAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            owner.ClearPrintEventSubscription(this);
        }
    }

    public void Complete() => observations.Writer.TryComplete();

    private void OnPrinted(PrinterMonitorObservation observation)
    {
        if (disposed == 0)
        {
            _ = observations.Writer.TryWrite(observation);
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDispatch)]
internal sealed class BrotherBpacPrintEventCallback(
    Action<PrinterMonitorObservation> observer,
    TimeProvider timeProvider,
    Func<Guid> createId)
{
    private readonly Action<PrinterMonitorObservation> observer =
        observer ?? throw new ArgumentNullException(nameof(observer));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly Func<Guid> createId = createId ?? throw new ArgumentNullException(nameof(createId));

    public void PrintedEvent(int status, object? value)
    {
        try
        {
            var observationId = createId();
            if (observationId != Guid.Empty)
            {
                observer(new PrinterMonitorObservation(
                    observationId,
                    Translate(status),
                    status,
                    FormatValue(value),
                    timeProvider.GetUtcNow()));
            }
        }
        catch
        {
        }
    }

    private static PrinterMonitorEventKind Translate(int status) => status switch
    {
        0 => PrinterMonitorEventKind.PagePrinted,
        1 => PrinterMonitorEventKind.Offline,
        2 => PrinterMonitorEventKind.Paused,
        3 => PrinterMonitorEventKind.Deleted,
        4 => PrinterMonitorEventKind.Error,
        5 => PrinterMonitorEventKind.PrinterNotFound,
        _ => PrinterMonitorEventKind.Unknown,
    };

    private static string? FormatValue(object? value)
    {
        if (value is null)
        {
            return null;
        }
        if (value is Array array)
        {
            return string.Join(
                ",",
                array.Cast<object?>().Select(item =>
                    Convert.ToString(item, CultureInfo.InvariantCulture)));
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}