using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

/// <summary>
/// Para cada épica con issues en el sprint, verifica que exista una card STG
/// y que sus issue links ("is blocked by") cubran todos los dev issues de la épica.
/// </summary>
public sealed class StgChecklistService
{
    private readonly JiraService _jiraService;
    private readonly ILogger<StgChecklistService> _logger;

    public StgChecklistService(JiraService jiraService, ILogger<StgChecklistService> logger)
    {
        _jiraService = jiraService;
        _logger = logger;
    }

    public async Task<StgChecklistReport> BuildAsync(int sprintId, string sprintName)
    {
        _logger.LogInformation("StgChecklistService.BuildAsync: sprint={SprintId} ({SprintName})", sprintId, sprintName);

        var sprintIssues = await _jiraService.GetSprintIssuesDetailedAsync(sprintId);
        var sprintKeys = new HashSet<string>(sprintIssues.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);

        // Épicas únicas: issues cuyo parent directo sea una épica
        var epicMap = new Dictionary<string, JiraIssue>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in sprintIssues)
        {
            var parent = issue.Fields?.Parent;
            if (parent == null) continue;
            var parentType = parent.Fields?.IssueType?.Name ?? string.Empty;
            if (IsEpicType(parentType) && !epicMap.ContainsKey(parent.Key) && !ExcludedKeys.Contains(parent.Key))
                epicMap[parent.Key] = parent;
        }

        // Para obtener labels de las épicas (sprintIssues.Parent no siempre trae labels),
        // hacemos fetch completo de cada épica en paralelo.
        var fetchedEpics = new Dictionary<string, JiraIssue>(StringComparer.OrdinalIgnoreCase);
        var fetchSem = new SemaphoreSlim(4);
        await Task.WhenAll(epicMap.Keys.Select(async ek =>
        {
            await fetchSem.WaitAsync();
            try
            {
                var full = await _jiraService.GetIssueByKeyAsync(ek);
                lock (fetchedEpics) fetchedEpics[ek] = full;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo traer detalle de épica {EpicKey}", ek);
            }
            finally { fetchSem.Release(); }
        }));

