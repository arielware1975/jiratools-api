namespace ApiJiraTools.Configuration;

public class JiraSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string StoryPointsFieldId { get; set; } = "customfield_10024";
    public string StoryPointEstimateFieldId { get; set; } = "customfield_10016";
}
