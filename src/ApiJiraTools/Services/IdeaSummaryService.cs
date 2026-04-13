using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public class IdeaSummaryService
{
    private readonly JiraService _jira;
    private readonly ILogger<IdeaSummaryService> _logger;
    private const int MinCommentWords = 8;

    public IdeaSummaryService(JiraService jira, ILogger<IdeaSummaryService> logger)
    {
        _jira = jira;
        _logger = logger;
    }

    public async Task<IdeaNode> BuildIdeaNodeAsync(string issueKey)
    {
        _logger.LogInformation("IdeaSummaryService.BuildIdeaNodeAsync: {IssueKey}", issueKey);

        var idea = await _jira.GetIssueByKeyAsync(issueKey);

        var node = new IdeaNode
        {
            Key = idea.Key,
            Summary = idea.Fields?.Summary ?? string.Empty,
            Status = idea.Fields?.Status?.Name ?? string.Empty,
            Roadmap = _jira.GetDiscoveryRoadmapValue(idea),
            Assignee = idea.Fields?.Assignee?.DisplayName ?? "Sin asignar",
            Description = idea.Fields?.GetDescriptionText() ?? string.Empty,
            Attachments = MapAttachments(idea.Fields?.Attachments),
        };

        node.Comments = await GetFilteredCommentsAsync(idea.Key);

        // Épicas vinculadas
        var epicRefs = JiraService.GetLinkedEpicsFromIdea(idea);

        foreach (var (epicKey, epicSummary) in epicRefs)
        {
            try
            {
                var epicNode = await BuildEpicNodeAsync(epicKey, epicSummary);
                node.Epics.Add(epicNode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error procesando épica {EpicKey}", epicKey);
            }
        }

        return node;
    }

    public async Task<EpicNode> BuildEpicNodeAsync(string epicKey, string epicSummary = "")
    {
        var epic = await _jira.GetIssueByKeyAsync(epicKey);

        var node = new EpicNode
        {
            Key = epicKey,
            Summary = epic.Fields?.Summary ?? epicSummary,
            Status = epic.Fields?.Status?.Name ?? string.Empty,
            Assignee = epic.Fields?.Assignee?.DisplayName ?? "Sin asignar",
            Description = epic.Fields?.GetDescriptionText() ?? string.Empty,
            Attachments = MapAttachments(epic.Fields?.Attachments),
        };

        node.Comments = await GetFilteredCommentsAsync(epicKey);

        // Issues hijos
        List<JiraIssue> children;
        try { children = await _jira.GetEpicChildIssuesAsync(epicKey); }
        catch { children = new(); }

        // Procesar hijos con throttle
        var sem = new SemaphoreSlim(4, 4);
        var childTasks = children.Select(async child =>
        {
            await sem.WaitAsync();
            try { return await BuildChildNodeAsync(child, epicKey); }
            finally { sem.Release(); }
        });
        var childNodes = await Task.WhenAll(childTasks);
        node.Issues.AddRange(childNodes.OrderBy(c => c.Key));

        return node;
    }

    private async Task<ChildIssueNode> BuildChildNodeAsync(JiraIssue child, string epicKey)
    {
        double sp = child.Fields?.GetStoryPointsValue(_jira.StoryPointsFieldId) ?? 0d;
        if (sp <= 0) sp = child.Fields?.GetStoryPointEstimateValue(_jira.StoryPointEstimateFieldId) ?? 0d;

        var node = new ChildIssueNode
        {
            Key = child.Key,
            Summary = child.Fields?.Summary ?? string.Empty,
            IssueType = child.Fields?.IssueType?.Name ?? string.Empty,
            Status = child.Fields?.Status?.Name ?? string.Empty,
            Assignee = child.Fields?.Assignee?.DisplayName ?? "Sin asignar",
            Description = child.Fields?.GetDescriptionText() ?? string.Empty,
            EpicKey = epicKey,
            StoryPoints = sp > 0 ? sp : null,
            Attachments = MapAttachments(child.Fields?.Attachments),
        };

        node.Comments = await GetFilteredCommentsAsync(child.Key);
        return node;
    }

    private async Task<List<IssueCommentNode>> GetFilteredCommentsAsync(string issueKey)
    {
        List<JiraComment> raw;
        try { raw = await _jira.GetIssueCommentsAsync(issueKey); }
        catch { return new(); }

        return raw
            .Where(IsRelevantComment)
            .Select(c => new IssueCommentNode
            {
                Author = c.Author?.DisplayName ?? "Desconocido",
                Body = c.GetBodyText().Trim(),
                Created = c.CreatedDate ?? DateTimeOffset.MinValue
            })
            .OrderBy(c => c.Created)
            .ToList();
    }

    private static bool IsRelevantComment(JiraComment c)
    {
        string body = c.GetBodyText().Trim();
        if (string.IsNullOrWhiteSpace(body)) return false;
        string author = c.Author?.DisplayName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(author)) return false;
        string lower = body.ToLowerInvariant();
        if (lower.StartsWith("ranked ") || lower.StartsWith("sprint moved") ||
            lower.StartsWith("status changed") || lower.StartsWith("estado cambiado"))
            return false;
        return body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= MinCommentWords;
    }

    private static List<IssueAttachmentNode> MapAttachments(List<JiraAttachment>? attachments)
    {
        if (attachments == null || attachments.Count == 0) return new();
        return attachments.Select(a => new IssueAttachmentNode
        {
            Filename = a.Filename,
            MimeType = a.MimeType,
            Author = a.Author?.DisplayName ?? string.Empty
        }).ToList();
    }
}
