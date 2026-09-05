namespace Optimus.Providers.Windows;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Automation;

/// <summary>Reads text exposed through Windows UI Automation beneath one HWND.</summary>
public sealed class UiaWindowTextSource : IWindowTextSource
{
    private static readonly Condition TextControls = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

    public IReadOnlyList<VisibleTextNode> Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return Array.Empty<VisibleTextNode>();
        }

        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, TextControls);
            var result = new List<VisibleTextNode>(elements.Count);

            for (int index = 0; index < elements.Count; index++)
            {
                AutomationElement element = elements[index];
                string text = ReadText(element);
                if (string.IsNullOrWhiteSpace(text) || element.Current.IsOffscreen)
                {
                    continue;
                }

                string automationId = element.Current.AutomationId ?? string.Empty;
                string name = element.Current.Name ?? string.Empty;
                string controlType = element.Current.ControlType.ProgrammaticName ?? string.Empty;
                string key = BuildKey(element, automationId, controlType, index);
                result.Add(new VisibleTextNode(key, text, name, automationId, controlType));
            }

            return result;
        }
        catch (ElementNotAvailableException)
        {
            return Array.Empty<VisibleTextNode>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<VisibleTextNode>();
        }
    }

    private static string ReadText(AutomationElement element)
    {
        string raw;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
        {
            raw = ((TextPattern)pattern).DocumentRange.GetText(-1);
        }
        else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
        {
            raw = ((ValuePattern)pattern).Current.Value;
        }
        else
        {
            raw = element.Current.Name ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Replace("\uFFFC", "", StringComparison.Ordinal)
                  .Replace("\uFFFD", "", StringComparison.Ordinal);
    }

    private static string BuildKey(
        AutomationElement element,
        string automationId,
        string controlType,
        int fallbackIndex)
    {
        try
        {
            int[] runtimeId = element.GetRuntimeId();
            if (runtimeId.Length > 0)
            {
                return string.Join('.', runtimeId);
            }
        }
        catch (ElementNotAvailableException)
        {
            // The caller will either skip this snapshot or use the deterministic fallback.
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{controlType}:{automationId}:{fallbackIndex}");
    }
}
