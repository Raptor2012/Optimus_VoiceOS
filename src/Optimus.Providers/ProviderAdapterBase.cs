namespace Optimus.Providers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Optimus.Providers.Desktop;
using Optimus.Providers.Windows;

/// <summary>
/// Shared UI Automation workflow for provider-specific desktop adapters.
/// </summary>
public abstract class DesktopProviderAdapter : IProviderAdapter
{
    private readonly HashSet<string> _processNames;
    private readonly IReadOnlyList<string> _titleKeywords;
    private string? _lastSentText;
    private bool _lastSendSucceeded;

    /// <summary>Initializes a provider adapter with its window matching rules.</summary>
    protected DesktopProviderAdapter(
        string providerId,
        string displayName,
        IEnumerable<string> processNames,
        IEnumerable<string> titleKeywords,
        IDesktopObserver? observer = null,
        IDesktopExecutor? executor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(processNames);
        ArgumentNullException.ThrowIfNull(titleKeywords);

        ProviderId = providerId;
        DisplayName = displayName;
        _processNames = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        _titleKeywords = titleKeywords.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        Observer = observer ?? new DesktopObserver();
        Executor = executor ?? new DesktopExecutor(Observer);
    }

    /// <summary>Gets the stable routing key for this provider.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the user-facing provider name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the observer used to inspect the provider window.</summary>
    protected IDesktopObserver Observer { get; }

    /// <summary>Gets the executor used for focus, typing, clicks, and send shortcuts.</summary>
    protected IDesktopExecutor Executor { get; }

    /// <inheritdoc />
    public virtual Task<bool> IsAvailable() => Task.FromResult(FindWindow() != null);

