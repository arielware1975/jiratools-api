using ApiJiraTools.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApiJiraTools.Controllers;

[ApiController]
[Route("api/release")]
public class ReleaseController : ControllerBase
{
    private readonly JiraService _jira;
    private readonly ReleaseAuditService _release;

    public ReleaseController(JiraService jira, ReleaseAuditService release)
    {
        _jira = jira;
        _release = release;
    }

    /// <summary>Release audit: ideas → épicas → issues con alertas de trazabilidad.</summary>
    [HttpGet("audit")]
    public async Task<IActionResult> GetReleaseAudit(
        [FromQuery] string project,
        [FromQuery] string discovery)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(discovery))
            return BadRequest(new { error = "Parámetros 'project' y 'discovery' requeridos." });

        var sprints = await _jira.GetSprintsByProjectAsync(project.ToUpperInvariant());
        var active = sprints.FirstOrDefault(s => s.State.Equals("active", StringComparison.OrdinalIgnoreCase));
        if (active == null)
            return NotFound(new { error = $"No hay sprint activo en {project.ToUpperInvariant()}." });

        var report = await _release.BuildAsync(
            project.ToUpperInvariant(),
            discovery.ToUpperInvariant(),
            active.Id,
            active.Name,
            active.StartDate,
            active.EndDate);

        return Ok(report);
    }
}
