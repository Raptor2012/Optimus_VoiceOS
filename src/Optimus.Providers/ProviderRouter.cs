namespace Optimus.Providers;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Optimus.Providers.Antigravity;
using Optimus.Providers.Ao;
using Optimus.Providers.Claude;
using Optimus.Providers.Codex;
using Optimus.Providers.Desktop;

/// <summary>Context resolved by the conversation coordinator before a provider operation.</summary>
public sealed record ProviderContext(
    string? Provider,
    string? ConversationId = null,
    string? ProjectId = null)
{
    /// <summary>Alias for the provider name used by callers that call it an application.</summary>
    public string? Application => Provider;
}

/// <summary>
/// Routes coordinator operations to one explicitly selected provider adapter.
/// </summary>
public sealed class ProviderRouter
{
    private readonly Dictionary<string, IProviderAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a router with the built-in desktop provider adapters.</summary>
    public ProviderRouter()
        : this(new IProviderAdapter[]
        {
            new CodexAdapter(),
            new ClaudeDesktopAdapter(),
            new AntigravityAdapter(),
            new AoDesktopAdapter(new AoClient())
        })
    {
    }

    /// <summary>Creates a router from provider adapters, keyed by their known provider type.</summary>
    public ProviderRouter(IEnumerable<IProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        foreach (IProviderAdapter adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            Register(InferProviderId(adapter), adapter);
        }
    }

    /// <summary>Creates a router from explicit provider routing keys.</summary>
    public ProviderRouter(IReadOnlyDictionary<string, IProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        foreach ((string key, IProviderAdapter adapter) in adapters)
        {
            Register(key, adapter);
        }
    }

    /// <summary>Gets a snapshot of the registered provider adapters.</summary>
    public ReadOnlyDictionary<string, IProviderAdapter> Adapters =>
        new(new Dictionary<string, IProviderAdapter>(_adapters, StringComparer.OrdinalIgnoreCase));

    /// <summary>Registers or replaces an adapter under a provider routing key.</summary>
    public void Register(string provider, IProviderAdapter adapter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters[NormalizeProvider(provider)] = adapter;
    }

    /// <summary>Resolves a provider by name without silently selecting an unrelated application.</summary>
    public IProviderAdapter Resolve(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (!_adapters.TryGetValue(NormalizeProvider(provider), out IProviderAdapter? adapter))
        {
            throw new KeyNotFoundException($"No provider adapter is registered for '{provider}'.");
        }

        return adapter;
    }

    /// <summary>Resolves the provider and verifies a requested conversation when one is supplied.</summary>
    public async Task<IProviderAdapter> ResolveAsync(ProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IProviderAdapter adapter;
        if (!string.IsNullOrWhiteSpace(context.Provider))
        {
            adapter = Resolve(context.Provider);
        }
        else if (!string.IsNullOrWhiteSpace(context.ConversationId))
        {
            var matches = new List<IProviderAdapter>();
            foreach (IProviderAdapter candidate in _adapters.Values.Distinct())
            {
                ProviderState state = await candidate.Observe().ConfigureAwait(false);
                if (string.Equals(state.ActiveConversationId, context.ConversationId, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    matches.Count == 0
                        ? $"No provider owns conversation '{context.ConversationId}'."
                        : $"Conversation '{context.ConversationId}' is ambiguous across providers.");
            }

            adapter = matches[0];
        }
        else
        {
            throw new InvalidOperationException("A provider or conversation context is required.");
        }

        if (!string.IsNullOrWhiteSpace(context.ConversationId))
        {
            ProviderState state = await adapter.Observe().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(state.ActiveConversationId) &&
                !string.Equals(state.ActiveConversationId, context.ConversationId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Provider '{context.Provider ?? "selected"}' is not on conversation '{context.ConversationId}'.");
            }
        }

        return adapter;
    }

    /// <summary>Routes a send operation after resolving its provider and conversation context.</summary>
    public async Task SendMessageAsync(ProviderContext context, string text)
    {
        IProviderAdapter adapter = await ResolveAsync(context).ConfigureAwait(false);
        await adapter.SendMessage(text).ConfigureAwait(false);
    }

    /// <summary>Routes observation after resolving its provider and conversation context.</summary>
    public async Task<ProviderState> ObserveAsync(ProviderContext context) =>
        await (await ResolveAsync(context).ConfigureAwait(false)).Observe().ConfigureAwait(false);

    /// <summary>Routes response reading after resolving its provider and conversation context.</summary>
    public async Task<string> ReadLastResponseAsync(ProviderContext context) =>
        await (await ResolveAsync(context).ConfigureAwait(false)).ReadLastResponse().ConfigureAwait(false);

    /// <summary>Routes send verification after resolving its provider and conversation context.</summary>
    public async Task<bool> VerifySentAsync(ProviderContext context) =>
        await (await ResolveAsync(context).ConfigureAwait(false)).VerifySent().ConfigureAwait(false);

    private static string InferProviderId(IProviderAdapter adapter) =>
        adapter is DesktopProviderAdapter desktop
            ? desktop.ProviderId
            : adapter.GetType().Name.Replace("Adapter", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeProvider(string provider)
    {
        string key = provider.Trim().Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return key.ToLowerInvariant() switch
        {
            "chatgpt" => "codex",
            "claudedesktop" => "claude",
            "gemini" => "antigravity",
            "agentorchestrator" => "ao",
            _ => key.ToLowerInvariant()
        };
    }
}
