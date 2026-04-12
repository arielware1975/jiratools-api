namespace ApiJiraTools.Configuration;

public class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Chat IDs that receive daily alerts. Comma-separated.</summary>
    public string AlertChatIds { get; set; } = string.Empty;

    /// <summary>Project keys for alerts. Comma-separated (e.g. "CTA,EC").</summary>
    public string AlertProjects { get; set; } = string.Empty;

    /// <summary>Hour (UTC) to send daily alerts. Default: 12 (= 9am ART).</summary>
    public int AlertHourUtc { get; set; } = 12;

    /// <summary>Minute (UTC) for daily alerts. Default: 0.</summary>
    public int AlertMinuteUtc { get; set; } = 0;

    /// <summary>Mapping dev project → discovery project. Format: "CTA:PC;EC:PEC".</summary>
    public string DiscoveryProjectMapping { get; set; } = "CTA:PC";
}
