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

    Task<AoConversationResponse?> GetSessionConversationAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<AoSendMessageResponse?> SendSessionMessageAsync(string sessionId, string message, string? clientMessageId = null, CancellationToken cancellationToken = default);

    Task<bool> ResolveApprovalAsync(string sessionId, string requestId, string decisionId, CancellationToken cancellationToken = default);

    Task<bool> InterruptAsync(string sessionId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AoCdcEvent> StreamEventsAsync(long? afterSeq = null, CancellationToken cancellationToken = default);
}
