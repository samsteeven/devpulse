using System.Security.Cryptography;
using System.Text;

namespace DevPulse.Identity.Domain;

/// <summary>
/// Secret opaque remis une seule fois (cookie, lien d'invitation). Seule son empreinte SHA-256 est conservée :
/// une fuite de la base ne donne aucun jeton utilisable. 32 octets aléatoires rendent toute recherche exhaustive vaine,
/// d'où une empreinte simple et non un hachage lent comme pour un mot de passe.
/// </summary>
public static class SecretToken
{
    public static (string Value, string Hash) Generate()
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (value, Hash(value));
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// Jeton de renouvellement. Chaque usage le remplace par un nouveau de la même famille. Présenter un jeton déjà
/// remplacé signifie qu'une copie circule : toute la famille est révoquée, le voleur comme l'utilisateur doivent se reconnecter.
/// </summary>
public sealed class RefreshToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    private RefreshToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? ReplacedById { get; private set; }

    public static (RefreshToken Token, string Value) Issue(Guid userId, Guid organizationId, DateTimeOffset now, Guid? familyId = null)
    {
        var (value, hash) = SecretToken.Generate();
        return (new RefreshToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            OrganizationId = organizationId,
            FamilyId = familyId ?? Guid.NewGuid(),
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        }, value);
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    /// <summary>Jeton déjà échangé contre un autre : signe d'une copie.</summary>
    public bool WasRotated => ReplacedById is not null;

    public (RefreshToken Token, string Value) Rotate(DateTimeOffset now)
    {
        var next = Issue(UserId, OrganizationId, now, FamilyId);
        RevokedAt = now;
        ReplacedById = next.Token.Id;
        return next;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

/// <summary>Invitation nominative, à usage unique, valable 7 jours. Le lien porte le secret ; la base, son empreinte.</summary>
public sealed class Invitation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private Invitation() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Email { get; private set; } = "";
    public OrganizationRole Role { get; private set; }
    public string TokenHash { get; private set; } = "";
    public Guid InvitedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static (Invitation Invitation, string Token) Create(Guid organizationId, string email, OrganizationRole role, Guid invitedBy, DateTimeOffset now)
    {
        var (value, hash) = SecretToken.Generate();
        return (new Invitation
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Email = NormalizeEmail(email),
            Role = role,
            TokenHash = hash,
            InvitedBy = invitedBy,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        }, value);
    }

    public bool IsPending(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;

    public void Accept(DateTimeOffset now)
    {
        if (!IsPending(now)) throw new DomainException("Cette invitation n'est plus valable.");
        AcceptedAt = now;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public static class RolePolicy
{
    /// <summary>Un administrateur gère l'équipe mais ne crée ni ne destitue de propriétaire.</summary>
    public static bool CanAssign(OrganizationRole actor, OrganizationRole target) => actor switch
    {
        OrganizationRole.Owner => true,
        OrganizationRole.Admin => target != OrganizationRole.Owner,
        _ => false,
    };
}
