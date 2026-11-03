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
        services.AddScoped<AccountService>();
        return services;
    }
}