        // Excluir épicas con label stg_not_required
        var excludedByLabel = fetchedEpics
            .Where(kv => HasLabel(kv.Value, "stg_not_required"))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var k in excludedByLabel)
            epicMap.Remove(k);

        if (excludedByLabel.Count > 0)
            _logger.LogInformation("StgChecklist: {Count} épica(s) excluida(s) por label stg_not_required: {Keys}",
                excludedByLabel.Count, string.Join(", ", excludedByLabel));

        var report = new StgChecklistReport
        {
            SprintName = sprintName,
            GeneratedAt = DateTime.Now,
        };

        foreach (var kvp in epicMap)
        {
            var epicKey = kvp.Key;
            var epicIssue = kvp.Value;

            List<JiraIssue> allChildren;
            try
            {
                allChildren = await _jiraService.GetEpicChildIssuesAsync(epicKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo hijos de épica {EpicKey}.", epicKey);
                continue;
            }

            // Card STG = primer hijo cuyo summary empieza con "STG"
            var stgCard = allChildren.FirstOrDefault(c =>
                (c.Fields?.Summary ?? string.Empty).TrimStart()
                .StartsWith("STG", StringComparison.OrdinalIgnoreCase));

            // Dev issues = hijos que NO son operacionales (STG, Pasaje, PROD) y NO están finalizados
            var devIssues = allChildren
                .Where(c => !IsOperationalTask(c) && !IsDone(c))
                .ToList();

            // Issues cubiertos por la card STG vienen de sus IssueLinks
            var stgRows = new List<StgIssueRow>();
            if (stgCard != null)
            {
                foreach (var link in stgCard.Fields?.IssueLinks ?? Enumerable.Empty<JiraIssueLink>())
                {
                    var linked = link.InwardIssue ?? link.OutwardIssue;
                    if (linked == null) continue;
                    if (ExcludedKeys.Contains(linked.Key)) continue;
                    if (IsOperationalSummary(linked.Fields?.Summary ?? string.Empty)) continue;
                    if (IsDoneLinked(linked)) continue;
                    stgRows.Add(new StgIssueRow
                    {
                        Key = linked.Key,
                        Summary = linked.Fields?.Summary ?? string.Empty,
                        Status = linked.Fields?.Status?.Name ?? string.Empty,
                        IssueType = linked.Fields?.IssueType?.Name ?? string.Empty,
                        Assignee = string.Empty,
                        IsInSprint = sprintKeys.Contains(linked.Key),
                    });
                }
            }

            var devRows = devIssues.Select(i => ToRow(i, sprintKeys.Contains(i.Key))).ToList();

            // Match por key exacta
            var stgCoveredKeys = new HashSet<string>(stgRows.Select(r => r.Key), StringComparer.OrdinalIgnoreCase);

            foreach (var dev in devRows)
            {
                if (!stgCoveredKeys.Contains(dev.Key)) continue;
                dev.IsMatched = true;
                var stgRow = stgRows.FirstOrDefault(r =>
                    string.Equals(r.Key, dev.Key, StringComparison.OrdinalIgnoreCase));
                if (stgRow != null) stgRow.IsMatched = true;
            }

            var missingFromStg = devRows.Where(r => !r.IsMatched).ToList();
            var extraInStg = stgRows.Where(r => !r.IsMatched).ToList();

            StgAlignment alignment;
            if (stgCard == null) alignment = StgAlignment.NoCard;
            else if (stgRows.Count == 0) alignment = StgAlignment.Empty;
            else if (missingFromStg.Count == 0) alignment = StgAlignment.Ok;
            else alignment = StgAlignment.Partial;

            report.Epics.Add(new StgEpicRow
            {
                EpicKey = epicKey,
                EpicSummary = epicIssue.Fields?.Summary ?? epicKey,
                EpicStatus = epicIssue.Fields?.Status?.Name ?? string.Empty,
                HasStgCard = stgCard != null,
                StgKey = stgCard?.Key ?? string.Empty,
                StgSummary = stgCard?.Fields?.Summary ?? string.Empty,
                StgStatus = stgCard?.Fields?.Status?.Name ?? string.Empty,
                StgInSprint = stgCard != null && sprintKeys.Contains(stgCard.Key),
                DevIssues = devRows,
                StgSubtasks = stgRows,
                Alignment = alignment,
                MissingFromStg = missingFromStg,
                ExtraInStg = extraInStg,
            });
        }

        report.Epics = report.Epics
            .OrderBy(e => AlignmentOrder(e.Alignment))
            .ThenBy(e => e.EpicKey)
            .ToList();

        // PROD cards: issues del sprint cuyo summary empieza con "Pasaje a Prod"
        foreach (var prodIssue in sprintIssues.Where(i =>
            (i.Fields?.Summary ?? string.Empty).TrimStart()
            .StartsWith("Pasaje a Prod", StringComparison.OrdinalIgnoreCase)))
        {
            var linkedStg = new List<StgIssueRow>();
            foreach (var link in prodIssue.Fields?.IssueLinks ?? Enumerable.Empty<JiraIssueLink>())
            {
                var linked = link.InwardIssue ?? link.OutwardIssue;
                if (linked == null) continue;
                linkedStg.Add(new StgIssueRow
                {
                    Key = linked.Key,
                    Summary = linked.Fields?.Summary ?? string.Empty,
                    Status = linked.Fields?.Status?.Name ?? string.Empty,
                    IssueType = linked.Fields?.IssueType?.Name ?? string.Empty,
                    IsInSprint = sprintKeys.Contains(linked.Key),
                });
            }
            report.ProdCards.Add(new ProdCardRow
            {
                Key = prodIssue.Key,
                Summary = prodIssue.Fields?.Summary ?? string.Empty,
                Status = prodIssue.Fields?.Status?.Name ?? string.Empty,
                LinkedStgCards = linkedStg,
            });
        }

        _logger.LogInformation("StgChecklistService finalizado: {EpicCount} épicas, {ProdCount} cards PROD.", report.TotalEpics, report.ProdCards.Count);
        return report;
    }

    private static int AlignmentOrder(StgAlignment a) => a switch
    {
        StgAlignment.NoCard => 0,
        StgAlignment.Empty => 1,
        StgAlignment.Partial => 2,
        StgAlignment.Ok => 3,
        _ => 4,
    };

    private StgIssueRow ToRow(JiraIssue issue, bool isInSprint) => new()
    {
        Key = issue.Key,
        Summary = issue.Fields?.Summary ?? string.Empty,
        Status = issue.Fields?.Status?.Name ?? string.Empty,
        Assignee = issue.Fields?.Assignee?.DisplayName ?? string.Empty,
        IssueType = issue.Fields?.IssueType?.Name ?? string.Empty,
        StoryPoints = GetSp(issue),
        IsInSprint = isInSprint,
    };

    private double GetSp(JiraIssue issue)
    {
        if (issue?.Fields == null) return 0d;
        var sp = issue.Fields.GetStoryPointsValue(_jiraService.StoryPointsFieldId);
        if (sp > 0) return sp;
        return issue.Fields.GetStoryPointEstimateValue(_jiraService.StoryPointEstimateFieldId);
    }

    private static bool IsDone(JiraIssue issue)
    {
        var status = issue?.Fields?.Status;
        if (status == null) return false;
        return IsCompletedStatus(status.StatusCategory?.Key ?? string.Empty, status.Name);
    }

    private static bool IsDoneLinked(JiraLinkedIssue linked)
    {
        var status = linked?.Fields?.Status;
        if (status == null) return false;
        return IsCompletedStatus(status.StatusCategory?.Key ?? string.Empty, status.Name);
    }

    private static bool IsCompletedStatus(string categoryKey, string statusName) =>
        string.Equals(categoryKey, "done", StringComparison.OrdinalIgnoreCase)
        || statusName.Contains("cancel", StringComparison.OrdinalIgnoreCase)
        || statusName.Contains("cancelad", StringComparison.OrdinalIgnoreCase);

    private static bool IsEpicType(string typeName) =>
        string.Equals(typeName, "Epic", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "Épica", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "CTA-646",  // Issue de publicación de releases PROD
    };

    private static bool IsOperationalTask(JiraIssue issue) =>
        ExcludedKeys.Contains(issue?.Key ?? string.Empty)
        || IsOperationalSummary(issue?.Fields?.Summary ?? string.Empty);

    private static bool IsOperationalSummary(string summary)
    {
        var s = summary.TrimStart();
        return s.StartsWith("STG", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("Pasaje", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("PROD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLabel(JiraIssue? issue, string label)
        => issue?.Fields?.Labels != null
           && issue.Fields.Labels.Any(x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase));
}
