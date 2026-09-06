namespace Optimus.Providers.Codex;

using Optimus.Providers.Desktop;

/// <summary>Desktop adapter for the Codex (Luna 5.6 High) application.</summary>
public sealed class CodexAdapter : DesktopProviderAdapter
{
    /// <summary>Creates a Codex UI adapter.</summary>
    public CodexAdapter(IDesktopObserver? observer = null, IDesktopExecutor? executor = null)
        : base(
            "codex",
            "Codex",
            ["codex", "ChatGPT", "chatgpt"],
            ["Codex", "ChatGPT"],
            observer,
            executor)
    {
    }
}
