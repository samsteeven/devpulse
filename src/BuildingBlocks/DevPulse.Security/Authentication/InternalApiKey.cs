using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevPulse.Security.Authentication;

public sealed class InternalApiOptions
{
    public const string SectionName = "InternalApi";
    public const string HeaderName = "X-Internal-Key";

    public string Key { get; set; } = "";
}

/// <summary>Protège une route entre services. Sans clé configurée, tout appel est refusé.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInternalKeyAttribute : Attribute, IAuthorizationFilter, IAllowAnonymous
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<InternalApiOptions>>().Value;
        var provided = context.HttpContext.Request.Headers[InternalApiOptions.HeaderName].ToString();

        if (string.IsNullOrEmpty(options.Key) || !FixedTimeEquals(provided, options.Key))
            context.Result = new UnauthorizedResult();
    }

    // Les empreintes ont toujours la même longueur : la comparaison ne révèle ni le contenu ni la taille de la clé.
    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(left)),
            SHA256.HashData(Encoding.UTF8.GetBytes(right)));
}
