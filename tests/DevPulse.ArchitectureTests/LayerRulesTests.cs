using System.Reflection;
using NetArchTest.Rules;

namespace DevPulse.ArchitectureTests;

/// <summary>Les règles de dépendance du blueprint, vérifiées sur les assemblages compilés.</summary>
public class LayerRulesTests
{
    public static readonly TheoryData<string> Services = new() { "Identity", "Registry", "Monitoring" };

    private static readonly string[] AllServices = ["Identity", "Registry", "Monitoring"];

    // Forcer le chargement : une référence de projet non utilisée dans le code de test ne charge pas l'assemblage.
    private static readonly Assembly[] Loaded =
    [
        typeof(Identity.Infrastructure.IdentityDb).Assembly,
        typeof(Registry.Infrastructure.RegistryDb).Assembly,
        typeof(Monitoring.Infrastructure.MonitoringDb).Assembly,
    ];

    private static Assembly Layer(string service, string layer)
    {
        _ = Loaded;
        return Assembly.Load($"DevPulse.{service}.{layer}");
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} dépend de {string.Join(", ", forbidden)} : {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Le_domaine_ne_depend_d_aucune_couche_technique(string service) =>
        AssertNoDependency(Layer(service, "Domain"),
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "RabbitMQ", "System.Net.Http",
            $"DevPulse.{service}.Application", $"DevPulse.{service}.Infrastructure", "DevPulse.Contracts", "DevPulse.Security");

    [Theory]
    [MemberData(nameof(Services))]
    public void L_application_ne_connait_ni_la_persistance_ni_le_transport(string service) =>
        AssertNoDependency(Layer(service, "Application"),
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "RabbitMQ",
            $"DevPulse.{service}.Infrastructure", $"DevPulse.{service}.Api");

    [Theory]
    [MemberData(nameof(Services))]
    public void Un_service_ne_depend_jamais_du_code_d_un_autre_service(string service)
    {
        var others = AllServices.Where(s => s != service).Select(s => $"DevPulse.{s}").ToArray();
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
            AssertNoDependency(Layer(service, layer), others);
    }

    [Fact]
    public void Les_contrats_restent_des_contrats_de_transport()
    {
        var contracts = typeof(DevPulse.Contracts.Internal.ActiveServiceDto).Assembly;
        var references = contracts.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore") || r.StartsWith("Microsoft.AspNetCore") || r.StartsWith("DevPulse."));
    }
}
