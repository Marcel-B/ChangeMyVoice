using ChangeMyVoice.Api.Security;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ChangeMyVoice.Api.OpenApi;

/// <summary>
/// Trägt das Schlüsselverfahren in die Schnittstellenbeschreibung ein.
/// </summary>
/// <remarks>
/// Ohne diesen Eintrag gäbe es in der Swagger-Oberfläche keine Möglichkeit, den
/// Zugangsschlüssel zu hinterlegen — die Dokumentation wäre vorhanden, aber
/// nicht benutzbar.
/// </remarks>
internal sealed class ApiKeySecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[ApiKeyAuthenticationOptions.SchemeName] =
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyAuthenticationOptions.HeaderName,
                Description =
                    "Der Zugangsschlüssel. Anfragen werden ausschließlich über das Gateway "
                    + "angenommen, das diesen Schlüssel setzt.",
            };

        document.Info.Title = "ChangeMyVoice";
        document.Info.Version = "v1";
        document.Info.Description =
            "Schnittstelle für Singing Voice Conversion: Eine Gesangsaufnahme behält ihre "
            + "Melodie, Phrasierung und Darbietung, übernimmt aber das Timbre einer "
            + "gespeicherten Referenzstimme. Die Verarbeitung läuft im Hintergrund — ein "
            + "Auftrag wird angenommen, sein Fortschritt abgefragt und das Ergebnis "
            + "anschließend heruntergeladen.";

        return Task.CompletedTask;
    }
}

/// <summary>
/// Ergänzt die geschützten Endpunkte um ihre Zugangsanforderung und die
/// Antworten, die bei fehlender Berechtigung entstehen.
/// </summary>
internal sealed class ApiKeyOperationTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var requiresAuthorization = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any();

        if (!requiresAuthorization)
        {
            return Task.CompletedTask;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationOptions.SchemeName)] = [],
            },
        ];

        operation.Responses ??= new OpenApiResponses();

        operation.Responses.TryAdd("401", new OpenApiResponse
        {
            Description = "Der Zugangsschlüssel fehlt oder ist ungültig.",
        });

        operation.Responses.TryAdd("403", new OpenApiResponse
        {
            Description = "Die Anfrage kam nicht von einer zugelassenen Adresse.",
        });

        return Task.CompletedTask;
    }
}
