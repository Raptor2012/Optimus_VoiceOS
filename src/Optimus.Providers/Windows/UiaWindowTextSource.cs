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
            System.Windows.Rect rootBounds;
            try
            {
                rootBounds = root.Current.BoundingRectangle;
            }
            catch
            {
                rootBounds = System.Windows.Rect.Empty;
            }

            var excludedBounds = new List<System.Windows.Rect>();
            CollectExcludedContainerBounds(root, excludedBounds);

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

                if (IsSidebarOrChrome(automationId, name, controlType))
                {
                    continue;
                }

                System.Windows.Rect bounds;
                try
                {
                    bounds = element.Current.BoundingRectangle;
                }
                catch
                {
                    bounds = System.Windows.Rect.Empty;
                }

                if (IsInsideExcludedGeometry(bounds, rootBounds, excludedBounds))
                {
                    continue;
                }

                if (IsInExcludedContainer(element, root, excludedBounds))
                {
                    continue;
                }

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

    private static void CollectExcludedContainerBounds(AutomationElement root, List<System.Windows.Rect> excludedBounds)
    {
        try
        {
            TreeWalker walker = TreeWalker.ControlViewWalker;
            AutomationElement? child = walker.GetFirstChild(root);
            while (child != null)
            {
                CheckAndAddExcludedBounds(child, excludedBounds);

                AutomationElement? grandchild = walker.GetFirstChild(child);
                while (grandchild != null)
                {
                    CheckAndAddExcludedBounds(grandchild, excludedBounds);
                    grandchild = walker.GetNextSibling(grandchild);
                }

                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private static void CheckAndAddExcludedBounds(AutomationElement element, List<System.Windows.Rect> excludedBounds)
    {
        try
        {
            string autoId = element.Current.AutomationId ?? string.Empty;
            string className = element.Current.ClassName ?? string.Empty;
            string name = element.Current.Name ?? string.Empty;

            if (IsSidebarOrChromeContainer(autoId, className, name))
            {
                System.Windows.Rect rect = element.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                {
                    excludedBounds.Add(rect);
                }
            }
        }
        catch { }
    }

    private static bool IsInsideExcludedGeometry(
        System.Windows.Rect bounds,
        System.Windows.Rect rootBounds,
        List<System.Windows.Rect> excludedBounds)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        foreach (System.Windows.Rect excl in excludedBounds)
        {
            double cx = bounds.Left + bounds.Width / 2.0;
            double cy = bounds.Top + bounds.Height / 2.0;
            if (cx >= excl.Left && cx <= excl.Right && cy >= excl.Top && cy <= excl.Bottom)
            {
                return true;
            }
        }

        if (!rootBounds.IsEmpty && rootBounds.Width > 200 && rootBounds.Height > 200)
        {
            // Title bar / top window menu band (top 60px)
            if (bounds.Bottom <= rootBounds.Top + 60)
            {
                return true;
            }

            // Bottom status bar band (bottom 35px)
            if (bounds.Top >= rootBounds.Bottom - 35)
            {
                return true;
            }

            // Far-left icon bar (left 50px)
            if (bounds.Right <= rootBounds.Left + 50)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInExcludedContainer(
        AutomationElement element,
        AutomationElement root,
        List<System.Windows.Rect> excludedBounds)
    {
        try
        {
            TreeWalker walker = TreeWalker.ControlViewWalker;
            AutomationElement current = element;
            int depth = 0;
            while (current != null && !current.Equals(root) && depth++ < 6)
            {
                string autoId = current.Current.AutomationId ?? string.Empty;
                string className = current.Current.ClassName ?? string.Empty;
                string name = current.Current.Name ?? string.Empty;

                if (IsSidebarOrChromeContainer(autoId, className, name))
                {
                    try
                    {
                        System.Windows.Rect r = current.Current.BoundingRectangle;
                        if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                        {
                            excludedBounds.Add(r);
                        }
                    }
                    catch { }

                    return true;
                }

                current = walker.GetParent(current);
            }
        }
        catch (ElementNotAvailableException)
        {
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }

        return false;
    }

    private static bool IsSidebarOrChromeContainer(string autoId, string className, string name)
    {
        string meta = $"{autoId} {className} {name}";
        return meta.Contains("sidebar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("activitybar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("activity-bar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("titlebar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("title-bar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("statusbar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("status-bar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("menubar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("menu-bar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("auxiliarybar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("workbench.parts.sidebar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("workbench.parts.activitybar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("workbench.parts.titlebar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("workbench.parts.statusbar", StringComparison.OrdinalIgnoreCase) ||
               meta.Contains("navigation", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsSidebarOrChrome(string automationId, string name, string controlType)
    {
        string metadata = $"{automationId} {name} {controlType}";
        return metadata.Contains("sidebar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("activitybar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("activity-bar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("workbench.parts", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("titlebar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("menubar", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("navigation", StringComparison.OrdinalIgnoreCase);
    }
}
