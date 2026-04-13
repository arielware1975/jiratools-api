using ApiJiraTools.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApiJiraTools.Controllers;

[ApiController]
[Route("api/discovery")]
public class DiscoveryController : ControllerBase
{
    private readonly JiraService _jira;

    public DiscoveryController(JiraService jira)
    {
        _jira = jira;
    }

    /// <summary>Ideas de un proyecto discovery filtradas por roadmap (Ahora/Siguiente). Sin filtro devuelve todas.</summary>
    [HttpGet("{projectKey}/ideas")]
    public async Task<IActionResult> GetIdeas(string projectKey, [FromQuery] string? roadmap = null)
    {
        var ideas = await _jira.GetDiscoveryIdeasByRoadmapAsync(projectKey.ToUpperInvariant(), roadmap);

        var result = ideas.Select(i => new
        {
            key = i.Key,
            summary = i.Fields?.Summary,
            status = i.Fields?.Status?.Name,
            assignee = i.Fields?.Assignee?.DisplayName,
            roadmap = _jira.GetDiscoveryRoadmapValue(i)
        });

        return Ok(result);
    }
}
