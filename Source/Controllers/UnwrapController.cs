using Microsoft.AspNetCore.Mvc;
using Mof.Http;

namespace Mof.Http.Controllers;

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
        if (!ModelState.IsValid)
        {
            // Form reading happens during binding; an over-limit multipart body lands here
            // as a ModelState error instead of reaching the action parameters. Report it as
            // 413 with the same wording as the application-level upload check.
            if (ModelState.Values.SelectMany(state => state.Errors)
                .Any(error => error.ErrorMessage.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase)))
                return Problem(statusCode: 413, title: "The OBJ exceeds the configured upload limit.");
            return ValidationProblem();
        }
        try
        {
            var result = await unwrapper.UnwrapAsync(request, cancellationToken);
            Response.OnCompleted(() =>
            {
                result.Dispose();
                return Task.CompletedTask;
            });
            return PhysicalFile(result.ContentPath, "application/octet-stream", "unwrapped.obj");
        }
        catch (UnwrapException exception)
        {
            if (exception.StatusCode == 503) Response.Headers.RetryAfter = "1";
            return Problem(statusCode: exception.StatusCode, title: exception.Message);
        }
    }
}
