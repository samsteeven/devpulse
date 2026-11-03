using System.Security.Claims;
using System.Security.Cryptography;
using DevPulse.Identity.Application;
using DevPulse.Identity.Domain;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace DevPulse.Identity.Infrastructure;

public sealed class AppUser : IdentityUser<Guid>;

public sealed class IdentityDb(DbContextOptions<IdentityDb> options) : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        model.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.Property(o => o.Name).HasMaxLength(100);
            e.Property(o => o.Slug).HasMaxLength(120);
            e.HasIndex(o => o.Slug).IsUnique();
        });

        model.Entity<Membership>(e =>
        {
            e.ToTable("memberships");
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(m => new { m.UserId, m.OrganizationId }).IsUnique();
            e.HasIndex(m => m.OrganizationId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Organization>().WithMany().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.FamilyId);
            e.HasIndex(t => new { t.UserId, t.OrganizationId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Invitation>(e =>
        {
            e.ToTable("invitations");
            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.Email).HasMaxLength(254);
            e.Property(i => i.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.TokenHash).HasMaxLength(64);
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasIndex(i => new { i.OrganizationId, i.Email });
            e.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Pour « dotnet ef » : aucune connexion réelle ni secret n'est nécessaire pour générer une migration.</summary>
public sealed class IdentityDbDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDb>
{
    public IdentityDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<IdentityDb>()
        .UseNpgsql("Host=design-time").UseSnakeCaseNamingConvention().Options);
}

public sealed class AccountStore(
    IdentityDb db,
    UserManager<AppUser> users,
    IPasswordHasher<AppUser> hasher) : IAccountStore
{
    // Calculé une fois : une adresse inconnue coûte le même temps de hachage qu'un mot de passe faux.
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString());

    public async Task<RegistrationResult> CreateAsync(string email, string password, Organization organization, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, password);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Deux inscriptions simultanées : le contrôle d'UserManager ne voit pas l'autre, l'index unique tranche.
            return new RegistrationResult(RegistrationOutcome.EmailTaken);
        }

        if (!created.Succeeded)
        {
            var codes = created.Errors.Select(e => e.Code).ToList();
            return codes.Contains(nameof(IdentityErrorDescriber.DuplicateEmail)) || codes.Contains(nameof(IdentityErrorDescriber.DuplicateUserName))
                ? new RegistrationResult(RegistrationOutcome.EmailTaken)
                : new RegistrationResult(RegistrationOutcome.InvalidPassword, Errors: created.Errors.Select(e => e.Description).ToList());
        }

        db.Organizations.Add(organization);
        db.Memberships.Add(Membership.Create(user.Id, organization.Id, OrganizationRole.Owner, now));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new RegistrationResult(RegistrationOutcome.Created, new AccountRecord(user.Id, email));
    }

    public async Task<RegistrationResult> CreateUserAsync(string email, string password, CancellationToken ct)
    {
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, password);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new RegistrationResult(RegistrationOutcome.EmailTaken);
        }

        if (created.Succeeded) return new RegistrationResult(RegistrationOutcome.Created, new AccountRecord(user.Id, email));

        var codes = created.Errors.Select(e => e.Code).ToList();
        return codes.Contains(nameof(IdentityErrorDescriber.DuplicateEmail)) || codes.Contains(nameof(IdentityErrorDescriber.DuplicateUserName))
            ? new RegistrationResult(RegistrationOutcome.EmailTaken)
            : new RegistrationResult(RegistrationOutcome.InvalidPassword, Errors: created.Errors.Select(e => e.Description).ToList());
    }

    public async Task<bool> ExistsAsync(string email, CancellationToken ct) => await users.FindByEmailAsync(email) is not null;

    public async Task<AccountRecord?> VerifyCredentialsAsync(string email, string password, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            hasher.VerifyHashedPassword(new AppUser(), DummyHash, password);
            return null;
        }

        if (await users.IsLockedOutAsync(user)) return null;

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return null;
        }

        await users.ResetAccessFailedCountAsync(user);
        return new AccountRecord(user.Id, user.Email!);
    }

    public async Task<AccountRecord?> FindAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null ? null : new AccountRecord(user.Id, user.Email!);
    }

    public async Task<(Organization Organization, Membership Membership)?> FindMembershipAsync(Guid userId, Guid? organizationId, CancellationToken ct)
    {
        var row = await (
            from membership in db.Memberships.AsNoTracking()
            join organization in db.Organizations.AsNoTracking() on membership.OrganizationId equals organization.Id
            where membership.UserId == userId && (organizationId == null || membership.OrganizationId == organizationId)
            orderby membership.CreatedAt
            select new { organization, membership }).FirstOrDefaultAsync(ct);

        return row is null ? null : (row.organization, row.membership);
    }

    public async Task<IReadOnlyList<(Organization Organization, Membership Membership)>> ListMembershipsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await (
            from membership in db.Memberships.AsNoTracking()
            join organization in db.Organizations.AsNoTracking() on membership.OrganizationId equals organization.Id
            where membership.UserId == userId
            orderby organization.Name
            select new { organization, membership }).ToListAsync(ct);
        return rows.Select(r => (r.organization, r.membership)).ToList();
    }

    public Task<Organization?> FindOrganizationAsync(Guid organizationId, CancellationToken ct) =>
        db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == organizationId, ct);

    public async Task<IReadOnlyList<MemberRecord>> ListMembersAsync(Guid organizationId, CancellationToken ct) =>
        await (
            from membership in db.Memberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId
            orderby membership.CreatedAt
            select new MemberRecord(user.Id, user.Email!, membership.Role, membership.CreatedAt)).ToListAsync(ct);

    public Task<Membership?> FindMemberAsync(Guid organizationId, Guid userId, CancellationToken ct) =>
        db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct);

    public Task<int> CountOwnersAsync(Guid organizationId, CancellationToken ct) =>
        db.Memberships.CountAsync(m => m.OrganizationId == organizationId && m.Role == OrganizationRole.Owner, ct);

    public void AddMembership(Membership membership) => db.Memberships.Add(membership);

    public void RemoveMembership(Membership membership) => db.Memberships.Remove(membership);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class SessionStore(IdentityDb db) : ISessionStore
{
    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public Task<RefreshToken?> FindByHashAsync(string hash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var token in await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct))
            token.Revoke(now);
    }

    public async Task RevokeForMemberAsync(Guid userId, Guid organizationId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.OrganizationId == organizationId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in active) token.Revoke(now);
    }
}

