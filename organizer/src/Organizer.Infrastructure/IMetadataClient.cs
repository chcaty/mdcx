using Organizer.Core;

namespace Organizer.Infrastructure;

/// <summary>Read-only metadata lookup. Construction never starts a query or network access.</summary>
public interface IMetadataClient : IAsyncDisposable
{
    Task<IReadOnlyList<MetadataCandidate>> MatchAsync(string query, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? sources = null);
}
