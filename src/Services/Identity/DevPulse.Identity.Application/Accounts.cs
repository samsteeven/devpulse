using System.ComponentModel.DataAnnotations;
using DevPulse.Identity.Domain;

namespace DevPulse.Identity.Application;

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, MinLength(12), MaxLength(128)] string Password,
    [Required, MinLength(2), MaxLength(100)] string OrganizationName);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record OrganizationDto(Guid Id, string Name, string Slug, string Role);
public sealed record AuthResponse(string AccessToken, int ExpiresIn, Guid UserId, string Email, OrganizationDto Organization);
public sealed record CurrentUserResponse(Guid UserId, string Email, OrganizationDto Organization);

public sealed record AccountRecord(Guid UserId, string Email);
public sealed record IssuedToken(string Value, int ExpiresInSeconds);

public enum RegistrationOutcome { Created, EmailTaken, InvalidPassword }
public sealed record RegistrationResult(RegistrationOutcome Outcome, AccountRecord? Account = null, IReadOnlyList<string>? Errors = null);

public interface IAccountStore
{
    /// <summary>Crée l'utilisateur, l'organisation et l'adhésion dans une seule transaction.</summary>
    Task<RegistrationResult> CreateAsync(string email, string password, Organization organization, DateTimeOffset now, CancellationToken ct);

    /// <summary>Null si l'adresse ou le mot de passe est faux, ou le compte verrouillé (sans dire lequel des trois).</summary>
    Task<AccountRecord?> VerifyCredentialsAsync(string email, string password, CancellationToken ct);

    Task<AccountRecord?> FindAsync(Guid userId, CancellationToken ct);

    Task<(Organization Organization, Membership Membership)?> FindMembershipAsync(Guid userId, Guid? organizationId, CancellationToken ct);
}

public interface ITokenIssuer
{
    IssuedToken Issue(AccountRecord account, Guid organizationId, OrganizationRole role);
}

public sealed class AccountService(IAccountStore store, ITokenIssuer tokens, TimeProvider clock)
{
    public async Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var organization = Organization.Create(request.OrganizationName, clock.GetUtcNow());
        return await store.CreateAsync(request.Email, request.Password, organization, clock.GetUtcNow(), ct);
    }

    public async Task<AuthResponse?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var account = await store.VerifyCredentialsAsync(email, password, ct);
        return account is null ? null : await OpenSessionAsync(account, ct);
    }

    /// <summary>Le jeton est rattaché à la première organisation de l'utilisateur (le changement d'organisation viendra avec les invitations).</summary>
    public async Task<AuthResponse?> OpenSessionAsync(AccountRecord account, CancellationToken ct)
    {
        var found = await store.FindMembershipAsync(account.UserId, null, ct);
        if (found is null) return null;

        var (organization, membership) = found.Value;
        var token = tokens.Issue(account, organization.Id, membership.Role);
        return new AuthResponse(token.Value, token.ExpiresInSeconds, account.UserId, account.Email, ToDto(organization, membership));
    }

    public async Task<CurrentUserResponse?> GetCurrentAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        var account = await store.FindAsync(userId, ct);
        if (account is null) return null;

        var found = await store.FindMembershipAsync(userId, organizationId, ct);
        if (found is null) return null;

        var (organization, membership) = found.Value;
        return new CurrentUserResponse(account.UserId, account.Email, ToDto(organization, membership));
    }

    private static OrganizationDto ToDto(Organization organization, Membership membership) =>
        new(organization.Id, organization.Name, organization.Slug, membership.Role.ToString());
}
