namespace Optimus.Providers;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Optimus.Providers.Windows;

/// <summary>
/// The fixed set of destinations this build can send to, and their bindings.
/// </summary>
/// <remarks>
/// Deliberately a fixed list rather than a discovery mechanism or plugin surface. Three known
/// applications is the whole requirement; anything more general would be scope this project has
/// explicitly ruled out.
/// </remarks>
public sealed class DestinationRegistry
{
    private readonly List<IDestinationAdapter> _adapters;

    public DestinationRegistry()
        : this(CreateDefaultAdapters())
    {
    }

    public DestinationRegistry(IEnumerable<IDestinationAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToList();

        if (_adapters.Count == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(adapters));
        }
    }

    public ReadOnlyCollection<IDestinationAdapter> Adapters => _adapters.AsReadOnly();

    /// <summary>
    /// The three configured Windows targets, verified against the live machine.
    /// </summary>
    /// <remarks>
    /// <c>Codex</c> targets the ChatGPT desktop application, which hosts both the ChatGPT and
    /// Codex workspaces in the same window and switches between them with an in-app selector.
    /// Nothing in the window's identity reflects that choice, so the adapter cannot verify which
    /// workspace is active; the user binds the window and is responsible for having Codex
    /// selected in it. This is stated rather than papered over, because a silent wrong-workspace
    /// send is precisely the failure the destination rules exist to prevent.
    /// </remarks>
    private static IDestinationAdapter[] CreateDefaultAdapters() =>
        new IDestinationAdapter[]
        {
            new WindowsAppAdapter("claude", "Claude", "claude"),
            new WindowsAppAdapter("antigravity", "Antigravity", "Antigravity"),
            new WindowsAppAdapter("codex", "Codex (ChatGPT app)", "ChatGPT")
        };

    public IDestinationAdapter? Find(string destinationId) =>
        _adapters.FirstOrDefault(a =>
            string.Equals(a.DestinationId, destinationId, StringComparison.Ordinal));

    /// <summary>Probes every destination. Used to refresh the picker.</summary>
    public IReadOnlyList<(IDestinationAdapter Adapter, DestinationStatus Status)> ProbeAll() =>
        _adapters.Select(a => (a, a.Probe())).ToList();
}
