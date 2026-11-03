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

public sealed record SwitchOrganizationRequest([Required] Guid OrganizationId);

public sealed record OrganizationDto(Guid Id, string Name, string Slug, string Role);
public sealed record AuthResponse(string AccessToken, int ExpiresIn, Guid UserId, string Email, OrganizationDto Organization);
public sealed record CurrentUserResponse(Guid UserId, string Email, OrganizationDto Organization);

/// <summary>Une session : le jeton d'accès (réponse JSON) et le jeton de renouvellement (cookie HttpOnly, jamais lisible par le script).</summary>
public sealed record SessionResult(AuthResponse Response, string RefreshToken, DateTimeOffset RefreshExpiresAt);

public sealed record AccountRecord(Guid UserId, string Email);
public sealed record MemberRecord(Guid UserId, string Email, OrganizationRole Role, DateTimeOffset JoinedAt);
public sealed record IssuedToken(string Value, int ExpiresInSeconds);

public enum RegistrationOutcome { Created, EmailTaken, InvalidPassword }
public sealed record RegistrationResult(RegistrationOutcome Outcome, AccountRecord? Account = null, IReadOnlyList<string>? Errors = null);

/// <summary>Refus d'une action d'équipe : l'appelant n'a pas le rôle nécessaire.</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>L'action contredit l'état actuel (déjà membre, dernier propriétaire…).</summary>
public sealed class ConflictException(string message) : Exception(message);

public sealed class PasswordRejectedException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public interface IAccountStore
{
    /// <summary>Crée l'utilisateur, l'organisation et l'adhésion dans une seule transaction.</summary>
    Task<RegistrationResult> CreateAsync(string email, string password, Organization organization, DateTimeOffset now, CancellationToken ct);

    /// <summary>Crée un utilisateur sans organisation (il en rejoint une par invitation).</summary>
    Task<RegistrationResult> CreateUserAsync(string email, string password, CancellationToken ct);

    /// <summary>Null si l'adresse ou le mot de passe est faux, ou le compte verrouillé (sans dire lequel des trois).</summary>
    Task<AccountRecord?> VerifyCredentialsAsync(string email, string password, CancellationToken ct);

    Task<AccountRecord?> FindAsync(Guid userId, CancellationToken ct);
    Task<bool> ExistsAsync(string email, CancellationToken ct);

    Task<(Organization Organization, Membership Membership)?> FindMembershipAsync(Guid userId, Guid? organizationId, CancellationToken ct);
    Task<IReadOnlyList<(Organization Organization, Membership Membership)>> ListMembershipsAsync(Guid userId, CancellationToken ct);
    Task<Organization?> FindOrganizationAsync(Guid organizationId, CancellationToken ct);

    Task<IReadOnlyList<MemberRecord>> ListMembersAsync(Guid organizationId, CancellationToken ct);
    Task<Membership?> FindMemberAsync(Guid organizationId, Guid userId, CancellationToken ct);
    Task<int> CountOwnersAsync(Guid organizationId, CancellationToken ct);
    void AddMembership(Membership membership);
    void RemoveMembership(Membership membership);

    Task SaveAsync(CancellationToken ct);
}

public interface ISessionStore
{
    void Add(RefreshToken token);
    Task<RefreshToken?> FindByHashAsync(string hash, CancellationToken ct);
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct);
    Task RevokeForMemberAsync(Guid userId, Guid organizationId, DateTimeOffset now, CancellationToken ct);
}

public interface IInvitationStore
{
    void Add(Invitation invitation);
    Task<Invitation?> FindByHashAsync(string hash, CancellationToken ct);
    Task<Invitation?> FindAsync(Guid organizationId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<Invitation>> ListPendingAsync(Guid organizationId, DateTimeOffset now, CancellationToken ct);
}

public interface ITokenIssuer
{
    IssuedToken Issue(AccountRecord account, Guid organizationId, OrganizationRole role);
}

public sealed class AccountService(IAccountStore accounts, ISessionStore sessions, ITokenIssuer tokens, TimeProvider clock)
{
    public async Task<RegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var organization = Organization.Create(request.OrganizationName, clock.GetUtcNow());
        return await accounts.CreateAsync(request.Email, request.Password, organization, clock.GetUtcNow(), ct);
    }