public sealed class InvitationStore(IdentityDb db) : IInvitationStore
{
    public void Add(Invitation invitation) => db.Invitations.Add(invitation);

    public Task<Invitation?> FindByHashAsync(string hash, CancellationToken ct) =>
        db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);

    public Task<Invitation?> FindAsync(Guid organizationId, Guid id, CancellationToken ct) =>
        db.Invitations.FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Id == id, ct);

    public async Task<IReadOnlyList<Invitation>> ListPendingAsync(Guid organizationId, DateTimeOffset now, CancellationToken ct) =>
        await db.Invitations
            .Where(i => i.OrganizationId == organizationId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
}

public sealed class RsaTokenIssuer : ITokenIssuer
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;
    private readonly TimeProvider _clock;

    public RsaTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;

        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(_options.PrivateKey), out _);
        _credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
    }

    public IssuedToken Issue(AccountRecord account, Guid organizationId, OrganizationRole role)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(_options.AccessTokenMinutes);

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(
            [
                new Claim(DevPulseClaims.Subject, account.UserId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, account.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(DevPulseClaims.OrganizationId, organizationId.ToString()),
                new Claim(DevPulseClaims.OrganizationRole, role.ToString()),
            ]),
        });

        return new IssuedToken(token, (int)lifetime.TotalSeconds);
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<IdentityDb>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Database"))
            .UseSnakeCaseNamingConvention());

        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<IdentityDb>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITokenIssuer, RsaTokenIssuer>();
        services.AddScoped<IAccountStore, AccountStore>();
        services.AddScoped<ISessionStore, SessionStore>();
        services.AddScoped<IInvitationStore, InvitationStore>();
        services.AddScoped<AccountService>();
        services.AddScoped<TeamService>();
        return services;
    }
}