    /// <inheritdoc />
    public virtual Task<ProviderState> Observe()
    {
        WindowInfo? window = FindWindow();
        if (window == null)
        {
            return Task.FromResult(ProviderState.Unavailable($"{DisplayName} is not running."));
        }

        try
        {
            ObservationSnapshot observation = Observer.ObserveWindow(window.Hwnd);
            if (!observation.IsValid)
            {
                return Task.FromResult(ProviderState.Unavailable($"{DisplayName} window is no longer available."));
            }

            return Task.FromResult(BuildState(window, observation));
        }
        catch (Exception ex) when (IsTransientDesktopException(ex))
        {
            return Task.FromResult(ProviderState.Unavailable($"Could not inspect {DisplayName}: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public virtual Task SendMessage(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        bool sent = TrySendMessageThroughUi(text);
        RecordSend(text, sent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task<string> ReadLastResponse()
    {
        WindowInfo? window = FindWindow();
        if (window == null)
        {
            return Task.FromResult(string.Empty);
        }

        try
        {
            ObservationSnapshot observation = Observer.ObserveWindow(window.Hwnd);
            string response = FindLastResponse(window.Hwnd, observation.Accessibility);
            return Task.FromResult(response);
        }
        catch (Exception ex) when (IsTransientDesktopException(ex))
        {
            return Task.FromResult(string.Empty);
        }
    }

    /// <inheritdoc />
    public virtual Task<bool> VerifySent()
    {
        if (!_lastSendSucceeded || string.IsNullOrWhiteSpace(_lastSentText))
        {
            return Task.FromResult(false);
        }

        WindowInfo? window = FindWindow();
        if (window == null)
        {
            return Task.FromResult(false);
        }

        try
        {
            string expected = Normalize(_lastSentText);
            ObservationSnapshot observation = Observer.ObserveWindow(window.Hwnd);
            bool visible = observation.Accessibility.Elements.Any(element =>
                Normalize(element.CurrentValue ?? element.Name).Contains(expected, StringComparison.OrdinalIgnoreCase));

            if (!visible)
            {
                visible = Observer.ReadContent(window.Hwnd).Any(node =>
                    Normalize(node.Text).Contains(expected, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(visible);
        }
        catch (Exception ex) when (IsTransientDesktopException(ex))
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>Finds the best visible top-level window for this provider.</summary>
    protected WindowInfo? FindWindow()
    {
        try
        {
            IReadOnlyList<WindowInfo> windows = Observer.ListWindows();
            return windows
                .Where(IsEligibleWindow)
                .OrderByDescending(window => window.IsForeground)
                .ThenByDescending(window => !string.IsNullOrWhiteSpace(window.Title))
                .FirstOrDefault();
        }
        catch (Exception ex) when (IsTransientDesktopException(ex))
        {
            return null;
        }
    }

    /// <summary>Builds a state from one strictly window-scoped observation.</summary>
    protected virtual ProviderState BuildState(WindowInfo window, ObservationSnapshot observation)
    {
        AccessibleControlInfo? input = FindInput(observation.Accessibility.Elements);
        string? conversation = FindConversation(observation.Accessibility.Elements, window.Title);
        string lastMessage = FindLastResponse(window.Hwnd, observation.Accessibility);

        return new ProviderState(
            IsAvailable: true,
            ActiveConversation: conversation,
            LastMessage: string.IsNullOrWhiteSpace(lastMessage) ? null : lastMessage,
            InputReady: input is { IsEnabled: true, IsOffscreen: false },
            WindowTitle: window.Title,
            LayoutHash: observation.LayoutHash,
            WindowHandle: window.Hwnd,
            AccessibleControls: observation.Accessibility.Elements,
            ObservedAtUtc: observation.ObservedAtUtc,
            Detail: $"Observed {observation.Accessibility.Elements.Count} accessible controls.");
    }

    /// <summary>Attempts to send through the observed composer without throwing on UI obstacles.</summary>
    protected bool TrySendMessageThroughUi(string text)
    {
        WindowInfo? window = FindWindow();
        if (window == null)
        {
            return false;
        }

        try
        {
            ObservationSnapshot observation = Observer.ObserveWindow(window.Hwnd);
            if (!observation.IsValid)
            {
                return false;
            }

            AccessibleControlInfo? input = FindInput(observation.Accessibility.Elements);
            ActionResult focused = Executor.FocusWindow(window.Hwnd, observation.ObservedAtUtc);
            if (!focused.Success)
            {
                return false;
            }

            ActionResult entered = Executor.EnterText(
                text,
                window.Hwnd,
                input?.Id,
                preserveExisting: false,
                TextEntryMode.Auto,
                observation.ObservedAtUtc);
            if (!entered.Success)
            {
                return false;
            }

            ActionResult submitted = Executor.PressShortcut("ENTER", window.Hwnd, observation.ObservedAtUtc);
            return submitted.Success;
        }
        catch (Exception ex) when (IsTransientDesktopException(ex))
        {
            return false;
        }
    }

    /// <summary>Records the last send attempt for verification.</summary>
    protected void RecordSend(string text, bool succeeded)
    {
        _lastSentText = text;
        _lastSendSucceeded = succeeded;
    }

    /// <summary>Gets the last requested message, for adapters that use a provider API.</summary>
    protected string? LastSentText => _lastSentText;

    /// <summary>Sets the result of a provider-API send attempt.</summary>
    protected void RecordProviderSend(string text, bool succeeded) => RecordSend(text, succeeded);

    /// <summary>Returns a response found in visible text or accessibility content.</summary>
    protected string FindLastResponse(IntPtr hwnd, WindowAccessibilitySnapshot accessibility)
    {
        try
        {
            IReadOnlyList<VisibleTextNode> nodes = Observer.ReadContent(hwnd);
            string? visible = nodes
                .Reverse()
                .Where(node => !IsNavigationText(node.Text, node.Name, node.AutomationId))
                .Select(node => Normalize(node.Text))
                .FirstOrDefault(text => text.Length > 0 && !IsComposerText(text) && !IsLastSentText(text));
            if (!string.IsNullOrWhiteSpace(visible))
            {
                return visible;
            }
        }
        catch (Exception ex) when (IsTransientDesktopException(ex))
        {
            // Fall back to the already captured accessibility tree.
        }

        return accessibility.Elements
            .Reverse()
            .Where(element => !IsInput(element) && !IsNavigationText(element.Name, element.Id, element.ControlType))
            .Select(element => Normalize(element.CurrentValue ?? element.Name))
            .FirstOrDefault(text => text.Length > 0 && !IsComposerText(text) && !IsLastSentText(text)) ?? string.Empty;
    }

    /// <summary>Invokes one matching accessible control, used by AO for sidebar navigation.</summary>
    protected bool TryInvokeControl(WindowInfo window, WindowAccessibilitySnapshot accessibility, params string[] terms)
    {
        AccessibleControlInfo? control = accessibility.Elements
            .Where(element => element.IsEnabled && !element.IsOffscreen)
            .OrderByDescending(element => Score(element, terms))
            .FirstOrDefault(element => Score(element, terms) > 0);
        return control != null && Executor.InvokeAccessibleElement(
            window.Hwnd,
            elementId: control.Id,
            observationTimestamp: accessibility.CapturedAtUtc).Success;
    }

    private bool IsEligibleWindow(WindowInfo window)
    {
        if (!window.IsVisible || window.IsIconic)
        {
            return false;
        }

        if (_processNames.Contains(window.ProcessName))
        {
            return true;
        }

        return _titleKeywords.Any(keyword => window.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static AccessibleControlInfo? FindInput(IReadOnlyList<AccessibleControlInfo> elements) =>
        elements
            .Where(element => element.IsEnabled && !element.IsOffscreen && IsInput(element))
            .OrderByDescending(element => Score(element, "message", "prompt", "composer", "ask", "input", "question"))
            .ThenBy(element => element.Depth)
            .FirstOrDefault();

    private static string? FindConversation(IReadOnlyList<AccessibleControlInfo> elements, string title)
    {
        AccessibleControlInfo? named = elements
            .Where(element => !IsInput(element) && element.Name.Length > 0)
            .OrderByDescending(element => Score(element, "conversation", "session", "chat"))
            .FirstOrDefault(element => Score(element, "conversation", "session", "chat") > 0);
        return named?.Name.Trim() ?? (string.IsNullOrWhiteSpace(title) ? null : title.Trim());
    }

    private static bool IsInput(AccessibleControlInfo element)
    {
        string metadata = $"{element.Id} {element.Name} {element.ControlType} {string.Join(' ', element.SupportedPatterns)}";
        bool isEditor = element.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                        element.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase) ||
                        element.SupportedPatterns.Contains("Value", StringComparer.OrdinalIgnoreCase);
        return isEditor && !metadata.Contains("button", StringComparison.OrdinalIgnoreCase) &&
               (metadata.Contains("input", StringComparison.OrdinalIgnoreCase) ||
                metadata.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                metadata.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
                metadata.Contains("composer", StringComparison.OrdinalIgnoreCase) ||
                metadata.Contains("ask", StringComparison.OrdinalIgnoreCase) ||
                metadata.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                metadata.Contains("question", StringComparison.OrdinalIgnoreCase) ||
                element.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase));
    }

    private static int Score(AccessibleControlInfo element, params string[] terms)
    {
        string metadata = $"{element.Id} {element.Name} {element.ControlType}";
        return terms.Count(term => metadata.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNavigationText(string text, string name, string id)
    {
        string metadata = $"{text} {name} {id}";
        return metadata.Contains("sidebar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("navigation", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("activitybar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("titlebar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("statusbar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsComposerText(string text) =>
        text.Contains("send a message", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ask anything", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("type a message", StringComparison.OrdinalIgnoreCase);

    private bool IsLastSentText(string text) =>
        !string.IsNullOrWhiteSpace(_lastSentText) &&
        string.Equals(text, Normalize(_lastSentText), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsTransientDesktopException(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException;
}
