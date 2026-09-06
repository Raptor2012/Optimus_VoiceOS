namespace Optimus.Providers;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Optimus.Providers.Desktop;

/// <summary>
/// Describes the part of a provider window that is useful to the conversation coordinator.
/// </summary>
public sealed record ProviderState(
    bool IsAvailable,
    string? ActiveConversation,
    string? LastMessage,
    bool InputReady,
    string? WindowTitle = null,
    string? LayoutHash = null,
    IntPtr WindowHandle = default,
    IReadOnlyList<AccessibleControlInfo>? AccessibleControls = null,
    DateTimeOffset? ObservedAtUtc = null,
    string? Detail = null)
{
    /// <summary>Gets the active conversation identifier or visible conversation title.</summary>
    public string? ActiveConversationId => ActiveConversation;

    /// <summary>Gets the most recent provider response.</summary>
    public string? LastResponse => LastMessage;

    /// <summary>Gets whether the provider can accept a composed message.</summary>
    public bool CanAcceptInput => InputReady;

    /// <summary>Gets the observed controls, or an empty collection when the application is unavailable.</summary>
    public IReadOnlyList<AccessibleControlInfo> Controls => AccessibleControls ?? Array.Empty<AccessibleControlInfo>();

    /// <summary>Creates an unavailable state without throwing when an application is not running.</summary>
    public static ProviderState Unavailable(string detail) =>
        new(false, null, null, false, Detail: detail, ObservedAtUtc: DateTimeOffset.UtcNow);
}

/// <summary>
/// UI adapter used by the conversation coordinator to communicate with one desktop provider.
/// </summary>
public interface IProviderAdapter
{
    /// <summary>Checks whether the provider application has a visible eligible window.</summary>
    Task<bool> IsAvailable();

    /// <summary>Inspects the provider window and returns its current conversation state.</summary>
    Task<ProviderState> Observe();

    /// <summary>Composes and sends a message through the provider UI.</summary>
    Task SendMessage(string text);

    /// <summary>Reads the most recent visible provider response.</summary>
    Task<string> ReadLastResponse();

    /// <summary>Verifies that the most recently requested message is visible in the provider.</summary>
    Task<bool> VerifySent();
}
