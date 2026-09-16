using Microsoft.AspNetCore.Mvc;

namespace Mof.Http;

[ApiController]
[Route("api/unwrap")]
public sealed class UnwrapController(Unwrapper unwrapper) : ControllerBase
{
    /// <summary>Generate UV coordinates for an uploaded OBJ.</summary>
    /// <remarks>Send multipart/form-data with File and optional settings. The response contains the unwrapped OBJ as an attachment. Processing completes within this request; no jobs or polling are used.</remarks>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(byte[]), 200, "application/octet-stream")]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 413, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 422, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 500, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 503, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), 504, "application/problem+json")]
    public async Task<IActionResult> Unwrap([FromForm] UnwrapRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await unwrapper.UnwrapAsync(request, cancellationToken);
            return File(result, "application/octet-stream", "unwrapped.obj");
        }
        catch (UnwrapException exception)
        {
            if (exception.StatusCode == 503) Response.Headers.RetryAfter = "1";
            return Problem(statusCode: exception.StatusCode, title: exception.Message);
        }
    }
}
