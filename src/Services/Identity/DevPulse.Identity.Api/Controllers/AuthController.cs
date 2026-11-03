using DevPulse.Identity.Application;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DevPulse.Identity.Api.Controllers;

public sealed class AuthCookieOptions
{
    public const string SectionName = "Auth";

    /// <summary>Vrai partout ; les navigateurs acceptent un cookie Secure sur http://localhost.</summary>
    public bool SecureCookie { get; set; } = true;
}

/// <summary>
/// Le jeton de renouvellement vit dans un cookie HttpOnly (illisible par un script injecté), SameSite=Strict et limité
/// à /api/auth : il n'accompagne aucune autre requête. Les routes qui l'utilisent exigent en plus un en-tête que
/// seul un script de la même origine peut poser.
/// </summary>
public static class RefreshCookie
{
    public const string Name = "devpulse_refresh";
    public const string RequiredHeader = "X-Requested-With";

    public static void Write(HttpResponse response, SessionResult session, bool secure) =>
        response.Cookies.Append(Name, session.RefreshToken, Options(secure, session.RefreshExpiresAt));

    public static void Clear(HttpResponse response, bool secure) => response.Cookies.Delete(Name, Options(secure, null));

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static bool HasRequiredHeader(HttpRequest request) => request.Headers[RequiredHeader] == "DevPulse";

    private static CookieOptions Options(bool secure, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true,
    };
}

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AccountService accounts, TeamService team, IOptions<AuthCookieOptions> cookie) : ControllerBase
{
    private bool Secure => cookie.Value.SecureCookie;

    private OkObjectResult Session(SessionResult session)
    {
        RefreshCookie.Write(Response, session, Secure);
        return Ok(session.Response);
    }

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

        var session = await accounts.OpenSessionAsync(result.Account!, null, ct);
        RefreshCookie.Write(Response, session!, Secure);
        return StatusCode(StatusCodes.Status201Created, session!.Response);
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
            : Session(session);
    }

    /// <summary>Nouveau jeton d'accès à partir du cookie, qui est remplacé au passage (rotation).</summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!RefreshCookie.HasRequiredHeader(Request)) return Problem(statusCode: StatusCodes.Status400BadRequest, title: "En-tête X-Requested-With manquant.");

        var session = await accounts.RefreshAsync(RefreshCookie.Read(Request), ct);
        if (session is not null) return Session(session);

        RefreshCookie.Clear(Response, Secure);
        return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Session expirée.");
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (!RefreshCookie.HasRequiredHeader(Request)) return Problem(statusCode: StatusCodes.Status400BadRequest, title: "En-tête X-Requested-With manquant.");

        await accounts.LogoutAsync(RefreshCookie.Read(Request), ct);
        RefreshCookie.Clear(Response, Secure);
        return NoContent();
    }

    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var current = await accounts.GetCurrentAsync(User.GetUserId(), User.GetOrganizationId(), ct);
        return current is null ? Unauthorized() : Ok(current);
    }

    [HttpGet("organizations")]
    public Task<IReadOnlyList<OrganizationDto>> Organizations(CancellationToken ct) => accounts.ListOrganizationsAsync(User.GetUserId(), ct);

    /// <summary>Change d'organisation : nouvelle session, l'ancienne famille de jetons est révoquée.</summary>
    [HttpPost("switch")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Switch(SwitchOrganizationRequest request, CancellationToken ct)
    {
        var session = await accounts.SwitchAsync(User.GetUserId(), request.OrganizationId, RefreshCookie.Read(Request), ct);
        return session is null ? NotFound() : Session(session);
    }

    [AllowAnonymous]
    [HttpPost("invitations/preview")]
    [ProducesResponseType<InvitationPreview>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewInvitation(InvitationTokenRequest request, CancellationToken ct) =>
        await team.PreviewAsync(request.Token, ct) is { } preview ? Ok(preview) : NotFound();

    [AllowAnonymous]
    [HttpPost("invitations/accept")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AcceptInvitation(AcceptInvitationRequest request, CancellationToken ct)
    {
        var session = await team.AcceptAsync(request, ct);
        return session is null
            ? Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Mot de passe incorrect pour ce compte.")
            : Session(session);
    }
}

[ApiController]
[Route("api/organization")]
public sealed class OrganizationController(TeamService team) : ControllerBase
{
    private TeamActor Actor => new(User.GetUserId(), User.GetOrganizationId(),
        Enum.Parse<Domain.OrganizationRole>(User.FindFirst(DevPulseClaims.OrganizationRole)!.Value));

    [HttpGet("members")]
    public Task<IReadOnlyList<MemberResponse>> Members(CancellationToken ct) => team.ListMembersAsync(User.GetOrganizationId(), ct);

    [Authorize(Policies.ManageMembers)]
    [HttpPut("members/{userId:guid}")]
    public async Task<IActionResult> ChangeRole(Guid userId, ChangeRoleRequest request, CancellationToken ct) =>
        await team.ChangeRoleAsync(Actor, userId, request.Role, ct) is { } member ? Ok(member) : NotFound();

    [Authorize(Policies.ManageMembers)]
    [HttpDelete("members/{userId:guid}")]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken ct) =>
        await team.RemoveAsync(Actor, userId, ct) ? NoContent() : NotFound();

    [Authorize(Policies.ManageMembers)]
    [HttpGet("invitations")]
    public Task<IReadOnlyList<InvitationResponse>> Invitations(CancellationToken ct) => team.ListInvitationsAsync(User.GetOrganizationId(), ct);

    [Authorize(Policies.ManageMembers)]
    [HttpPost("invitations")]
    [ProducesResponseType<InvitationCreated>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Invite(InviteRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await team.InviteAsync(Actor, request, ct));

    [Authorize(Policies.ManageMembers)]
    [HttpDelete("invitations/{id:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid id, CancellationToken ct) =>
        await team.RevokeInvitationAsync(User.GetOrganizationId(), id, ct) ? NoContent() : NotFound();
}
