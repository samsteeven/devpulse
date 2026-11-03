using DevPulse.Identity.Application;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevPulse.Identity.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AccountService accounts) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await accounts.RegisterAsync(request, ct);
        switch (result.Outcome)
        {
            case RegistrationOutcome.EmailTaken:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Cette adresse e-mail est déjà utilisée.");
            case RegistrationOutcome.InvalidPassword:
                ModelState.AddModelError(nameof(request.Password), string.Join(" ", result.Errors ?? []));
                return ValidationProblem(ModelState);
        }

        var session = await accounts.OpenSessionAsync(result.Account!, ct);
        return StatusCode(StatusCodes.Status201Created, session);
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var session = await accounts.LoginAsync(request.Email, request.Password, ct);
        return session is null
            ? Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Adresse e-mail ou mot de passe incorrect.")
            : Ok(session);
    }

    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var current = await accounts.GetCurrentAsync(User.GetUserId(), User.GetOrganizationId(), ct);
        return current is null ? Unauthorized() : Ok(current);
    }
}
