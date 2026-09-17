namespace Inventoryzing.Agent.Runtime.Printing;

public enum PrintDispatchAuthorizationStatus
{
    Granted,
    Rejected,
}

public sealed record PrintDispatchAuthorization(
    Guid AcknowledgementId,
    PrintDispatchAuthorizationStatus Status,
    string? Detail);

public interface IPrintDispatchAuthority
{
    // The coordinator implementation renews the claim and durably acknowledges
    // dispatch-started in one idempotent operation. An exception leaves the local
    // attempt Prepared, before the physical dispatch barrier.
    ValueTask<PrintDispatchAuthorization> AuthorizeAsync(
        PrintDispatchIntentRecord intent,
        CancellationToken cancellationToken);
}

internal sealed class LocalPrintDispatchAuthority : IPrintDispatchAuthority
{
    public ValueTask<PrintDispatchAuthorization> AuthorizeAsync(
        PrintDispatchIntentRecord intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PrintDispatchAuthorization(
            Guid.NewGuid(),
            PrintDispatchAuthorizationStatus.Granted,
            "Local runtime authorization."));
    }
}
