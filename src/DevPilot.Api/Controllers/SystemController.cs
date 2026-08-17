using DevPilot.Api.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace DevPilot.Api.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    [HttpGet("info")]
    public ActionResult<SystemInfoResponse> GetInfo()
    {
        return Ok(new SystemInfoResponse("DevPilot", "Running", DateTime.UtcNow));
    }
}
