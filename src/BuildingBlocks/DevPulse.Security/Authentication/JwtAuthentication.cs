using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DevPulse.Security.Authentication;

public static class DevPulseClaims
{
    public const string Subject = "sub";
    public const string OrganizationId = "org_id";
    public const string OrganizationRole = "org_role";
}

public static class OrganizationRoles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string DevOps = "DevOps";
    public const string Developer = "Developer";
    public const string Viewer = "Viewer";
}

public static class Policies
{
    public const string ManageServices = nameof(ManageServices);
    public const string ManageIncidents = nameof(ManageIncidents);
    public const string ManageAlerts = nameof(ManageAlerts);
    public const string ManageMembers = nameof(ManageMembers);
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "devpulse-identity";
    public string Audience { get; set; } = "devpulse-api";

    /// <summary>Clé publique RSA (SubjectPublicKeyInfo, base64). Tous les services la connaissent.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Clé privée RSA (PKCS#8, base64). Seul Identity la reçoit : un autre service ne peut pas forger de jeton.</summary>
    public string PrivateKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;
}

public static class JwtAuthenticationExtensions
{
    /// <summary>
    /// Validation des jetons et autorisation par défaut. Toute route exige un jeton rattaché à une organisation,
    /// sauf <c>[AllowAnonymous]</c> explicite : un contrôleur oublié reste fermé.
    /// </summary>
    public static IServiceCollection AddDevPulseAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.PublicKey), "Jwt:PublicKey est obligatoire.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(jwt.Value.PublicKey), out _);

                bearer.MapInboundClaims = false;

                // Un navigateur ne peut pas poser d'en-tête Authorization sur une connexion WebSocket : pour les hubs
                // SignalR, et seulement pour eux, le jeton est lu dans la requête (access_token).
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"].ToString();
                        if (token.Length > 0 && context.HttpContext.Request.Path.StartsWithSegments("/hubs")) context.Token = token;
                        return Task.CompletedTask;
                    },
                };
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = new RsaSecurityKey(rsa),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = DevPulseClaims.Subject,
                    RoleClaimType = DevPulseClaims.OrganizationRole,
                };
            });

        var requireOrganization = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(DevPulseClaims.OrganizationId)
            .Build();

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(requireOrganization)
            .SetFallbackPolicy(requireOrganization)
            .AddPolicy(Policies.ManageServices, policy => policy
                .Combine(requireOrganization)
                .RequireClaim(DevPulseClaims.OrganizationRole, OrganizationRoles.Owner, OrganizationRoles.Admin, OrganizationRoles.DevOps))
            .AddPolicy(Policies.ManageAlerts, policy => policy
                .Combine(requireOrganization)
                .RequireClaim(DevPulseClaims.OrganizationRole, OrganizationRoles.Owner, OrganizationRoles.Admin, OrganizationRoles.DevOps))
            // Un développeur d'astreinte doit pouvoir prendre en charge un incident ; un observateur, non.
            .AddPolicy(Policies.ManageIncidents, policy => policy
                .Combine(requireOrganization)
                .RequireClaim(DevPulseClaims.OrganizationRole,
                    OrganizationRoles.Owner, OrganizationRoles.Admin, OrganizationRoles.DevOps, OrganizationRoles.Developer))
            .AddPolicy(Policies.ManageMembers, policy => policy
                .Combine(requireOrganization)
                .RequireClaim(DevPulseClaims.OrganizationRole, OrganizationRoles.Owner, OrganizationRoles.Admin));

        return services;
    }

    public static Guid GetOrganizationId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(DevPulseClaims.OrganizationId)
            ?? throw new InvalidOperationException("Jeton sans organisation."));

    public static string? GetEmail(this ClaimsPrincipal user) => user.FindFirstValue("email");

    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(DevPulseClaims.Subject)
            ?? throw new InvalidOperationException("Jeton sans sujet."));
}
