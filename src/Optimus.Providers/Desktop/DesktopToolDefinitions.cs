namespace Optimus.Providers.Desktop;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// A tool schema definition for desktop observation and execution.
/// </summary>
public sealed record DesktopToolDefinition(
    string Name,
    string Description,
    JsonDocument Parameters)
{
    public object ToFunctionObject() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = Description,
            parameters = Parameters.RootElement
        }
    };
}

public sealed record ListWindowsArgs(
    string? FilterProcessName = null,
    bool IncludeEmptyTitles = false);

public sealed record FocusWindowArgs(
    long Hwnd,
    string? ProcessName = null);

public sealed record InspectControlsArgs(
    long Hwnd,
    int MaxDepth = 6,
    int MaxElements = 150,
    string? FilterControlType = null);

public sealed record CaptureWindowArgs(
    long Hwnd,
    int? RegionX = null,
    int? RegionY = null,
    int? RegionWidth = null,
    int? RegionHeight = null);

public sealed record InvokeElementArgs(
    long Hwnd,
    string? ElementId = null,
    string? ElementName = null,
    string? ControlType = null);

public sealed record ClickArgs(
    int X,
    int Y,
    string ClickType = "left",
    long? Hwnd = null);

public sealed record ScrollArgs(
    int DeltaY,
    int DeltaX = 0,
    int? X = null,
    int? Y = null,
    long? Hwnd = null);

public sealed record EnterTextArgs(
    string Text,
    long Hwnd,
    string? ElementId = null,
    bool PreserveExisting = true,
    string Mode = "auto");

public sealed record PressShortcutArgs(
    string Shortcut,
    long Hwnd);

public sealed record ReadContentArgs(
    long Hwnd,
    bool ExcludeChrome = true);

public sealed record ObserveResultArgs(
    long Hwnd,
    string? ExpectedChange = null,
    int TimeoutMs = 3000);

