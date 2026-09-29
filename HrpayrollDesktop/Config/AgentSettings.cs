namespace HrpayrollDesktop.Config;

public class AgentSettings
{
    public string BackendBaseUrl { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 1;
    public int SyncIntervalSeconds { get; set; } = 15;
}