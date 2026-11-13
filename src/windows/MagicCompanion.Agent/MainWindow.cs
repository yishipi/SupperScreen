using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QRCoder;
using Forms = System.Windows.Forms;

namespace MagicCompanion.Agent;

internal sealed partial class MainWindow : Window
{
    private readonly AgentStore store;
    private readonly HostIdentity identity;
    private readonly WindowsPlatform platform;
    private readonly PairingCoordinator pairing;
    private AgentServer? server;
    private X509Certificate2? serverCertificate;
    private IPAddress[] activeLanAddresses = [];
    private IPAddress[] configuredLanAddresses = [];
    private const int UsbPort = 8765;
    private const int LanPort = 8766;
    private long nextConnectionCheck;
    private Task connectionUpdate = Task.CompletedTask;
    private bool restartingServer => !connectionUpdate.IsCompleted;
    private bool controlPaused;
    private AdbBridge? adb;
    private BluetoothRelay? bluetooth;
    private readonly List<LanDiscovery> discovery = [];
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon? trayIcon;
    private readonly StackPanel todoRows = new(),
        appRows = new(),
        deviceRows = new();
    private readonly TextBox scriptJson = new() { AcceptsReturn = true, Height = 200, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel scriptRows = new();
    private readonly TextBlock status = new(),
        usbStatus = new(),
        bluetoothStatus = new(),
        lanStatus = new(),
        message = new();
    private readonly ListBox diagnostics = new() { MaxHeight = 260 };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool quitting;
    private Window? pairingWindow;
    private HwndSource? source;
    private const string DefaultQuickHotkey = "CTRL+`";
    private int quickHotkeyId;
    private (uint Modifiers, uint Key) quickHotkey;
    private readonly string dataDirectory;
    private readonly Dictionary<string, string> permissionNames = new()
    {
        ["system.read"] = "系统状态",
        ["todos.read"] = "读取待办",
        ["todos.write"] = "手机完成待办",
        ["app.launch"] = "启动允许的应用",
        ["audio.control"] = "音量和媒体",
        ["tasks.read"] = "快捷脚本和任务状态",
        ["terminal.control"] = "PowerShell 终端",
        ["screen.capture"] = "保存电脑截屏",
        ["tasks.run"] = "运行电脑配置的任务",
    };

    public MainWindow(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        dataDirectory =
            index >= 0 && args.Length > index + 1
                ? Path.GetFullPath(args[index + 1])
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MagicCompanion"
                );
        Directory.CreateDirectory(dataDirectory);
        store = new(Path.Combine(dataDirectory, "agent.db"));
        identity = new(store, dataDirectory);
        platform = new();
        pairing = new(store, identity.HostId);
        pairing.ApproveAsync = ApprovePairing;
        Title = "SupperScreen · 电脑控制台";
        SetResourceReference(IconProperty, "AppIcon");
        Width = 1060;
        Height = 760;
        MinWidth = 820;
        MinHeight = 560;
        Background = Brush("#F2F4F8");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var connect = Button("连接手机", ShowPairing, true);
        connect.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(connect, Dock.Right);
        header.Children.Add(connect);
        var title = new StackPanel();
        header.Children.Add(title);
        title.Children.Add(new TextBlock
        {
            Text = "SupperScreen",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#18243A"),
        });
        status.FontSize = 12;
        status.Foreground = Brush("#64748B");
        status.Margin = new Thickness(0, 5, 0, 0);
        title.Children.Add(status);
        message.Foreground = Brush("#A54020");
        message.TextWrapping = TextWrapping.Wrap;
        message.Visibility = Visibility.Collapsed;
        message.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(message, Dock.Bottom);
        root.Children.Add(message);
        var tabs = new TabControl();
        root.Children.Add(tabs);
        AddTab(tabs, "待办", BuildTodos());
        AddTab(tabs, "应用启动", BuildApps());
        AddTab(tabs, "手机与权限", BuildDevices());
        AddTab(tabs, "连接设置", BuildSettings());
        AddTab(tabs, "脚本", BuildTasks());
        AddTab(tabs, "截屏", BuildScreenshots());
        AddTab(tabs, "诊断", diagnostics);
        trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "SupperScreen",
            Visible = true,
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowConsole);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开控制台", null, (_, _) => Dispatcher.Invoke(ShowConsole));
        menu.Items.Add("待办悬浮窗", null, (_, _) => Dispatcher.Invoke(QuickTodo));
        menu.Items.Add(
            "暂停 / 恢复手机控制",
            null,
            (_, _) => Dispatcher.Invoke(() => SetControlPaused(!controlPaused))
        );
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(CloseApplication));
        tray.ContextMenuStrip = menu;
        Closing += (_, e) =>
        {
            if (!quitting)
            {
                e.Cancel = true;
                Hide();
            }
        };
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessage);
            RegisterQuickHotkey(store.GetSetting("quickHotkey") ?? DefaultQuickHotkey, false);
        };
        store.TodosChanged += _ => Dispatcher.BeginInvoke(RenderTodos);
        store.ConfigurationChanged += () =>
            Dispatcher.BeginInvoke(() =>
            {
                RenderApps();
                RenderDevice();
            });
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        refresh.Tick += async (_, _) =>
        {
            status.Text =
                server == null
                    ? "服务未启动"
                    : $"{Environment.MachineName} · {(server.LinkCount > 0 ? "手机已连接" : "等待手机")} · {(server.ControlPaused ? "控制已暂停" : "服务就绪")}";
            if (restartingServer || server == null || Environment.TickCount64 < nextConnectionCheck)
                return;
            nextConnectionCheck = Environment.TickCount64 + 5000;
            var addresses = LanAddresses();
            if (!configuredLanAddresses.SequenceEqual(addresses))
                await StartServer(addresses);
            else
                await StartBluetooth();
        };
        refresh.Start();
        Loaded += async (_, _) =>
        {
            RenderTodos();
            RenderApps();
            RenderDevice();
            await StartServer();
            if (args.Contains("--tray"))
                Hide();
        };
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private static TextBlock Text(string text, double size = 13) =>
        new()
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#18243A"),
            Margin = new Thickness(0, 4, 0, 8),
        };

    private static Button Button(string text, Action action, bool primary = false)
    {
        var button = CreateButton(text, primary);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button CreateButton(object content, bool primary = false)
    {
        var button = new Button { Content = content };
        if (primary)
            button.SetResourceReference(StyleProperty, "PrimaryButton");
        return button;
    }

    private static Border Card(UIElement content) => new()
    {
        Child = content,
        Background = Brushes.White,
        BorderBrush = Brush("#DCE3EE"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(0, 4, 0, 4),
    };

    private static void AddTab(TabControl tabs, string name, UIElement content) =>
        tabs.Items.Add(
            new TabItem
            {
                Header = name,
                Content = new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = new Thickness(16),
                },
            }
        );

    private void Try(Action action)
    {
        try
        {
            action();
            message.Text = "";
            message.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
            when (ex
                    is AgentException
                        or ArgumentException
                        or IOException
                        or JsonException
                        or System.ComponentModel.Win32Exception
            )
        {
            Report(ex.Message);
        }
    }

    private void Report(string text)
    {
        message.Text = text;
        message.Visibility = Visibility.Visible;
        RecordDiagnostic(text);
    }

    private void RecordDiagnostic(string text)
    {
        diagnostics.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + " " + text);
        while (diagnostics.Items.Count > 100)
            diagnostics.Items.RemoveAt(100);
    }

    private StackPanel BuildApps()
    {
        var p = new StackPanel();
        p.Children.Add(Button("添加应用", PickApp, true));
        p.Children.Add(appRows);
        return p;
    }

    private void RenderApps()
    {
        appRows.Children.Clear();
        foreach (var app in store.Apps())
        {
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            row.Children.Add(Text(app.DisplayName));
            row.Children.Add(Button("移除", () => Try(() => store.RemoveApp(app.AppId))));
            appRows.Children.Add(Card(row));
        }
    }

    private void PickApp()
    {
        Try(() =>
        {
            var apps = platform.DiscoverApps();
            var dialog = Dialog("Windows 应用选择器", 660, 610);
            var panel = new DockPanel { Margin = new Thickness(20) };
            dialog.Content = panel;
            var search = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(search, Dock.Top);
            panel.Children.Add(search);
            var list = new ListBox { DisplayMemberPath = "DisplayName", ItemsSource = apps };
            var add = Button(
                "加入允许列表",
                () =>
                {
                    if (list.SelectedItem is not DiscoveredApp chosen)
                        return;
                    var old = store.Apps().FirstOrDefault(a => a.SourceId == chosen.SourceId);
                    if (old == null)
                        store.SaveApp(
                            new(
                                Wire.NewId(),
                                chosen.DisplayName,
                                chosen.SourceId,
                                chosen.ExecutablePath,
                                chosen.Arguments,
                                chosen.IconBase64
                            )
                        );
                    dialog.Close();
                }
            );
            DockPanel.SetDock(add, Dock.Bottom);
            panel.Children.Add(add);
            panel.Children.Add(list);
            search.TextChanged += (_, _) =>
                list.ItemsSource = apps.Where(a =>
                        a.DisplayName.Contains(
                            search.Text,
                            StringComparison.CurrentCultureIgnoreCase
                        )
                    )
                    .ToList();
            dialog.ShowDialog();
        });
    }

    private StackPanel BuildDevices()
    {
        var p = new StackPanel();
        p.Children.Add(deviceRows);
        return p;
    }

    private void RenderDevice()
    {
        deviceRows.Children.Clear();
        if (store.Device() is not { Revoked: false } device)
        {
            deviceRows.Children.Add(Text("尚未配对手机"));
            return;
        }
        deviceRows.Children.Add(Text(device.Name, 16));
        var permissions = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(0, 8, 0, 8),
        };
        deviceRows.Children.Add(Card(permissions));
        foreach (var permission in PermissionSet.All)
        {
            var check = new CheckBox
            {
                Content = permissionNames[permission],
                IsChecked = device.Permissions.Contains(permission),
                Margin = new Thickness(4, 7, 4, 7),
            };
            check.Click += (_, _) =>
                Try(() =>
                {
                    var current = store.Device()!;
                    store.SaveDevice(current with
                    {
                        Permissions = PermissionSet.Set(current.Permissions, permission, check.IsChecked == true),
                    });
                });
            permissions.Children.Add(check);
        }
        deviceRows.Children.Add(
            Button(
                "解除配对并停止控制",
                () =>
                    Try(() => pairing.Revoke(device.DeviceId))
            )
        );
    }

    private StackPanel BuildScreenshots()
    {
        var panel = new StackPanel();
        var directory = store.GetSetting("screenshotDirectory") ?? "";
        var path = new TextBox { IsReadOnly = true, Text = directory };
        var choose = Button("选择目录", () => Try(() =>
        {
            var picker = new OpenFolderDialog { Title = "选择截图保存目录" };
            if (Directory.Exists(directory)) picker.InitialDirectory = directory;
            if (picker.ShowDialog(this) != true) return;
            directory = picker.FolderName;
            store.SetSetting("screenshotDirectory", directory);
            path.Text = directory;
        }), true);
        var row = new DockPanel();
        DockPanel.SetDock(choose, Dock.Right);
        row.Children.Add(choose);
        row.Children.Add(path);
        panel.Children.Add(Text("截图保存目录"));
        panel.Children.Add(row);
        panel.Children.Add(Text("在「手机与权限」开启「保存电脑截屏」。每个活动屏幕各保存一张 PNG。"));
        return panel;
    }

    private StackPanel BuildSettings()
    {
        var panel = new StackPanel();
        var connectionStatus = new StackPanel();
        usbStatus.TextWrapping = TextWrapping.Wrap;
        bluetoothStatus.TextWrapping = TextWrapping.Wrap;
        usbStatus.Margin = new Thickness(0, 4, 0, 4);
        bluetoothStatus.Margin = new Thickness(0, 4, 0, 4);
        connectionStatus.Children.Add(usbStatus);
        connectionStatus.Children.Add(bluetoothStatus);
        connectionStatus.Children.Add(lanStatus);
        panel.Children.Add(Card(connectionStatus));

        var lan = new CheckBox
        {
            Content = "允许局域网连接",
            IsChecked = LanEnabled,
            Margin = new Thickness(0, 6, 0, 10),
        };
        lan.Click += async (_, _) =>
        {
            store.SetSetting("lanEnabled", lan.IsChecked == true ? "true" : "false");
            await StartServer(LanAddresses());
        };
        panel.Children.Add(lan);

        var usb = new StackPanel();
        usb.Children.Add(Text("ADB 路径"));
        var adbPath = new TextBox
        {
            Text = store.GetSetting("adbPath") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe"
            ),
        };
        var pathRow = new DockPanel();
        var pickAdb = Button("浏览", () =>
        {
            var picker = new OpenFileDialog { Filter = "ADB|adb.exe", CheckFileExists = true };
            if (picker.ShowDialog() == true)
                adbPath.Text = picker.FileName;
        });
        DockPanel.SetDock(pickAdb, Dock.Right);
        pathRow.Children.Add(pickAdb);
        pathRow.Children.Add(adbPath);
        usb.Children.Add(pathRow);
        usb.Children.Add(Text("ADB 端口"));
        var port = new TextBox { Text = store.GetSetting("adbPort") ?? "15037" };
        usb.Children.Add(port);
        usb.Children.Add(Text("设备序列号（可留空）"));
        var serial = new TextBox { Text = store.GetSetting("adbSerial") ?? "" };
        usb.Children.Add(serial);
        panel.Children.Add(new Expander { Header = "USB 调试", Content = usb });

        var preferences = new StackPanel();
        preferences.Children.Add(Text("待办悬浮窗快捷键"));
        var quick = new TextBox { Text = store.GetSetting("quickHotkey") ?? DefaultQuickHotkey };
        var hotkeyRow = new DockPanel();
        var saveHotkey = Button("保存快捷键", () => RegisterQuickHotkey(quick.Text, true));
        DockPanel.SetDock(saveHotkey, Dock.Right);
        hotkeyRow.Children.Add(saveHotkey);
        hotkeyRow.Children.Add(quick);
        preferences.Children.Add(hotkeyRow);
        using (var startup = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            var autostart = new CheckBox
            {
                Content = "登录后在托盘运行",
                IsChecked = startup?.GetValue("MagicCompanion") != null,
                Margin = new Thickness(0, 12, 0, 12),
            };
            autostart.Click += (_, _) => Try(() =>
            {
                using var run = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                if (autostart.IsChecked == true)
                    run.SetValue("MagicCompanion", "\"" + Environment.ProcessPath + "\" --tray");
                else
                    run.DeleteValue("MagicCompanion", false);
            });
            preferences.Children.Add(autostart);
        }
        panel.Children.Add(new Expander { Header = "偏好设置", Content = preferences });
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Button("保存连接设置", async () =>
        {
            if (!int.TryParse(port.Text, out var number) || number < 1024 || number > 65535)
            {
                Report("ADB 端口需在 1024～65535 之间");
                return;
            }
            store.SetSetting("adbPath", adbPath.Text);
            store.SetSetting("adbPort", number.ToString());
            store.SetSetting("adbSerial", serial.Text.Trim());
            await StartServer();
        }, true));
        actions.Children.Add(Button("退出程序", CloseApplication));
        panel.Children.Add(actions);
        return panel;
    }

    private StackPanel BuildTasks()
    {
        var p = new StackPanel();
        var templates = scriptJson;
        templates.Text = store.GetSetting("taskTemplates") ?? "[]";
        p.Children.Add(Button("添加 PowerShell 脚本", AddScript, true));
        p.Children.Add(scriptRows);
        RenderScripts();
        var advanced = new StackPanel();
        templates.ToolTip = "id、name、executable、arguments、workingDirectory、timeoutSeconds、parameterPatterns";
        advanced.Children.Add(templates);
        advanced.Children.Add(
            Button(
                "保存预设",
                () =>
                    Try(() =>
                    {
                        var tasks =
                            JsonSerializer.Deserialize<TaskTemplate[]>(templates.Text, Wire.Json)
                            ?? [];
                        if (tasks.Length > 30)
                            throw new AgentException("INVALID_ARGUMENT", "最多配置 30 个任务");
                        foreach (var t in tasks)
                        {
                            if (
                                !Path.IsPathFullyQualified(t.Executable)
                                || !File.Exists(t.Executable)
                                || !Directory.Exists(t.WorkingDirectory)
                            )
                                throw new AgentException(
                                    "INVALID_ARGUMENT",
                                    "任务程序和工作目录必须存在"
                                );
                        }
                        store.SetSetting("taskTemplates", Wire.Serialize(tasks));
                        RenderScripts();
                        Report("预设已保存");
                    })
            )
        );
        p.Children.Add(new Expander { Header = "高级任务（JSON）", Content = advanced, Margin = new Thickness(0, 12, 0, 0) });
        return p;
    }

    private void AddScript()
    {
        var picker = new OpenFileDialog { Filter = "PowerShell 脚本 (*.ps1)|*.ps1", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var name = Prompt("脚本名称", Path.GetFileNameWithoutExtension(picker.FileName));
        if (string.IsNullOrWhiteSpace(name)) return;
        Try(() =>
        {
            var templates = JsonSerializer.Deserialize<TaskTemplate[]>(store.GetSetting("taskTemplates") ?? "[]", Wire.Json) ?? [];
            if (templates.Length >= 30) throw new AgentException("INVALID_ARGUMENT", "最多配置 30 个任务");
            var script = PowerShellTerminal.Script(name.Trim(), picker.FileName);
            store.SetSetting("taskTemplates", Wire.Serialize(templates.Append(script).ToArray()));
            scriptJson.Text = store.GetSetting("taskTemplates")!;
            RenderScripts(); Report("脚本已添加");
        });
    }

    private void RenderScripts()
    {
        scriptRows.Children.Clear();
        var templates = JsonSerializer.Deserialize<TaskTemplate[]>(store.GetSetting("taskTemplates") ?? "[]", Wire.Json) ?? [];
        foreach (var template in templates)
        {
            var row = new DockPanel();
            var remove = Button("移除", () => Try(() =>
            {
                var saved = JsonSerializer.Deserialize<TaskTemplate[]>(store.GetSetting("taskTemplates") ?? "[]", Wire.Json) ?? [];
                store.SetSetting("taskTemplates", Wire.Serialize(saved.Where(t => t.Id != template.Id).ToArray()));
                scriptJson.Text = store.GetSetting("taskTemplates")!;
                RenderScripts();
            }));
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = template.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            scriptRows.Children.Add(Card(row));
        }
    }

    private bool LanEnabled =>
        store.GetSetting("lanEnabled") switch
        {
            "true" => true,
            "false" => false,
            _ => store.GetSetting("lanAddress") != "关闭局域网",
        };

    private IPAddress[] LanAddresses() => LanEnabled ? HostIdentity.LocalAddresses() : [];

    private void StopDiscovery()
    {
        foreach (var service in discovery)
            service.Dispose();
        discovery.Clear();
    }

    private Task StartServer(IPAddress[]? addresses = null)
    {
        if (restartingServer || quitting)
            return connectionUpdate;
        connectionUpdate = RestartConnections(addresses ?? LanAddresses());
        return connectionUpdate;
    }

    private async Task RestartConnections(IPAddress[] addresses)
    {
        configuredLanAddresses = addresses;
        activeLanAddresses = [];
        try
        {
            pairingWindow?.Close();
            adb?.Dispose();
            StopDiscovery();
            StopBluetooth();
            await StopServer();
            if (quitting)
                return;
            try
            {
                await StartListener(configuredLanAddresses);
                activeLanAddresses = configuredLanAddresses;
            }
            catch (IOException) when (configuredLanAddresses.Length > 0)
            {
                // A LAN address can disappear between enumeration and binding.
                await StopServer();
                await StartListener([]);
            }
            if (quitting)
                return;
            lanStatus.Text = !LanEnabled ? "局域网已关闭"
                : activeLanAddresses.Length > 0 ? "局域网已就绪"
                : configuredLanAddresses.Length > 0 ? "局域网不可用，请检查网络和端口 8766"
                : "等待局域网";
            foreach (var address in activeLanAddresses)
            {
                try
                {
                    discovery.Add(new(identity.HostId, address, LanPort));
                }
                catch (System.Net.Sockets.SocketException)
                {
                    lanStatus.Text = "局域网已就绪 · 可扫码连接";
                }
            }
            adb = new(
                store.GetSetting("adbPath")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Android",
                        "Sdk",
                        "platform-tools",
                        "adb.exe"
                    ),
                int.Parse(store.GetSetting("adbPort") ?? "15037"),
                store.GetSetting("adbSerial") ?? ""
            );
            adb.Status += value => Dispatcher.BeginInvoke(() => usbStatus.Text = value);
            adb.Start();
            await StartBluetooth();
            RecordDiagnostic("连接服务已启动");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException)
        {
            await StopServer();
            activeLanAddresses = [];
            lanStatus.Text = "局域网未就绪";
            Report("服务启动失败：" + ex.Message);
        }
    }

    private async Task StartListener(IPAddress[] addresses)
    {
        var listener = new AgentServer(store, platform, pairing, identity.HostId, new PowerShellTerminal());
        server = listener;
        listener.Diagnostic += text => Dispatcher.BeginInvoke(() => RecordDiagnostic(text));
        listener.SetPaused(controlPaused);
        serverCertificate = identity.CreateCertificate(addresses);
        await listener.StartAsync(serverCertificate, UsbPort, LanPort, addresses, enableBluetooth: true);
    }

    private async Task StopServer()
    {
        var listener = server;
        server = null;
        if (listener != null)
            await listener.DisposeAsync();
        serverCertificate?.Dispose();
        serverCertificate = null;
    }

    private void StopBluetooth()
    {
        bluetooth?.Dispose();
        bluetooth = null;
        if (server != null)
            server.BluetoothAddress = null;
    }

    private async Task StartBluetooth()
    {
        if (quitting || server is not { } listener || bluetooth is { IsAvailable: true })
            return;
        StopBluetooth();
        var relay = new BluetoothRelay(listener.BluetoothPort);
        bluetooth = relay;
        try
        {
            var address = await relay.StartAsync();
            if (bluetooth != relay)
                return;
            listener.BluetoothAddress = address;
            bluetoothStatus.Text = "蓝牙已就绪";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or IOException or ArgumentException)
        {
            if (bluetooth != relay)
                return;
            StopBluetooth();
            bluetoothStatus.Text = "蓝牙未就绪 · " + ex.Message;
        }
    }

    private void ShowPairing()
    {
        if (server == null || restartingServer)
        {
            Report("请先启动连接服务");
            return;
        }
        pairingWindow?.Close();
        var session = pairing.Begin();
        var candidates = new List<object>
        {
            new { transport = "usb", url = $"wss://127.0.0.1:{UsbPort}/ws" },
        };
        foreach (var address in activeLanAddresses)
            candidates.Add(new { transport = "lan", url = $"wss://{address}:{LanPort}/ws" });
        var payload = Wire.Serialize(
            new
            {
                type = "magic-companion-pair",
                protocolVersion = 1,
                hostId = identity.HostId,
                hostName = Environment.MachineName,
                fingerprint = identity.Fingerprint,
                pairSessionId = session.Id,
                secret = Convert.ToBase64String(session.Secret),
                expiresAt = session.ExpiresAt,
                candidates,
                bluetoothAddress = server.BluetoothAddress,
            }
        );
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(png.GetGraphic(5));
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        var dialog = Dialog("手机扫码配对", 540, 650);
        pairingWindow = dialog;
        var p = new StackPanel { Margin = new Thickness(20) };
        p.Children.Add(Text("手机扫码后，请在电脑确认验证码。"));
        p.Children.Add(
            new System.Windows.Controls.Image
            {
                Source = bitmap,
                Height = 430,
                Stretch = Stretch.Uniform,
            }
        );
        var remaining = Text("二维码 120 秒内有效");
        p.Children.Add(remaining);
        dialog.Content = p;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var seconds = Math.Max(
                0,
                (int)(DateTimeOffset.Parse(session.ExpiresAt) - DateTimeOffset.UtcNow).TotalSeconds
            );
            remaining.Text =
                seconds > 0 && pairing.Active
                    ? $"剩余 {seconds} 秒"
                    : "二维码已使用或过期，请关闭后重新生成";
            if (!pairing.Active)
            {
                bitmap = null!;
                ((System.Windows.Controls.Image)p.Children[1]).Source = null;
                timer.Stop();
            }
        };
        timer.Start();
        dialog.Closed += (_, _) =>
        {
            timer.Stop();
            pairing.Cancel();
            pairingWindow = null;
        };
        dialog.Show();
    }

    private async Task<string[]?> ApprovePairing(
        PairingRequest request,
        CancellationToken cancellation
    )
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested)
                return null;
            var dialog = Dialog("确认配对手机", 500, 660);
            var p = new StackPanel { Margin = new Thickness(24) };
            dialog.Content = p;
            p.Children.Add(
                Text($"设备：{request.DeviceName}\n请核对手机验证码：{request.ShortCode}", 20)
            );
            var checks = PermissionSet
                .All.Select(x =>
                    (
                        Key: x,
                        Box: new CheckBox
                        {
                            Content = permissionNames[x],
                            IsChecked = PermissionSet.Basic.Contains(x),
                            Margin = new Thickness(0, 4, 0, 4),
                        }
                    )
                )
                .ToArray();
            foreach (var check in checks)
                p.Children.Add(check.Box);
            string[]? result = null;
            p.Children.Add(
                Button(
                    "确认配对",
                    () =>
                    {
                        result = PermissionSet.Normalize(checks
                            .Where(x => x.Box.IsChecked == true)
                            .Select(x => x.Key));
                        dialog.Close();
                    }
                )
            );
            p.Children.Add(Button("拒绝", () => dialog.Close()));
            using var registration = cancellation.Register(
                () => Dispatcher.BeginInvoke(() => dialog.Close())
            );
            dialog.ShowDialog();
            return result;
        });
    }

    private Window Dialog(string title, double width, double height) =>
        new()
        {
            Owner = this,
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Background = Background,
        };

    private bool RegisterQuickHotkey(string text, bool persist)
    {
        try
        {
            var parts = text.ToUpperInvariant()
                .Split('+', StringSplitOptions.TrimEntries);
            if (parts.Length is < 2 or > 4 || parts.Distinct().Count() != parts.Length)
                throw new AgentException(
                    "INVALID_ARGUMENT",
                    "快捷键需包含修饰键和一个普通键，例如 CTRL+`"
                );
            uint modifiers = 0x4000;
            foreach (var part in parts[..^1])
                modifiers |= part switch
                {
                    "CTRL" => 2u,
                    "ALT" => 1u,
                    "SHIFT" => 4u,
                    _ => throw new AgentException(
                        "INVALID_ARGUMENT",
                        "修饰键只允许 CTRL、ALT、SHIFT"
                    ),
                };
            var code = NativeMethods.ParseKey(parts[^1]);
            if (code is 16 or 17 or 18)
                throw new AgentException("INVALID_ARGUMENT", "最后一项需为普通键");
            if (source == null)
                return false;
            if (quickHotkey != (modifiers, (uint)code))
            {
                // Register the replacement first so an occupied key never removes the current shortcut.
                var nextId = quickHotkeyId == 1101 ? 1102 : 1101;
                if (!NativeMethods.RegisterHotKey(source.Handle, nextId, modifiers, code))
                {
                    Report("快捷键已被占用，仍可从托盘打开待办悬浮窗");
                    return false;
                }
                if (quickHotkeyId != 0)
                    NativeMethods.UnregisterHotKey(source.Handle, quickHotkeyId);
                quickHotkeyId = nextId;
                quickHotkey = (modifiers, code);
            }
            if (persist)
                store.SetSetting("quickHotkey", string.Join('+', parts));
            return true;
        }
        catch (AgentException ex)
        {
            Report(ex.Message);
            return false;
        }
    }

    private string? Prompt(string title, string value)
    {
        var dialog = Dialog(title, 520, 200);
        var panel = new StackPanel { Margin = new Thickness(20) };
        var input = new TextBox { Text = value, MaxLength = 160 };
        panel.Children.Add(input);
        string? result = null;
        panel.Children.Add(
            Button(
                "保存",
                () =>
                {
                    result = input.Text;
                    dialog.Close();
                }
            )
        );
        dialog.Content = panel;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        dialog.ShowDialog();
        return result;
    }

    private void ShowConsole()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && quickHotkeyId != 0 && wParam == quickHotkeyId)
        {
            QuickTodo();
            handled = true;
        }
        return 0;
    }

    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (
            e.Reason == SessionSwitchReason.SessionLock
            || e.Reason == SessionSwitchReason.SessionLogoff
        )
            Dispatcher.BeginInvoke(() => SetControlPaused(true));
    }

    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Dispatcher.BeginInvoke(() => SetControlPaused(true));
    }

    private void SetControlPaused(bool paused)
    {
        controlPaused = paused;
        server?.SetPaused(paused);
    }

    private async void CloseApplication()
    {
        if (quitting)
            return;
        quitting = true;
        refresh.Stop();
        await connectionUpdate;
        pairingWindow?.Close();
        pairing.Cancel();
        tray.Visible = false;
        tray.Dispose();
        trayIcon?.Dispose();
        adb?.Dispose();
        StopDiscovery();
        StopBluetooth();
        quickTodoWindow?.Close();
        if (source != null && quickHotkeyId != 0)
            NativeMethods.UnregisterHotKey(source.Handle, quickHotkeyId);
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        await StopServer();
        platform.Dispose();
        identity.Dispose();
        store.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
