using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Cursivis.Companion.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private Drawing.Icon? _icon;

    public TrayIconService(Action openSettings, Func<Task> runDiagnostics, Action openLogs, Action exit)
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Cursivis",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu(openSettings, runDiagnostics, openLogs, exit)
        };
        _notifyIcon.DoubleClick += (_, _) => openSettings();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
    }

    private Forms.ContextMenuStrip BuildMenu(Action openSettings, Func<Task> runDiagnostics, Action openLogs, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => openSettings());
        menu.Items.Add("Diagnostics & Repair", null, async (_, _) => await runDiagnostics());
        menu.Items.Add("Open Logs", null, (_, _) => openLogs());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Updates install with Companion Setup") { Enabled = false });
        menu.Items.Add("Exit Cursivis", null, (_, _) => exit());
        return menu;
    }

    private Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/cursivis-icon.png"));
            if (resource?.Stream is null)
            {
                return Drawing.SystemIcons.Application;
            }

            using (resource.Stream)
            using (var bitmap = new Drawing.Bitmap(resource.Stream))
            {
                var iconHandle = bitmap.GetHicon();
                try
                {
                    using var temporary = Drawing.Icon.FromHandle(iconHandle);
                    _icon = (Drawing.Icon)temporary.Clone();
                    return _icon;
                }
                finally
                {
                    DestroyIcon(iconHandle);
                }
            }
        }
        catch
        {
            return Drawing.SystemIcons.Application;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
