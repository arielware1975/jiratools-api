using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

public sealed class IssueTreeService
{
    private readonly JiraService _jira;
    private readonly ILogger<IssueTreeService> _logger;

    public IssueTreeService(JiraService jira, ILogger<IssueTreeService> logger)
    {
        _jira = jira;
        _logger = logger;
    }

    public async Task<IssueTreeReport> BuildAsync(string issueKey)
    {
        _logger.LogInformation("IssueTreeService.BuildAsync: issueKey={IssueKey}", issueKey);

        var issue = await _jira.GetIssueByKeyAsync(issueKey);
        var report = new IssueTreeReport
        {
            SearchKey = issueKey,
            GeneratedAt = DateTime.UtcNow,
            IssueKey = issue.Key,
            IssueSummary = issue.Fields?.Summary ?? string.Empty,
            IssueType = issue.Fields?.IssueType?.Name ?? string.Empty,
            IssueStatus = issue.Fields?.Status?.Name ?? string.Empty,
            IssueAssignee = issue.Fields?.Assignee?.DisplayName ?? "Sin asignar",
            IssueStoryPoints = GetSp(issue),
            IssueSprint = issue.Fields?.GetSprintName(),
            IssueLabels = issue.Fields?.Labels?.ToList() ?? new List<string>(),
        };

        // Links y bloqueantes
        foreach (var link in issue.Fields?.IssueLinks ?? Enumerable.Empty<JiraIssueLink>())
        {
            var linkType = link.Type;
            if (linkType == null) continue;

            string relation;
            JiraLinkedIssue? linked;
            if (link.OutwardIssue != null)
            {
                relation = linkType.Outward ?? linkType.Name;
                linked = link.OutwardIssue;
            }
            else if (link.InwardIssue != null)
            {
                relation = linkType.Inward ?? linkType.Name;
                linked = link.InwardIssue;
            }
            else continue;

            var treeLink = new IssueTreeLink
            {
                Relation = relation,
                Key = linked.Key,
                Summary = linked.Fields?.Summary ?? string.Empty,
                Status = linked.Fields?.Status?.Name ?? string.Empty,
                IssueType = linked.Fields?.IssueType?.Name ?? string.Empty,
            };

            string linkName = linkType.Name ?? string.Empty;
            if (linkName.Contains("Block", StringComparison.OrdinalIgnoreCase))
            {
                if (link.InwardIssue != null && (linkType.Inward ?? "").Contains("blocked", StringComparison.OrdinalIgnoreCase))
                    report.BlockedBy.Add(treeLink);
                else if (link.OutwardIssue != null && (linkType.Outward ?? "").Contains("block", StringComparison.OrdinalIgnoreCase))
                    report.Blocks.Add(treeLink);
                else
                    report.Links.Add(treeLink);
            }
            else
            {
                report.Links.Add(treeLink);
            }
        }

        // Épica padre
        var parent = issue.Fields?.Parent;
        bool isEpic = IsEpicType(report.IssueType);

        string? epicKey = null;
        if (isEpic)
        {
            epicKey = issue.Key;
            report.EpicKey = issue.Key;
            report.EpicSummary = report.IssueSummary;
            report.EpicStatus = report.IssueStatus;
        }
        else if (parent != null)
        {
            epicKey = parent.Key;
            report.EpicKey = parent.Key;
            report.EpicSummary = parent.Fields?.Summary ?? string.Empty;
            report.EpicStatus = parent.Fields?.Status?.Name ?? string.Empty;
        }

        // Hijos de la épica
        if (!string.IsNullOrEmpty(epicKey))
        {
            try
            {
                var children = await _jira.GetEpicChildIssuesAsync(epicKey);
                foreach (var child in children)
                {
                    var treeChild = new IssueTreeChild
                    {
                        Key = child.Key,
                        Summary = child.Fields?.Summary ?? string.Empty,
                        Status = child.Fields?.Status?.Name ?? string.Empty,
                        Assignee = child.Fields?.Assignee?.DisplayName ?? "Sin asignar",
                        IssueType = child.Fields?.IssueType?.Name ?? string.Empty,
                        StoryPoints = GetSp(child),
                        Sprint = child.Fields?.GetSprintName(),
                    };

                    if (isEpic)
                        report.Children.Add(treeChild);
                    else if (!child.Key.Equals(issue.Key, StringComparison.OrdinalIgnoreCase))
                        report.Siblings.Add(treeChild);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo hijos de épica {EpicKey}.", epicKey);
            }
        }

        return report;
    }

    private static bool IsEpicType(string typeName)
        => string.Equals(typeName, "Epic", StringComparison.OrdinalIgnoreCase)
        || string.Equals(typeName, "Épica", StringComparison.OrdinalIgnoreCase);

    private double GetSp(JiraIssue issue)
    {
        if (issue?.Fields == null) return 0d;
        var sp = issue.Fields.GetStoryPointsValue(_jira.StoryPointsFieldId);
        return sp > 0 ? sp : issue.Fields.GetStoryPointEstimateValue(_jira.StoryPointEstimateFieldId);
    }
}
