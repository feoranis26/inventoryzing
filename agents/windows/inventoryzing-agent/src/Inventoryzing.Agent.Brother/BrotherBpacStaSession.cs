using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Brother;

public sealed class BrotherBpacStaSession : IMonitoredPrinterSession
{
    private readonly BrotherPrinterProbe probe;
    private readonly Func<IBrotherBpacClient> clientFactory;
    private readonly BrotherBpacStaDispatcher dispatcher;
    private readonly TimeProvider timeProvider;
    private readonly Func<Guid> createId;
    private readonly TaskCompletionSource<IPrinterMonitorSubscription> monitorReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private IBrotherBpacClient? client;
    private IPrinterMonitorSubscription? monitor;
    private int disposed;
    private int submissionStarted;

    public BrotherBpacStaSession(
        Guid attemptId,
        string deviceId,
        string printerName,
        IEnumerable<BrotherBpacProfile> profiles,
        PrinterMonitorCompletionCapability completionCapability =
            PrinterMonitorCompletionCapability.Unknown)
        : this(
            attemptId,
            deviceId,
            printerName,
            profiles,
            completionCapability,
            static () => new BrotherBpacComClient(),
            new BrotherBpacStaDispatcher(),
            TimeProvider.System,
            Guid.NewGuid)
    {
    }

    internal BrotherBpacStaSession(
        Guid attemptId,
        string deviceId,
        string printerName,
        IEnumerable<BrotherBpacProfile> profiles,
        PrinterMonitorCompletionCapability completionCapability,
        Func<IBrotherBpacClient> clientFactory,
        BrotherBpacStaDispatcher dispatcher,
        TimeProvider timeProvider,
        Func<Guid> createId)
    {
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt ID cannot be empty.", nameof(attemptId));
        }
        if (!Enum.IsDefined(completionCapability))
        {
            throw new ArgumentOutOfRangeException(nameof(completionCapability));
        }

        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.createId = createId ?? throw new ArgumentNullException(nameof(createId));
        probe = new BrotherPrinterProbe(deviceId, printerName, profiles, clientFactory);
        CompletionCapability = completionCapability;
        AttemptId = attemptId;
    }

    public Guid AttemptId { get; }

    public PrinterMonitorCompletionCapability CompletionCapability { get; }

    public async ValueTask<PrinterCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await dispatcher.InvokeAsync(
            () =>
            {
                using var capabilityClient = CreateClient();
                return probe.GetCapabilities(capabilityClient, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrintSubmission> SubmitAsync(
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (request.RequestId != AttemptId)
        {
            throw new ArgumentException("The request does not belong to this printer session.", nameof(request));
        }
        if (Interlocked.Exchange(ref submissionStarted, 1) != 0)
        {
            throw new InvalidOperationException("A printer session may submit its attempt only once.");
        }
        return await dispatcher.InvokeAsync(
            () => probe.Submit(EnsureMonitoredClient, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrinterStatusObservation> ObserveStatusAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await dispatcher.InvokeAsync(
            () =>
            {
                using var statusClient = CreateClient();
                return probe.ObserveStatus(
                    statusClient,
                    CompletionCapability,
                    timeProvider,
                    createId,
                    cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrinterMonitorObservation> WaitForMonitorEventAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (CompletionCapability == PrinterMonitorCompletionCapability.Unsupported)
        {
            throw new NotSupportedException(
                "This printer model does not support b-PAC print monitor callbacks.");
        }
        var activeMonitor = await monitorReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await activeMonitor.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        monitorReady.TrySetException(new ObjectDisposedException(nameof(BrotherBpacStaSession)));
        try
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    monitor?.Dispose();
                    monitor = null;
                    client?.Dispose();
                    client = null;
                    return true;
                }).ConfigureAwait(false);
        }
        finally
        {
            await dispatcher.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private IBrotherBpacClient EnsureMonitoredClient()
    {
        client ??= CreateClient();
        if (CompletionCapability == PrinterMonitorCompletionCapability.Unsupported)
        {
            return client;
        }
        if (monitor is not null)
        {
            return client;
        }
        if (client is not IBrotherBpacPrintEventClient eventClient)
        {
            throw new InvalidOperationException(
                "The b-PAC client does not support print monitor callbacks.");
        }

        try
        {
            monitor = eventClient.ArmPrintedEvents(timeProvider, createId);
            monitorReady.TrySetResult(monitor);
            return client;
        }
        catch (Exception exception)
        {
            monitorReady.TrySetException(exception);
            client.Dispose();
            client = null;
            throw;
        }
    }

    private IBrotherBpacClient CreateClient() =>
        clientFactory() ?? throw new InvalidOperationException("The b-PAC client factory returned null.");

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed != 0, this);
}
