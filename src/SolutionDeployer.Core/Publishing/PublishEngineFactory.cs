using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

public interface IPublishEngineFactory
{
    IPublishEngine Get(PublishEngineKind kind);
}

public sealed class PublishEngineFactory(IEnumerable<IPublishEngine> engines) : IPublishEngineFactory
{
    private readonly Dictionary<PublishEngineKind, IPublishEngine> _engines =
        engines.ToDictionary(e => e.Kind);

    public IPublishEngine Get(PublishEngineKind kind) =>
        _engines.TryGetValue(kind, out var engine)
            ? engine
            : throw new InvalidOperationException($"No publish engine registered for {kind}.");
}
