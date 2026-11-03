using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Silverbud.Core;
using Silverbud.ViewModels;
using Forms = System.Windows.Forms;

namespace Silverbud;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _tray;
    private bool _exiting;
    private bool _toldAboutTray;

    public MainWindow(bool demo)
    {
        InitializeComponent();
        // Never open taller or wider than the screen's usable area
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 16);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 16);
        _vm = new MainViewModel(Dispatcher)
        {
            Confirm = (title, message) =>
                MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK
        };
        DataContext = _vm;

        _tray = CreateTray();
        _vm.TrayTextChanged += () => _tray.Text = _vm.TrayText;
        _vm.Notify += (title, text) => _tray.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Warning);

        SourceInitialized += (_, _) => UseRoundedCorners();
        StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        if (demo)
            _vm.LoadDemo();
        else
            _vm.Start();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    public void Quit()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _vm.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray.ShowBalloonTip(4000, "Silverbud is still running",
                    "It's in the notification area, watching your buds' battery. Right-click the icon to quit.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        _vm.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Silverbud", null, (_, _) => ShowFromTray());

        var noise = new Forms.ToolStripMenuItem("Noise control");
        foreach (var (label, value) in new[] { ("Off", 0), ("Noise canceling", 1), ("Ambient sound", 2) })
        {
            var item = new Forms.ToolStripMenuItem(label) { Tag = value };
            item.Click += (_, _) => _vm.NoiseControl = value;
            noise.DropDownItems.Add(item);
        }
        menu.Items.Add(noise);

        var ring = new Forms.ToolStripMenuItem("Ring buds");
        ring.Click += (_, _) => _vm.RingCommand.Execute(null);
        menu.Items.Add(ring);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Silverbud", null, (_, _) => Quit());

        menu.Opening += (_, _) =>
        {
            noise.Enabled = ring.Enabled = _vm.IsLinked;
            ring.Text = _vm.RingLabel;
            foreach (Forms.ToolStripMenuItem item in noise.DropDownItems)
            {
                item.Checked = (int)item.Tag! == _vm.NoiseControl;
                item.Visible = (int)item.Tag! != 2 || _vm.HasAmbientSound;
            }
        };

        var tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Silverbud",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowFromTray();
        };
        return tray;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/silverbud.ico"));
        using var stream = resource!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UseRoundedCorners()
    {
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Write($"Rounded corners unavailable: {ex.Message}");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
