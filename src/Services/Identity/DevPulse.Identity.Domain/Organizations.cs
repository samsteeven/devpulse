using System.Globalization;
using System.Text;

namespace DevPulse.Identity.Domain;

public sealed class DomainException(string message) : Exception(message);

public enum OrganizationRole
{
    Owner,
    Admin,
    DevOps,
    Developer,
    Viewer,
}

public sealed class Organization
{
    private Organization() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static Organization Create(string name, DateTimeOffset now)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 2 or > 100) throw new DomainException("Le nom de l'organisation doit faire de 2 à 100 caractères.");

        var slug = ToSlug(name);
        if (slug.Length == 0) throw new DomainException("Le nom de l'organisation doit contenir des lettres ou des chiffres.");

        // Suffixe aléatoire : deux organisations peuvent porter le même nom sans collision.
        var id = Guid.NewGuid();
        return new Organization { Id = id, Name = name, Slug = $"{slug}-{id.ToString("N")[..6]}", CreatedAt = now };
    }

    private static string ToSlug(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        return slug.ToString().Trim('-');
    }
}

/// <summary>Un utilisateur peut appartenir à plusieurs organisations, avec un rôle dans chacune.</summary>
public sealed class Membership
{
    private Membership() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public OrganizationRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Membership Create(Guid userId, Guid organizationId, OrganizationRole role, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), UserId = userId, OrganizationId = organizationId, Role = role, CreatedAt = now };

    public void ChangeRole(OrganizationRole role) => Role = role;
}
