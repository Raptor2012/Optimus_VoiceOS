namespace Optimus.Providers;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers.Ao;
using Optimus.Providers.Windows;

/// <summary>
/// The set of destinations this build can send to, and their bindings.
/// Supports both local Windows applications and Agent Orchestrator project/session targets.
/// </summary>
public sealed class DestinationRegistry
{
    private readonly List<IDestinationAdapter> _adapters;
    private readonly IAoClient? _aoClient;

    public DestinationRegistry(IAoClient? aoClient = null)
        : this(CreateDefaultAdapters(), aoClient)
    {
    }

    public DestinationRegistry(IEnumerable<IDestinationAdapter> adapters, IAoClient? aoClient = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToList();
        _aoClient = aoClient;

        if (_adapters.Count == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(adapters));
        }
    }

    public ReadOnlyCollection<IDestinationAdapter> Adapters
    {
        get
        {
            lock (_adapters)
            {
                return _adapters.ToList().AsReadOnly();
            }
        }
    }

    private static IDestinationAdapter[] CreateDefaultAdapters() =>
        new IDestinationAdapter[]
        {
            new WindowsAppAdapter("claude", "Claude", "claude"),
            new WindowsAppAdapter("antigravity", "Antigravity", "Antigravity"),
            new WindowsAppAdapter("codex", "Codex (ChatGPT app)", "ChatGPT")
        };

    public void Register(IDestinationAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        lock (_adapters)
        {
            int idx = _adapters.FindIndex(a => string.Equals(a.DestinationId, adapter.DestinationId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _adapters[idx] = adapter;
            }
            else
            {
                _adapters.Add(adapter);
            }
        }
    }

    public bool Unregister(string destinationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        lock (_adapters)
        {
            int removed = _adapters.RemoveAll(a => string.Equals(a.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        }
    }

    public IDestinationAdapter? Find(string destinationId)
    {
        if (string.IsNullOrWhiteSpace(destinationId))
        {
            return null;
        }

        lock (_adapters)
        {
            var found = _adapters.FirstOrDefault(a =>
                string.Equals(a.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                return found;
            }

            // On-demand creation for AO targets if AoClient is available
            if (_aoClient != null && destinationId.StartsWith("ao:", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = destinationId.Split(':');
                if (parts.Length >= 2)
                {
                    string projId = parts[1];
                    string? sessId = parts.Length >= 3 ? parts[2] : null;
                    var aoAdapter = new AoDestinationAdapter(_aoClient, projId, sessId);
                    _adapters.Add(aoAdapter);
                    return aoAdapter;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Discovers AO projects and registers them as first-class destinations.
    /// </summary>
    public async Task RefreshAoDestinationsAsync(CancellationToken cancellationToken = default)
    {
        if (_aoClient == null)
        {
            return;
        }

        try
        {
            var projects = await _aoClient.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
            var projectMap = projects.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var proj in projects)
            {
                string displayName = $"AO: {proj.Name}";
                var adapter = new AoDestinationAdapter(_aoClient, proj.Id, displayName: displayName);
                Register(adapter);
            }

            var sessions = await _aoClient.GetSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var sess in sessions)
            {
                if (sess.IsTerminated)
                {
                    continue;
                }

                string projName = projectMap.TryGetValue(sess.ProjectId, out string? name) ? name : sess.ProjectId;
                string label = !string.IsNullOrWhiteSpace(sess.DisplayName) ? sess.DisplayName : sess.Id;
                string displayName = $"AO · {projName} ({label})";
                var adapter = new AoDestinationAdapter(_aoClient, sess.ProjectId, sess.Id, displayName);
                Register(adapter);
            }
        }
        catch
        {
            // Soft failure if AO daemon is not currently up
        }
    }

    /// <summary>Probes every destination. Used to refresh the picker.</summary>
    public IReadOnlyList<(IDestinationAdapter Adapter, DestinationStatus Status)> ProbeAll()
    {
        List<IDestinationAdapter> current;
        lock (_adapters)
        {
            current = _adapters.ToList();
        }

        return current.Select(a => (a, a.Probe())).ToList();
    }
}