/// <summary>
/// Tool definitions and JSON schemas for local model tool calling.
/// </summary>
public static class DesktopToolDefinitions
{
    public static readonly DesktopToolDefinition ListWindows = new(
        "list_windows",
        "List all visible top-level application windows with titles, process IDs, and handles.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filter_process_name": {
                    "type": "string",
                    "description": "Optional process name filter (case-insensitive)."
                },
                "include_empty_titles": {
                    "type": "boolean",
                    "description": "Whether to include windows with empty titles. Default is false."
                }
            },
            "required": []
        }
        """));

    public static readonly DesktopToolDefinition FocusWindow = new(
        "focus_window",
        "Bring the specified window to the foreground and set keyboard focus.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "hwnd": {
                    "type": "integer",
                    "description": "The window handle (HWND) to focus."
                },
                "process_name": {
                    "type": "string",
                    "description": "Optional expected process name to verify window identity."
                }
            },
            "required": ["hwnd"]
        }
        """));

    public static readonly DesktopToolDefinition InspectControls = new(
        "inspect_controls",
        "Inspect the UI Automation accessibility control tree of a target window. Output is strictly scoped to the relevant window.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "hwnd": {
                    "type": "integer",
                    "description": "The HWND handle of the window to inspect."
                },
                "max_depth": {
                    "type": "integer",
                    "description": "Maximum tree depth to inspect. Default is 6."
                },
                "max_elements": {
                    "type": "integer",
                    "description": "Maximum number of elements to return. Default is 150."
                },
                "filter_control_type": {
                    "type": "string",
                    "description": "Optional control type filter (e.g. Button, Edit, Document, List)."
                }
            },
            "required": ["hwnd"]
        }
        """));

    public static readonly DesktopToolDefinition CaptureWindow = new(
        "capture_window",
        "Capture a screenshot of the specified window or sub-region with readable text and coordinate mapping.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "hwnd": {
                    "type": "integer",
                    "description": "The HWND handle of the window to capture."
                },
                "region_x": {
                    "type": "integer",
                    "description": "Optional X coordinate offset relative to window top-left."
                },
                "region_y": {
                    "type": "integer",
                    "description": "Optional Y coordinate offset relative to window top-left."
                },
                "region_width": {
                    "type": "integer",
                    "description": "Optional width of the crop region."
                },
                "region_height": {
                    "type": "integer",
                    "description": "Optional height of the crop region."
                }
            },
            "required": ["hwnd"]
        }
        """));

    public static readonly DesktopToolDefinition InvokeElement = new(
        "invoke_element",
        "Invoke or interact with an accessible control (button, tab, menu item, checkbox) by its accessibility ID or name. Preferred over coordinate clicks.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "hwnd": {
                    "type": "integer",
                    "description": "The HWND handle of the containing window."
                },
                "element_id": {
                    "type": "string",
                    "description": "The AutomationId or RuntimeId of the element."
                },
                "element_name": {
                    "type": "string",
                    "description": "The accessible Name of the element."
                },
                "control_type": {
                    "type": "string",
                    "description": "Optional control type (e.g. Button) to disambiguate."
                }
            },
            "required": ["hwnd"]
        }
        """));

    public static readonly DesktopToolDefinition Click = new(
        "click",
        "Click or double-click at specific coordinates on screen or relative to a window.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "x": {
                    "type": "integer",
                    "description": "X coordinate."
                },
                "y": {
                    "type": "integer",
                    "description": "Y coordinate."
                },
                "click_type": {
                    "type": "string",
                    "enum": ["left", "double", "right"],
                    "description": "Type of click (left, double, right). Default is left."
                },
                "hwnd": {
                    "type": "integer",
                    "description": "Optional target window HWND if coordinates are window-relative."
                }
            },
            "required": ["x", "y"]
        }
        """));

    public static readonly DesktopToolDefinition Scroll = new(
        "scroll",
        "Scroll vertically or horizontally by a given delta at the current or specified position.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "delta_y": {
                    "type": "integer",
                    "description": "Vertical scroll delta in wheel clicks (positive for down, negative for up)."
                },
                "delta_x": {
                    "type": "integer",
                    "description": "Horizontal scroll delta. Default is 0."
                },
                "x": {
                    "type": "integer",
                    "description": "Optional X coordinate to position cursor before scrolling."
                },
                "y": {
                    "type": "integer",
                    "description": "Optional Y coordinate to position cursor before scrolling."
                },
                "hwnd": {
                    "type": "integer",
                    "description": "Optional target window handle."
                }
            },
            "required": ["delta_y"]
        }
        """));

    public static readonly DesktopToolDefinition EnterText = new(
        "enter_text",
        "Enter text into an editable control or window. Preserves unsent content and prefers accessible entry over keystrokes or clipboard.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "text": {
                    "type": "string",
                    "description": "The text to enter."
                },
                "hwnd": {
                    "type": "integer",
                    "description": "The HWND handle of the target window."
                },
                "element_id": {
                    "type": "string",
                    "description": "Optional AutomationId of the editable control (preferred for accessible entry)."
                },
                "preserve_existing": {
                    "type": "boolean",
                    "description": "Whether to preserve existing unsent text (append rather than overwrite). Default is true."
                },
                "mode": {
                    "type": "string",
                    "enum": ["auto", "accessible", "type", "clipboard"],
                    "description": "Text entry mode. Default is auto."
                }
            },
            "required": ["text", "hwnd"]
        }
        """));

    public static readonly DesktopToolDefinition PressShortcut = new(
        "press_shortcut",
        "Send a keyboard shortcut or key combination (e.g. 'Ctrl+C', 'Ctrl+Shift+P', 'Enter', 'Escape') to the target window.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "shortcut": {
                    "type": "string",
                    "description": "Keyboard shortcut string (e.g. 'Ctrl+Enter', 'Alt+F4', 'Escape')."
                },
                "hwnd": {
                    "type": "integer",
                    "description": "Target window HWND."
                }
            },
            "required": ["shortcut", "hwnd"]
        }
        """));

    public static readonly DesktopToolDefinition ReadContent = new(
        "read_content",
        "Read visible text content and messages from the specified window's accessibility tree.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "hwnd": {
                    "type": "integer",
                    "description": "The HWND handle of the window to read."
                },
                "exclude_chrome": {
                    "type": "boolean",
                    "description": "Exclude window chrome, title bar, menus, and sidebars. Default is true."
                }
            },
            "required": ["hwnd"]
        }
        """));

    public static readonly DesktopToolDefinition ObserveResult = new(
        "observe_result",
        "Wait for and observe the outcome of an action in the target window with bounded deadlines and layout change detection.",
        JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "hwnd": {
                    "type": "integer",
                    "description": "The HWND handle of the window to observe."
                },
                "expected_change": {
                    "type": "string",
                    "description": "Optional description of expected state change."
                },
                "timeout_ms": {
                    "type": "integer",
                    "description": "Timeout in milliseconds for bounded wait. Default is 3000."
                }
            },
            "required": ["hwnd"]
        }
        """));

    public static readonly IReadOnlyList<DesktopToolDefinition> All = new[]
    {
        ListWindows,
        FocusWindow,
        InspectControls,
        CaptureWindow,
        InvokeElement,
        Click,
        Scroll,
        EnterText,
        PressShortcut,
        ReadContent,
        ObserveResult
    };

    private static readonly Dictionary<string, DesktopToolDefinition> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [ListWindows.Name] = ListWindows,
        [FocusWindow.Name] = FocusWindow,
        [InspectControls.Name] = InspectControls,
        [CaptureWindow.Name] = CaptureWindow,
        [InvokeElement.Name] = InvokeElement,
        [Click.Name] = Click,
        [Scroll.Name] = Scroll,
        [EnterText.Name] = EnterText,
        [PressShortcut.Name] = PressShortcut,
        [ReadContent.Name] = ReadContent,
        [ObserveResult.Name] = ObserveResult,
    };

    public static DesktopToolDefinition? Get(string name) =>
        ByName.TryGetValue(name, out DesktopToolDefinition? tool) ? tool : null;

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

    public static string ToJsonSchema(bool indented = true)
    {
        var list = new List<object>(All.Count);
        foreach (DesktopToolDefinition tool in All)
        {
            list.Add(tool.ToFunctionObject());
        }

        return JsonSerializer.Serialize(list, indented ? IndentedOptions : CompactOptions);
    }
}
