using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApiJiraTools.Configuration;
using ApiJiraTools.Models;
using Microsoft.Extensions.Options;

namespace ApiJiraTools.Services;

public class JiraService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger<JiraService> _logger;

    public string StoryPointsFieldId { get; }
    public string StoryPointEstimateFieldId { get; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public JiraService(IOptions<JiraSettings> options, ILogger<JiraService> logger)
    {
        var settings = options.Value;
        _logger = logger;

        ArgumentException.ThrowIfNullOrWhiteSpace(settings.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ApiToken);

        _logger.LogInformation("Inicializando JiraService para {BaseUrl}.", settings.BaseUrl.TrimEnd('/'));
        _baseUrl = settings.BaseUrl.TrimEnd('/');

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl)
        };

        string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Email}:{settings.ApiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        StoryPointsFieldId = settings.StoryPointsFieldId;
        StoryPointEstimateFieldId = settings.StoryPointEstimateFieldId;
    }

    public async Task<List<JiraProject>> GetProjectsAsync()
    {
        _logger.LogInformation("GetProjectsAsync iniciado.");
        var allProjects = new List<JiraProject>();
        string? nextPage = "/rest/api/3/project/search?maxResults=50&orderBy=key";

        while (!string.IsNullOrWhiteSpace(nextPage))
        {
            using var response = await _httpClient.GetAsync(nextPage);
            string content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Error HTTP al obtener proyectos. Código {StatusCode}.", (int)response.StatusCode);
                throw new HttpRequestException($"Error al obtener proyectos. HTTP {(int)response.StatusCode}: {content}");
            }

            var result = JsonSerializer.Deserialize<JiraProjectSearchResult>(content, JsonOptions)
                         ?? new JiraProjectSearchResult();

            if (result.Values.Count > 0)
                allProjects.AddRange(result.Values);

            nextPage = result.IsLast ? null : NormalizeNextPage(result.NextPage);
        }

        var ordered = allProjects.OrderBy(x => x.Key).ToList();
        _logger.LogInformation("GetProjectsAsync finalizado. Proyectos: {Count}.", ordered.Count);
        return ordered;
    }

    public async Task<List<JiraSprint>> GetSprintsByProjectAsync(string projectKey)
    {
        _logger.LogInformation("GetSprintsByProjectAsync iniciado. projectKey={ProjectKey}", projectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);

        var boardId = await GetBoardIdByProjectAsync(projectKey);
        if (boardId == null)
            return new List<JiraSprint>();

        var sprints = new List<JiraSprint>();
        int startAt = 0;
        bool isLast = false;

        while (!isLast)
        {
            string url = $"/rest/agile/1.0/board/{boardId.Value}/sprint?startAt={startAt}&maxResults=50";
            using var response = await _httpClient.GetAsync(url);
            string content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Error HTTP al obtener sprints del proyecto {ProjectKey}. Código {StatusCode}.", projectKey, (int)response.StatusCode);
                throw new HttpRequestException($"Error al obtener sprints. HTTP {(int)response.StatusCode}: {content}");
            }

            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("values", out var valuesElement) &&
                valuesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valuesElement.EnumerateArray())
                {
                    var sprint = JsonSerializer.Deserialize<JiraSprint>(item.GetRawText(), JsonOptions);
                    if (sprint != null)
                        sprints.Add(sprint);
                }
            }

            int maxResults = doc.RootElement.TryGetProperty("maxResults", out var maxEl) ? maxEl.GetInt32() : 50;
            int start = doc.RootElement.TryGetProperty("startAt", out var startEl) ? startEl.GetInt32() : 0;
            int total = doc.RootElement.TryGetProperty("total", out var totalEl) ? totalEl.GetInt32() : sprints.Count;
            isLast = doc.RootElement.TryGetProperty("isLast", out var lastEl) && lastEl.GetBoolean();

            startAt = start + maxResults;
            if (!isLast && startAt >= total)
                isLast = true;
        }

        var ordered = sprints
            .OrderByDescending(x => x.StartDate ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => x.Id)
            .ToList();

        _logger.LogInformation("GetSprintsByProjectAsync finalizado. Sprints: {Count}.", ordered.Count);
        return ordered;
    }

    public async Task<List<JiraIssue>> GetSprintIssuesDetailedAsync(int sprintId)
    {
        _logger.LogInformation("GetSprintIssuesDetailedAsync iniciado. sprintId={SprintId}", sprintId);
        string jql = $"sprint = {sprintId} ORDER BY updated DESC";

        var request = new JiraJqlSearchRequest
        {
            Jql = jql,
            MaxResults = 100,
            Fields = BuildStandardIssueFields()
        };

        var issues = await SearchIssuesAllPagesAsync(request);
        _logger.LogInformation("GetSprintIssuesDetailedAsync finalizado. Issues: {Count}.", issues.Count);
        return issues;
    }

    public async Task<JiraIssue> GetIssueByKeyAsync(string issueKey)
    {
        _logger.LogInformation("GetIssueByKeyAsync iniciado. issueKey={IssueKey}", issueKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueKey);

        string fields = string.Join(",", BuildStandardIssueFields());
        string url = $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields={fields}";

        using var response = await _httpClient.GetAsync(url);
        string content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Error HTTP al obtener issue {IssueKey}. Código {StatusCode}.", issueKey, (int)response.StatusCode);
            throw new HttpRequestException($"Error al obtener issue {issueKey}. HTTP {(int)response.StatusCode}: {content}");
        }

        var issue = JsonSerializer.Deserialize<JiraIssue>(content, JsonOptions)
            ?? throw new InvalidOperationException($"No se pudo deserializar el issue {issueKey}.");

        _logger.LogInformation("GetIssueByKeyAsync finalizado. issueKey={IssueKey}", issue.Key);
        return issue;
    }

    public async Task<List<JiraEpicSummary>> GetEpicsBySprintAsync(int sprintId)
    {
        _logger.LogInformation("GetEpicsBySprintAsync iniciado. sprintId={SprintId}", sprintId);
        var sprintIssues = await GetSprintIssuesDetailedAsync(sprintId);
        if (sprintIssues.Count == 0)
            return new List<JiraEpicSummary>();

        var parentEpics = sprintIssues
            .Where(x => x.Fields?.Parent != null &&
                        string.Equals(x.Fields.Parent.Fields?.IssueType?.Name, "Epic", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Fields!.Parent!)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Key)
            .ToList();

        var epicSummaries = new List<JiraEpicSummary>();

        foreach (var epicIssue in parentEpics)
        {
            string epicKey = epicIssue.Key;
            var childIssues = await GetEpicChildIssuesAsync(epicKey);

            var stgIssue = childIssues.FirstOrDefault(x =>
                x.Fields != null && !string.IsNullOrWhiteSpace(x.Fields.Summary) &&
                x.Fields.Summary.StartsWith("STG", StringComparison.OrdinalIgnoreCase));

            var sprintIssueKeys = new HashSet<string>(sprintIssues.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);

            var childInfos = childIssues
                .Select(child => new JiraEpicChildInfo
                {
                    Key = child.Key,
                    Summary = child.Fields?.Summary ?? string.Empty,
                    IssueTypeName = child.Fields?.IssueType?.Name ?? string.Empty,
                    IsInSelectedSprint = sprintIssueKeys.Contains(child.Key)
                })
                .OrderByDescending(x => x.IsInSelectedSprint)
                .ThenBy(x => x.Key)
                .ToList();

            var epicFullIssue = await GetIssueByKeyAsync(epicKey);
            var epicLabels = epicFullIssue.Fields?.Labels?.ToList() ?? new List<string>();

            epicSummaries.Add(new JiraEpicSummary
            {
                EpicId = epicFullIssue.Id,
                EpicKey = epicKey,
                EpicSummary = epicFullIssue.Fields?.Summary ?? epicIssue.Fields?.Summary ?? epicKey,
                HasStgChild = stgIssue != null,
                StgIssueKey = stgIssue?.Key ?? string.Empty,
                StgIssueSummary = stgIssue?.Fields?.Summary ?? string.Empty,
                ChildCount = childInfos.Count,
                SprintChildCount = childInfos.Count(x => x.IsInSelectedSprint),
                Labels = epicLabels,
                IsStgNotRequired = epicLabels.Any(x => string.Equals(x, "stg_not_required", StringComparison.OrdinalIgnoreCase)),
                Children = childInfos
            });
        }

        _logger.LogInformation("GetEpicsBySprintAsync finalizado. Épicas: {Count}.", epicSummaries.Count);
        return epicSummaries;
    }

    public async Task<List<JiraIssue>> GetEpicChildIssuesAsync(string epicKey)
    {
        _logger.LogInformation("GetEpicChildIssuesAsync iniciado. epicKey={EpicKey}", epicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(epicKey);

        string jql = $"(parent = {epicKey} OR \"Epic Link\" = {epicKey}) ORDER BY key ASC";
        var request = new JiraJqlSearchRequest
        {
            Jql = jql,
            MaxResults = 100,
            Fields = BuildStandardIssueFields()
        };

        var childIssues = await SearchIssuesAllPagesAsync(request);
        childIssues = childIssues
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation("GetEpicChildIssuesAsync finalizado. Hijos: {Count}.", childIssues.Count);
        return childIssues;
    }

    public async Task<List<JiraIssue>> SearchIssuesByJqlAsync(string jql, int maxResults = 100)
    {
        _logger.LogInformation("SearchIssuesByJqlAsync. JQL={Jql}", jql);
        var request = new JiraJqlSearchRequest
        {
            Jql = jql,
            MaxResults = maxResults,
            Fields = BuildStandardIssueFields()
        };
        return await SearchIssuesAllPagesAsync(request);
    }

    // ── Discovery methods ───────────────────────────────────────────────

    public async Task<List<JiraFieldDefinition>> GetFieldsAsync()
    {
        _logger.LogInformation("GetFieldsAsync iniciado.");
        using var response = await _httpClient.GetAsync("/rest/api/3/field");
        string content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Error al obtener fields. HTTP {(int)response.StatusCode}: {content}");
        return JsonSerializer.Deserialize<List<JiraFieldDefinition>>(content, JsonOptions) ?? new();
    }

    public JiraDiscoveryFieldMap BuildDiscoveryFieldMap(List<JiraFieldDefinition> fields)
    {
        fields ??= new();
        return new JiraDiscoveryFieldMap
        {
            RoadmapFieldId = FindFieldId(fields, "Hoja de ruta", "Roadmap"),
            RoadmapFieldIdAlt = "customfield_10204",
            ProjectTargetFieldId = FindFieldId(fields, "Objetivo del proyecto") ?? "customfield_10210",
            ProjectTargetFieldIdAlt = "customfield_10078",
            ProjectStartFieldId = FindFieldId(fields, "Inicio del proyecto") ?? "customfield_10209",
            ProjectStartFieldIdAlt = "customfield_10077",
            ImpactFieldId = FindFieldId(fields, "Impact", "Impacto"),
            EffortFieldId = FindFieldId(fields, "Effort", "Esfuerzo"),
            InsightsFieldId = FindFieldId(fields, "Insights", "Información"),
            ScoreFieldId = FindFieldId(fields, "Puntuación del impacto", "Score"),
            OwnerFieldId = FindFieldId(fields, "Owner", "Responsible"),
            DeliveryStatusFieldId = FindFieldId(fields, "Estado de la entrega", "Delivery status"),
        };
    }

    public async Task<List<JiraIssue>> GetDiscoveryIdeasAsync(string projectKey, int maxResults = 200)
    {
        _logger.LogInformation("GetDiscoveryIdeasAsync. projectKey={ProjectKey}", projectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);

        string jql = $"project = {projectKey} ORDER BY Rank ASC";
        var request = new JiraJqlSearchRequest { Jql = jql, MaxResults = maxResults, Fields = BuildStandardIssueFields() };
        var issues = await SearchIssuesAllPagesAsync(request);

        return issues
            .Where(i => string.Equals(i.Fields?.IssueType?.Name, "Idea", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<List<JiraIssue>> GetDiscoveryIdeasByRoadmapAsync(string projectKey, string? roadmapValue, int maxResults = 200)
    {
        var ideas = await GetDiscoveryIdeasAsync(projectKey, maxResults);
        var fieldMap = BuildDiscoveryFieldMap(await GetFieldsAsync());

        if (string.IsNullOrWhiteSpace(roadmapValue))
            return ideas;

        return ideas.Where(i => MatchesRoadmapFilter(GetRoadmapValue(i, fieldMap), roadmapValue)).ToList();
    }

    public string GetDiscoveryRoadmapValue(JiraIssue issue)
    {
        // Use hardcoded field IDs as fallback
        if (issue?.Fields == null) return string.Empty;
        var val = issue.Fields.GetCustomFieldOptionValue("customfield_10852");
        if (!string.IsNullOrWhiteSpace(val)) return val;
        val = issue.Fields.GetCustomFieldOptionValue("customfield_10204");
        if (!string.IsNullOrWhiteSpace(val)) return val;
        val = issue.Fields.GetCustomFieldAsString("customfield_10852");
        if (!string.IsNullOrWhiteSpace(val)) return val;
        val = issue.Fields.GetCustomFieldAsString("customfield_10204");
        return val ?? string.Empty;
    }

    private static string GetRoadmapValue(JiraIssue issue, JiraDiscoveryFieldMap fieldMap)
    {
        if (issue?.Fields == null || fieldMap == null) return string.Empty;
        var val = issue.Fields.GetCustomFieldOptionValue(fieldMap.RoadmapFieldId ?? "");
        if (!string.IsNullOrWhiteSpace(val)) return val;
        val = issue.Fields.GetCustomFieldOptionValue(fieldMap.RoadmapFieldIdAlt ?? "");
        if (!string.IsNullOrWhiteSpace(val)) return val;
        val = issue.Fields.GetCustomFieldAsString(fieldMap.RoadmapFieldId ?? "");
        if (!string.IsNullOrWhiteSpace(val)) return val;
        val = issue.Fields.GetCustomFieldAsString(fieldMap.RoadmapFieldIdAlt ?? "");
        return val ?? string.Empty;
    }

    private static bool MatchesRoadmapFilter(string? issueValue, string? filter)
    {
        var f = (filter ?? "").Trim();
        if (string.IsNullOrWhiteSpace(f) || f.Equals("Todos", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals((issueValue ?? "").Trim(), f, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindFieldId(List<JiraFieldDefinition> fields, params string[] names)
    {
        foreach (var name in names)
        {
            var match = fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Id;
        }
        return null;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task<int?> GetBoardIdByProjectAsync(string projectKey)
    {
        _logger.LogInformation("GetBoardIdByProjectAsync iniciado. projectKey={ProjectKey}", projectKey);
        string url = $"/rest/agile/1.0/board?projectKeyOrId={Uri.EscapeDataString(projectKey)}&maxResults=50";

        using var response = await _httpClient.GetAsync(url);
        string content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Error HTTP al obtener boards del proyecto {ProjectKey}. Código {StatusCode}.", projectKey, (int)response.StatusCode);
            throw new HttpRequestException($"Error al obtener boards. HTTP {(int)response.StatusCode}: {content}");
        }

        var result = JsonSerializer.Deserialize<JiraBoardSearchResult>(content, JsonOptions)
                     ?? new JiraBoardSearchResult();

        var scrumBoard = result.Values
            .FirstOrDefault(x => string.Equals(x.Type, "scrum", StringComparison.OrdinalIgnoreCase));

        _logger.LogInformation("GetBoardIdByProjectAsync finalizado. boardId={BoardId}", scrumBoard?.Id);
        return scrumBoard?.Id;
    }

    private async Task<List<JiraIssue>> SearchIssuesAllPagesAsync(JiraJqlSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogInformation("SearchIssuesAllPagesAsync iniciado. JQL={Jql}", request.Jql);

        var issues = new List<JiraIssue>();
        string? nextPageToken = null;
        bool isLast = false;

        do
        {
            request.NextPageToken = nextPageToken;
            string json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync("/rest/api/3/search/jql", content);
            string responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Error HTTP en búsqueda JQL. Código {StatusCode}. JQL={Jql}", (int)response.StatusCode, request.Jql);
                throw new HttpRequestException($"Error en búsqueda JQL. HTTP {(int)response.StatusCode}: {responseContent}");
            }

            var result = JsonSerializer.Deserialize<JiraJqlSearchResult>(responseContent, JsonOptions)
                         ?? new JiraJqlSearchResult();

            if (result.Issues.Count > 0)
                issues.AddRange(result.Issues);

            isLast = result.IsLast;
            nextPageToken = result.NextPageToken;
        }
        while (!isLast && !string.IsNullOrWhiteSpace(nextPageToken));

        _logger.LogInformation("SearchIssuesAllPagesAsync finalizado. Issues: {Count}.", issues.Count);
        return issues;
    }

    private static string? NormalizeNextPage(string? nextPage)
    {
        if (string.IsNullOrWhiteSpace(nextPage))
            return null;
        if (Uri.TryCreate(nextPage, UriKind.Absolute, out var absolute))
            return absolute.PathAndQuery;
        return nextPage;
    }

    private List<string> BuildStandardIssueFields()
    {
        var fields = new List<string>
        {
            "summary", "issuetype", "status", "assignee", "labels",
            "priority", "updated", "resolutiondate", "parent",
            "issuelinks", "description", "duedate",
            "customfield_10020", // sprint
            "attachment"
        };

        AddIfNotExists(fields, StoryPointsFieldId);
        AddIfNotExists(fields, StoryPointEstimateFieldId);

        return fields;
    }

    private static void AddIfNotExists(List<string> fields, string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId)) return;
        if (!fields.Any(x => string.Equals(x, fieldId, StringComparison.OrdinalIgnoreCase)))
            fields.Add(fieldId);
    }
}
