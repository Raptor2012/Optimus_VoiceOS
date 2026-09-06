namespace Optimus.Providers.Claude;

using Optimus.Providers.Desktop;

/// <summary>Desktop adapter for Claude Desktop.</summary>
public sealed class ClaudeDesktopAdapter : DesktopProviderAdapter
{
    /// <summary>Creates a Claude Desktop UI adapter.</summary>
    public ClaudeDesktopAdapter(IDesktopObserver? observer = null, IDesktopExecutor? executor = null)
        : base("claude", "Claude Desktop", ["claude", "Claude"], ["Claude"], observer, executor)
    {
    }
}
