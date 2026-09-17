namespace Inventoryzing.Agent.Printer.Host;

public sealed class PrinterAgentOptions
{
    public const string SectionName = "Inventoryzing:Printer";

    public bool Enabled { get; set; }
    public Uri? CoordinatorUri { get; set; }
    public string CredentialFile { get; set; } = string.Empty;
    public string PrinterId { get; set; } = "brother:ql-820nwb";
    public string RasterHost { get; set; } = string.Empty;
    public int RasterPort { get; set; }
    public int StatusPort { get; set; } = 161;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan CompletionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
