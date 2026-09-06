namespace Optimus.Providers.Ao;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface IAoClient
{
    string BaseUrl { get; }

    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AoProject>> GetProjectsAsync(CancellationToken cancellationToken = default);

    Task<AoProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AoSession>> GetSessionsAsync(string? projectId = null, CancellationToken cancellationToken = default);

    Task<AoSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AoConversationMessage>> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<bool> SendSessionMessageAsync(string sessionId, string message, CancellationToken cancellationToken = default);
}
