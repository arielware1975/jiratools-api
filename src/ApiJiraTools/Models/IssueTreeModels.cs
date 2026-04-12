namespace ApiJiraTools.Models;

public sealed class IssueTreeReport
{
    public string SearchKey { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public string? IdeaKey { get; set; }
    public string? IdeaSummary { get; set; }
    public string? IdeaStatus { get; set; }
    public string? EpicKey { get; set; }
    public string? EpicSummary { get; set; }
    public string? EpicStatus { get; set; }
    public string IssueKey { get; set; } = string.Empty;
    public string IssueSummary { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string IssueStatus { get; set; } = string.Empty;
    public string IssueAssignee { get; set; } = string.Empty;
    public double IssueStoryPoints { get; set; }
    public string? IssueSprint { get; set; }
    public List<string> IssueLabels { get; set; } = new();
    public List<IssueTreeChild> Children { get; set; } = new();
    public List<IssueTreeChild> Siblings { get; set; } = new();
    public List<IssueTreeLink> Links { get; set; } = new();
    public List<IssueTreeLink> BlockedBy { get; set; } = new();
    public List<IssueTreeLink> Blocks { get; set; } = new();
}

public sealed class IssueTreeChild
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public double StoryPoints { get; set; }
    public string? Sprint { get; set; }
}

public sealed class IssueTreeLink
{
    public string Relation { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
}
