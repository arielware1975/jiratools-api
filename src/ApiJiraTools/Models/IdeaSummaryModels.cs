namespace ApiJiraTools.Models;

public class IdeaNode
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Roadmap { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<IssueCommentNode> Comments { get; set; } = new();
    public List<IssueAttachmentNode> Attachments { get; set; } = new();
    public List<EpicNode> Epics { get; set; } = new();
}

public class EpicNode
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<IssueCommentNode> Comments { get; set; } = new();
    public List<IssueAttachmentNode> Attachments { get; set; } = new();
    public List<ChildIssueNode> Issues { get; set; } = new();
}

public class ChildIssueNode
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EpicKey { get; set; } = string.Empty;
    public double? StoryPoints { get; set; }
    public List<IssueCommentNode> Comments { get; set; } = new();
    public List<IssueAttachmentNode> Attachments { get; set; } = new();
}

public class IssueCommentNode
{
    public string Author { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset Created { get; set; }
}

public class IssueAttachmentNode
{
    public string Filename { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
}