    public async Task<SessionResult?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var account = await accounts.VerifyCredentialsAsync(email, password, ct);
        return account is null ? null : await OpenSessionAsync(account, null, ct);
    }

    /// <summary>Sans organisation précisée : la première rejointe. Chaque ouverture démarre une nouvelle famille de jetons.</summary>
    public async Task<SessionResult?> OpenSessionAsync(AccountRecord account, Guid? organizationId, CancellationToken ct)
    {
        var found = await accounts.FindMembershipAsync(account.UserId, organizationId, ct);
        if (found is null) return null;

        var (organization, membership) = found.Value;
        var (refresh, value) = RefreshToken.Issue(account.UserId, organization.Id, clock.GetUtcNow());
        sessions.Add(refresh);
        await accounts.SaveAsync(ct);

        return Session(account, organization, membership, refresh, value);
    }

    /// <summary>
    /// Échange un jeton de renouvellement contre une nouvelle session. Un jeton déjà échangé révoque toute sa famille ;
    /// un utilisateur retiré de l'organisation n'obtient plus rien.
    /// </summary>
    public async Task<SessionResult?> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) return null;

        var now = clock.GetUtcNow();
        var current = await sessions.FindByHashAsync(SecretToken.Hash(refreshToken), ct);
        if (current is null) return null;

        if (!current.IsActive(now))
        {
            if (current.WasRotated)
            {
                await sessions.RevokeFamilyAsync(current.FamilyId, now, ct);
                await accounts.SaveAsync(ct);
            }
            return null;
        }

        var account = await accounts.FindAsync(current.UserId, ct);
        var found = account is null ? null : await accounts.FindMembershipAsync(current.UserId, current.OrganizationId, ct);
        if (account is null || found is null)
        {
            current.Revoke(now);
            await accounts.SaveAsync(ct);
            return null;
        }

        var (next, value) = current.Rotate(now);
        sessions.Add(next);
        await accounts.SaveAsync(ct);
        return Session(account, found.Value.Organization, found.Value.Membership, next, value);
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        var current = await sessions.FindByHashAsync(SecretToken.Hash(refreshToken), ct);
        if (current is null) return;

        await sessions.RevokeFamilyAsync(current.FamilyId, clock.GetUtcNow(), ct);
        await accounts.SaveAsync(ct);
    }

    public async Task<SessionResult?> SwitchAsync(Guid userId, Guid organizationId, string? refreshToken, CancellationToken ct)
    {
        var account = await accounts.FindAsync(userId, ct);
        if (account is null || await accounts.FindMembershipAsync(userId, organizationId, ct) is null) return null;

        await LogoutAsync(refreshToken, ct);
        return await OpenSessionAsync(account, organizationId, ct);
    }

    public async Task<IReadOnlyList<OrganizationDto>> ListOrganizationsAsync(Guid userId, CancellationToken ct) =>
        (await accounts.ListMembershipsAsync(userId, ct)).Select(m => ToDto(m.Organization, m.Membership)).ToList();

    public async Task<CurrentUserResponse?> GetCurrentAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        var account = await accounts.FindAsync(userId, ct);
        if (account is null) return null;

        var found = await accounts.FindMembershipAsync(userId, organizationId, ct);
        if (found is null) return null;

        var (organization, membership) = found.Value;
        return new CurrentUserResponse(account.UserId, account.Email, ToDto(organization, membership));
    }

    private SessionResult Session(AccountRecord account, Organization organization, Membership membership, RefreshToken refresh, string value)
    {
        var token = tokens.Issue(account, organization.Id, membership.Role);
        var response = new AuthResponse(token.Value, token.ExpiresInSeconds, account.UserId, account.Email, ToDto(organization, membership));
        return new SessionResult(response, value, refresh.ExpiresAt);
    }

    internal static OrganizationDto ToDto(Organization organization, Membership membership) =>
        new(organization.Id, organization.Name, organization.Slug, membership.Role.ToString());
}

