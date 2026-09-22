using System.Reflection;
using ChangeMyVoice.Domain.Jobs;
using Shouldly;

namespace ChangeMyVoice.Domain.Tests;

/// <summary>
/// Sichert die Abhängigkeitsrichtung der hexagonalen Architektur ab.
/// </summary>
/// <remarks>
/// Die Regel „die Domäne kennt keine Infrastruktur“ steht sonst nur in der
/// CLAUDE.md und erodiert mit der Zeit unbemerkt. Hier bricht stattdessen der
/// Build, sobald jemand versehentlich ein Infrastrukturpaket hereinzieht.
/// </remarks>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(ConversionJob).Assembly;

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Microsoft.Data",
        "Swashbuckle",
        "Dapper",
        "Serilog",
        "Yarp",
        "System.Data",
    ];

    [Fact]
    public void Die_Domaene_referenziert_keine_Infrastrukturpakete()
    {
        var referenced = Domain.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        referenced.ShouldBeEmpty(
            "Die Domäne muss frei von Infrastruktur-Abhängigkeiten bleiben, "
            + "gefunden: " + string.Join(", ", referenced));
    }

    [Fact]
    public void Die_Domaene_verwendet_weder_Dateisystem_noch_Netzwerk_noch_Prozesse()
    {
        // Zugriff auf Dateien, Sockets oder Prozesse gehört in einen Adapter.
        // Taucht er hier auf, ist Technik in die Fachlogik gewandert.
        var forbiddenTypes = new[]
        {
            typeof(System.IO.File),
            typeof(System.IO.Directory),
            typeof(System.Diagnostics.Process),
            typeof(System.Net.Http.HttpClient),
        };

        var offenders = new List<string>();

        foreach (var type in Domain.GetTypes())
        {
            foreach (var field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static))
            {
                if (forbiddenTypes.Contains(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name}");
                }
            }

            foreach (var property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static))
            {
                if (forbiddenTypes.Contains(property.PropertyType))
                {
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "Technik gehört in einen Adapter, gefunden: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Die_Domaene_haengt_nur_an_der_Basisklassenbibliothek()
    {
        var referenced = Domain.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        referenced.ShouldAllBe(name =>
            name.StartsWith("System", StringComparison.Ordinal) ||
            name == "netstandard" ||
            name == "mscorlib");
    }
}
