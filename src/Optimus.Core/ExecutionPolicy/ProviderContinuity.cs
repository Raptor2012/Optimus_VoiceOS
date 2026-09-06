namespace Optimus.Core.ExecutionPolicy;

using System.Text.Json;

public interface IProviderTurn
{
    ExecutionProvider Provider { get; }

    Task StopAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record ProviderHandoffResult(bool Succeeded, ExecutionProvider From, ExecutionProvider To, string Detail);

/// <summary>
/// Transfers work at a safe boundary. The stop is awaited before the replacement can write.
/// </summary>
public sealed class ProviderContinuityManager
{
    public static async Task<ProviderHandoffResult> HandoffAsync(
        IProviderTurn outgoing,
        IProviderTurn replacement,
        HandoffCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (outgoing.Provider == replacement.Provider)
            return new(false, outgoing.Provider, replacement.Provider, "A handoff requires a different provider.");

        try
        {
            await outgoing.StopAsync(cancellationToken).ConfigureAwait(false);
            string message = JsonSerializer.Serialize(new
            {
                type = "execution_checkpoint",
                changes = checkpoint.Changes,
                decisions = checkpoint.Decisions,
                tests = checkpoint.Tests,
                blockers = checkpoint.Blockers,
                nextAction = checkpoint.NextAction
            });
            await replacement.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return new(true, outgoing.Provider, replacement.Provider, "Outgoing turn stopped before checkpoint write.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(false, outgoing.Provider, replacement.Provider, exception.Message);
        }
    }
}
