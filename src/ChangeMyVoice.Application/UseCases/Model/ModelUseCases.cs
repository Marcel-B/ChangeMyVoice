using ChangeMyVoice.Application.Ports;

namespace ChangeMyVoice.Application.UseCases.Model;

/// <summary>Meldet, ob das Modell geladen ist.</summary>
public interface IGetModelState
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    InferenceModelState Execute();
}

/// <inheritdoc />
public sealed class GetModelState(IInferenceModelHost host) : IGetModelState
{
    /// <inheritdoc />
    public InferenceModelState Execute() => host.GetState();
}

/// <summary>Entlädt das Modell, damit ein anderes Programm den Speicher bekommt.</summary>
public interface IReleaseModel
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<ModelReleaseResult> ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ReleaseModel(IInferenceModelHost host) : IReleaseModel
{
    /// <inheritdoc />
    public Task<ModelReleaseResult> ExecuteAsync(CancellationToken cancellationToken = default) =>
        host.ReleaseAsync(cancellationToken);
}