public sealed record InviteRequest([Required, EmailAddress, MaxLength(254)] string Email, [Required] OrganizationRole Role);

/// <summary>Le secret du lien n'est renvoyé qu'ici : DevPulse n'envoie pas d'e-mail, l'invitant transmet le lien.</summary>
public sealed record InvitationCreated(Guid Id, string Email, string Role, DateTimeOffset ExpiresAt, string Token);
public sealed record InvitationResponse(Guid Id, string Email, string Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record InvitationPreview(string OrganizationName, string Email, string Role, bool AccountExists, DateTimeOffset ExpiresAt);
public sealed record InvitationTokenRequest([Required] string Token);
public sealed record AcceptInvitationRequest([Required] string Token, [Required, MaxLength(128)] string Password);
public sealed record MemberResponse(Guid UserId, string Email, string Role, DateTimeOffset JoinedAt);
public sealed record ChangeRoleRequest([Required] OrganizationRole Role);

/// <summary>Qui agit, d'après son jeton : son identifiant, son organisation et son rôle dans celle-ci.</summary>
public sealed record TeamActor(Guid UserId, Guid OrganizationId, OrganizationRole Role);

public sealed class TeamService(
    IAccountStore accounts, ISessionStore sessions, IInvitationStore invitations, AccountService sessionsService, TimeProvider clock)
{
    public async Task<IReadOnlyList<MemberResponse>> ListMembersAsync(Guid organizationId, CancellationToken ct) =>
        (await accounts.ListMembersAsync(organizationId, ct)).Select(m => new MemberResponse(m.UserId, m.Email, m.Role.ToString(), m.JoinedAt)).ToList();

    public async Task<IReadOnlyList<InvitationResponse>> ListInvitationsAsync(Guid organizationId, CancellationToken ct) =>
        (await invitations.ListPendingAsync(organizationId, clock.GetUtcNow(), ct))
            .Select(i => new InvitationResponse(i.Id, i.Email, i.Role.ToString(), i.CreatedAt, i.ExpiresAt)).ToList();

    public async Task<InvitationCreated> InviteAsync(TeamActor actor, InviteRequest request, CancellationToken ct)
    {
        if (!RolePolicy.CanAssign(actor.Role, request.Role)) throw new ForbiddenException("Votre rôle ne permet pas d'inviter avec ce rôle.");

        var email = Invitation.NormalizeEmail(request.Email);
        if ((await accounts.ListMembersAsync(actor.OrganizationId, ct)).Any(m => Invitation.NormalizeEmail(m.Email) == email))
            throw new ConflictException("Cette personne fait déjà partie de l'organisation.");

        // Une nouvelle invitation remplace les précédentes pour la même adresse : un seul lien valable à la fois.
        var now = clock.GetUtcNow();
        foreach (var previous in (await invitations.ListPendingAsync(actor.OrganizationId, now, ct)).Where(i => i.Email == email))
            previous.Revoke(now);

        var (invitation, token) = Invitation.Create(actor.OrganizationId, email, request.Role, actor.UserId, now);
        invitations.Add(invitation);
        await accounts.SaveAsync(ct);
        return new InvitationCreated(invitation.Id, invitation.Email, invitation.Role.ToString(), invitation.ExpiresAt, token);
    }

    public async Task<bool> RevokeInvitationAsync(Guid organizationId, Guid id, CancellationToken ct)
    {
        var invitation = await invitations.FindAsync(organizationId, id, ct);
        if (invitation is null) return false;
        invitation.Revoke(clock.GetUtcNow());
        await accounts.SaveAsync(ct);
        return true;
    }

    public async Task<InvitationPreview?> PreviewAsync(string token, CancellationToken ct)
    {
        var invitation = await invitations.FindByHashAsync(SecretToken.Hash(token), ct);
        if (invitation is null || !invitation.IsPending(clock.GetUtcNow())) return null;

        var organization = await accounts.FindOrganizationAsync(invitation.OrganizationId, ct);
        return organization is null ? null : new InvitationPreview(
            organization.Name, invitation.Email, invitation.Role.ToString(), await accounts.ExistsAsync(invitation.Email, ct), invitation.ExpiresAt);
    }

    /// <summary>
    /// Compte existant : son mot de passe prouve que la personne qui clique est bien le destinataire. Sinon, le compte
    /// est créé avec l'adresse de l'invitation, qui ne peut pas être changée en chemin.
    /// </summary>
    public async Task<SessionResult?> AcceptAsync(AcceptInvitationRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var invitation = await invitations.FindByHashAsync(SecretToken.Hash(request.Token), ct);
        if (invitation is null || !invitation.IsPending(now)) throw new DomainException("Cette invitation n'est plus valable.");

        AccountRecord? account;
        if (await accounts.ExistsAsync(invitation.Email, ct))
        {
            account = await accounts.VerifyCredentialsAsync(invitation.Email, request.Password, ct);
            if (account is null) return null;
        }
        else
        {
            var created = await accounts.CreateUserAsync(invitation.Email, request.Password, ct);
            account = created.Outcome switch
            {
                RegistrationOutcome.Created => created.Account,
                RegistrationOutcome.InvalidPassword => throw new PasswordRejectedException(created.Errors ?? []),
                _ => throw new ConflictException("Un compte vient d'être créé avec cette adresse : connectez-vous puis réessayez."),
            };
        }

        if (await accounts.FindMemberAsync(invitation.OrganizationId, account!.UserId, ct) is null)
            accounts.AddMembership(Membership.Create(account.UserId, invitation.OrganizationId, invitation.Role, now));
        invitation.Accept(now);
        await accounts.SaveAsync(ct);

        return await sessionsService.OpenSessionAsync(account, invitation.OrganizationId, ct);
    }

    public async Task<MemberResponse?> ChangeRoleAsync(TeamActor actor, Guid userId, OrganizationRole role, CancellationToken ct)
    {
        var member = await accounts.FindMemberAsync(actor.OrganizationId, userId, ct);
        if (member is null) return null;

        if (!RolePolicy.CanAssign(actor.Role, role) || !RolePolicy.CanAssign(actor.Role, member.Role))
            throw new ForbiddenException("Votre rôle ne permet pas ce changement.");
        if (member.Role == OrganizationRole.Owner && role != OrganizationRole.Owner && await accounts.CountOwnersAsync(actor.OrganizationId, ct) == 1)
            throw new ConflictException("L'organisation doit garder au moins un propriétaire.");

        member.ChangeRole(role);
        // Le nouveau rôle s'applique au prochain renouvellement : on force une reconnexion de ses sessions actuelles.
        await sessions.RevokeForMemberAsync(userId, actor.OrganizationId, clock.GetUtcNow(), ct);
        await accounts.SaveAsync(ct);

        var record = (await accounts.ListMembersAsync(actor.OrganizationId, ct)).Single(m => m.UserId == userId);
        return new MemberResponse(record.UserId, record.Email, record.Role.ToString(), record.JoinedAt);
    }

    public async Task<bool> RemoveAsync(TeamActor actor, Guid userId, CancellationToken ct)
    {
        var member = await accounts.FindMemberAsync(actor.OrganizationId, userId, ct);
        if (member is null) return false;

        var leaving = userId == actor.UserId;
        if (!leaving && !RolePolicy.CanAssign(actor.Role, member.Role)) throw new ForbiddenException("Votre rôle ne permet pas de retirer ce membre.");
        if (member.Role == OrganizationRole.Owner && await accounts.CountOwnersAsync(actor.OrganizationId, ct) == 1)
            throw new ConflictException("L'organisation doit garder au moins un propriétaire.");

        accounts.RemoveMembership(member);
        await sessions.RevokeForMemberAsync(userId, actor.OrganizationId, clock.GetUtcNow(), ct);
        await accounts.SaveAsync(ct);
        return true;
    }
}
