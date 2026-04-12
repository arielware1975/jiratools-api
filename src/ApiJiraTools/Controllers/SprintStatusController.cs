using ApiJiraTools.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApiJiraTools.Controllers;

[ApiController]
[Route("api/sprints")]
public class SprintStatusController : ControllerBase
{
    private readonly JiraService _jira;
    private readonly SprintClosureService _closure;
    private readonly BurndownService _burndown;

    public SprintStatusController(JiraService jira, SprintClosureService closure, BurndownService burndown)
    {
        _jira = jira;
        _closure = closure;
        _burndown = burndown;
    }

    /// <summary>Sprint status completo: velocidad, scope, carry-over, épicas, calidad.</summary>
    [HttpGet("{id:int}/status")]
    public async Task<IActionResult> GetSprintStatus(int id)
    {
        var sprint = await FindSprintById(id);
        if (sprint == null)
            return NotFound(new { error = $"Sprint {id} no encontrado." });

        // Necesitamos el project — lo inferimos del sprint name o buscamos
        var project = new Models.JiraProject { Key = "unknown" };
        var report = await _closure.BuildAsync(project, sprint);
        return Ok(report);
    }

    /// <summary>Burndown data del sprint.</summary>
    [HttpGet("{id:int}/burndown")]
    public async Task<IActionResult> GetBurndown(int id)
    {
        var sprint = await FindSprintById(id);
        if (sprint == null)
            return NotFound(new { error = $"Sprint {id} no encontrado." });

        var issues = await _jira.GetSprintIssuesDetailedAsync(id);
        var start = sprint.StartDate?.DateTime ?? DateTime.UtcNow.AddDays(-14);
        var end = sprint.EndDate?.DateTime ?? DateTime.UtcNow;

        var data = _burndown.Build(issues, start, end);
        return Ok(data);
    }

    /// <summary>Issues por assignee del sprint.</summary>
    [HttpGet("{id:int}/assignees")]
    public async Task<IActionResult> GetAssignees(int id)
    {
        var sprint = await FindSprintById(id);
        if (sprint == null)
            return NotFound(new { error = $"Sprint {id} no encontrado." });

        var project = new Models.JiraProject { Key = "unknown" };
        var report = await _closure.BuildAsync(project, sprint);
        return Ok(report.ByAssignee);
    }

    private async Task<Models.JiraSprint?> FindSprintById(int id)
    {
        // Retornamos un sprint sintético con el ID — los datos se cargan desde GetSprintIssuesDetailedAsync
        // Para obtener las fechas del sprint, buscamos en los proyectos
        var projects = await _jira.GetProjectsAsync();
        foreach (var p in projects)
        {
            try
            {
                var sprints = await _jira.GetSprintsByProjectAsync(p.Key);
                var match = sprints.FirstOrDefault(s => s.Id == id);
                if (match != null) return match;
            }
            catch { /* skip projects without boards */ }
        }
        return null;
    }
}
