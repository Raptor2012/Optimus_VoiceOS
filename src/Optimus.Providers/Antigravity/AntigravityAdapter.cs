namespace Optimus.Providers.Antigravity;

using Optimus.Providers.Desktop;

/// <summary>Desktop adapter for Antigravity (Gemini Flash 3.8 High).</summary>
public sealed class AntigravityAdapter : DesktopProviderAdapter
{
    /// <summary>Creates an Antigravity UI adapter.</summary>
    public AntigravityAdapter(IDesktopObserver? observer = null, IDesktopExecutor? executor = null)
        : base(
            "antigravity",
            "Antigravity",
            ["Antigravity", "antigravity"],
            ["Antigravity"],
            observer,
            executor)
    {
    }
}
