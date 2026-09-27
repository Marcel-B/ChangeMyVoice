using System.Net;
using System.Net.Http.Json;
using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Application.Ports;
using Shouldly;

namespace ChangeMyVoice.Api.IntegrationTests;

public class ModelTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Der_Zustand_nennt_Ladezeitpunkt_und_Entladezeitpunkt()
    {
        var since = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        factory.ModelHost.State = new InferenceModelState(
            true, false, TimeSpan.FromMinutes(5), since, since.AddMinutes(5));

        var state = await factory.CreateAuthenticatedClient()
            .GetFromJsonAsync<ModelStateResponse>("/api/v1/model");

        state!.Loaded.ShouldBeTrue();
        state.Busy.ShouldBeFalse();
        state.KeepLoadedSeconds.ShouldBe(300);
        state.LoadedSinceUtc.ShouldBe(since);
        state.UnloadAtUtc.ShouldBe(since.AddMinutes(5));
    }

    [Theory]
    [InlineData(ModelReleaseResult.Released)]
    [InlineData(ModelReleaseResult.NotLoaded)]
    public async Task Entladen_antwortet_ohne_Inhalt(ModelReleaseResult result)
    {
        factory.ModelHost.ReleaseResult = result;
        var before = factory.ModelHost.Releases;

        var response = await factory.CreateAuthenticatedClient().DeleteAsync("/api/v1/model");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.ModelHost.Releases.ShouldBe(before + 1);
    }

    [Fact]
    public async Task Waehrend_eines_Laufs_wird_das_Entladen_abgelehnt()
    {
        factory.ModelHost.ReleaseResult = ModelReleaseResult.Busy;

        var response = await factory.CreateAuthenticatedClient().DeleteAsync("/api/v1/model");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("MODEL_BUSY");
    }

    [Fact]
    public async Task Ohne_Schluessel_bleibt_das_Modell_verschlossen()
    {
        var response = await factory.CreateClient().DeleteAsync("/api/v1/model");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
