namespace Optimus.Shell.Services;

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Forms = System.Windows.Forms;

/// <summary>Owns the notification-area presence of the companion.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showHideItem;
    private bool _isVisible = true;
    private bool _disposed;

    public event EventHandler? ShowHideRequested;
    public event EventHandler? ToggleListeningRequested;
    public event EventHandler? QuitRequested;

    public TrayService()
    {
        _showHideItem = new Forms.ToolStripMenuItem("Hide companion");
        _showHideItem.Click += (_, _) => ShowHideRequested?.Invoke(this, EventArgs.Empty);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_showHideItem);
        var listen = new Forms.ToolStripMenuItem("Toggle listening");
        listen.Click += (_, _) => ToggleListeningRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(listen);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var quit = new Forms.ToolStripMenuItem("Quit Optimus Voice OS");
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(quit);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = CreateOptimusIcon(),
            Text = "Optimus Voice OS",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHideRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetVisible(bool visible)
    {
        _isVisible = visible;
        _showHideItem.Text = visible ? "Hide companion" : "Show companion";
    }

    private static Icon CreateOptimusIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(140, 158, 255), Color.FromArgb(61, 74, 107), 45);
            graphics.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new Pen(Color.FromArgb(241, 242, 244), 2.5f);
            graphics.DrawEllipse(pen, 9, 7, 14, 18);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
