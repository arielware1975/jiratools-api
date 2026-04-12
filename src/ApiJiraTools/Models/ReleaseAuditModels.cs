namespace ApiJiraTools.Models;

public class ReleaseAuditReport
{
    public string ProjectKey { get; set; } = string.Empty;
    public string SprintName { get; set; } = string.Empty;
    public int TotalIdeas { get; set; }
    public int TotalEpics { get; set; }
    public int TotalIssues { get; set; }
    public int TotalProxRelease { get; set; }
    public List<ReleaseAuditAlert> Alerts { get; set; } = new();
    public List<ReleaseAuditIdeaDetail> IdeaDetails { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
    public DateTime? SprintStart { get; set; }
    public DateTime? SprintEnd { get; set; }
    public DateTime? FechaStg { get; set; }
    public DateTime? FechaProd { get; set; }
    public bool HasProdCard { get; set; }
    public string ProdCardKey { get; set; } = string.Empty;
    public string ProdCardStatus { get; set; } = string.Empty;
}

public enum ReleaseAuditSeverity { Error, Warning, Info }

public class ReleaseAuditAlert
{
    public ReleaseAuditSeverity Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string IssueKey { get; set; } = string.Empty;
}

public class ReleaseAuditIdeaDetail
{
    public string IdeaKey { get; set; } = string.Empty;
    public string IdeaSummary { get; set; } = string.Empty;
    public string IdeaStatus { get; set; } = string.Empty;
    public string IdeaRoadmap { get; set; } = string.Empty;
    public bool IdeaProxRelease { get; set; }
    public string IdeaTargetDate { get; set; } = string.Empty;
    public bool HasDescription { get; set; }
    public int EpicCount { get; set; }
    public List<ReleaseAuditEpicDetail> Epics { get; set; } = new();
}

public class ReleaseAuditEpicDetail
{
    public string EpicKey { get; set; } = string.Empty;
    public string EpicSummary { get; set; } = string.Empty;
    public string EpicStatus { get; set; } = string.Empty;
    public bool HasDescription { get; set; }
    public DateTime? TargetDate { get; set; }
    public bool HasStgCard { get; set; }
    public string StgCardKey { get; set; } = string.Empty;
    public string StgCardStatus { get; set; } = string.Empty;
    public int ProxReleaseCount { get; set; }
    public int ProxReleaseDone { get; set; }
    public int ProxReleaseInProgress { get; set; }
    public int ProxReleaseToDo { get; set; }
    public int WorkIssueCount { get; set; }
    public int WorkIssueDone { get; set; }
    public int WorkIssueInProgress { get; set; }
    public int WorkIssueToDo { get; set; }
}
