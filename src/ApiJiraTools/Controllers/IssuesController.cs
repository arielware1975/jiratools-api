using ApiJiraTools.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApiJiraTools.Controllers;

[ApiController]
[Route("api/issues")]
public class IssuesController : ControllerBase
{
    private readonly JiraService _jira;
    private readonly IssueTreeService _tree;

    public IssuesController(JiraService jira, IssueTreeService tree)
    {
        _jira = jira;
        _tree = tree;
    }

    /// <summary>Contexto completo de un issue: idea → épica → issue → hijos, links, blockers.</summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> GetIssueTree(string key)
    {
        var report = await _tree.BuildAsync(key.ToUpperInvariant());
        return Ok(report);
    }

    /// <summary>Hijos de una épica.</summary>
    [HttpGet("{key}/children")]
    public async Task<IActionResult> GetChildren(string key)
    {
        var children = await _jira.GetEpicChildIssuesAsync(key.ToUpperInvariant());
        return Ok(children);
    }

    /// <summary>Issues creados recientemente en un proyecto.</summary>
    [HttpGet("recent")]
    public async Task<IActionResult> GetRecentIssues([FromQuery] string project, [FromQuery] int days = 7)
    {
        if (string.IsNullOrWhiteSpace(project))
            return BadRequest(new { error = "Parámetro 'project' requerido." });

        string jql = $"project = \"{project.ToUpperInvariant()}\" AND created >= -{days}d ORDER BY created DESC";
        var issues = await _jira.SearchIssuesByJqlAsync(jql);
        return Ok(issues);
    }
}
