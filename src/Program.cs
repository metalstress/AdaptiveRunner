using System.Diagnostics;
using System.Text.Json;

namespace SnowRunnerApex5;

internal static class Program
{
    internal static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowRunnerApex5");
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static DiagnosticLog? Diagnostics { get; private set; }

    internal static void Trace(string kind, object data) => Diagnostics?.Write(kind, data);

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(DataFolder);
        if (args.Contains("--check-logging"))
        {
            DiagnosticLog check = new("diagnostics-check.jsonl");
            check.Write("logging_check", new { GameAccess = false, ControllerAccess = false,
                BuildId = typeof(Program).Assembly.ManifestModule.ModuleVersionId });
            check.Dispose();
            if (check.Error is not null)
                throw new IOException(check.Error);
            return;
        }
        if (args.Contains("--stop"))
        {
            if (EventWaitHandle.TryOpenExisting("Local\\SnowRunnerApex5.Stop", out EventWaitHandle? stop))
                using (stop) stop.Set();
            return;
        }
        if (args.Contains("--self-test"))
        {
            Log("Gameplay self-tests are not supported. Gameplay testing is manual.");
            return;
        }
        if (args.Contains("--diagnose"))
        {
            using GaugesReader gauges = new();
            using ApexController controller = new();
            using PhysicsReader physics = new();
            bool hasGauges = gauges.Connect();
            bool hasController = controller.Connect();
            physics.Connect(gauges.ProcessId);
            TruckState? truck = gauges.Read();
            List<object> inputs = [];
            for (uint i = 0; i < 4; i++)
                if (Native.XInputGetState(i, out XInputState state) == 0)
                    inputs.Add(new { Index = i, state.LeftTrigger, state.RightTrigger });
            File.WriteAllText(Path.Combine(DataFolder, "diagnostics.json"), JsonSerializer.Serialize(
                new { HasGauges = hasGauges, HasController = hasController, controller.Product,
                    gauges.ProcessId, Truck = truck, Physics = physics.Read(truck), Inputs = inputs }, JsonOptions));
            return;
        }

        using Process? launchGame = GetLaunchGame(args, out bool validLaunch);
        if (!validLaunch)
            return;
        using Mutex singleInstance = new(true, "Local\\SnowRunnerApex5.Running", out bool first);
        if (!first)
        {
            Program.Log("Launch skipped: an instance is already running.");
            return;
        }
        using DiagnosticLog diagnostics = new();
        Diagnostics = diagnostics;
        Trace("session_start", new { BuildId = typeof(Program).Assembly.ManifestModule.ModuleVersionId,
            AppName = "AdaptiveRunner",
            AppProcessId = Environment.ProcessId, FrameIntervalMs = 50,
            AppExecutable = Environment.ProcessPath, LaunchGameProcessId = launchGame?.Id,
            AckNote = "HID replies can also belong to other clients; ACK is not a tactile validation." });
        ApplicationConfiguration.Initialize();
        using TrayContext context = new(launchGame);
        Application.Run(context);
    }

    private static Process? GetLaunchGame(string[] args, out bool valid)
    {
        valid = true;
        int index = Array.IndexOf(args, "--game-pid");
        if (index < 0)
            return null;
        valid = false;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid) || pid <= 0)
        {
            Log("Invalid --game-pid; launch cancelled.");
            return null;
        }
        Process? game = null;
        try
        {
            game = Process.GetProcessById(pid);
            if (!game.ProcessName.Equals("SnowRunner", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The parent is not SnowRunner.");
            game.EnableRaisingEvents = true;
            if (game.HasExited)
                throw new InvalidOperationException("SnowRunner has already exited.");
            valid = true;
            return game;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            game?.Dispose();
            Log("Game lifecycle binding failed: " + error.Message);
            return null;
        }
    }

    internal static void Log(string text)
    {
        string path = Path.Combine(DataFolder, "bridge.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
            File.Move(path, path + ".previous", true);
        File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
    }
}

internal sealed record LiveStatus(string Status, bool Active = false, float Rpm = 0,
    float Speed = 0, int Gear = 0, float WheelSpeed = 0, int GroundedWheels = 0,
    float Throttle = 0, byte LeftPedal = 0, byte RightPedal = 0,
    byte LeftMode = 0, byte RightMode = 0, byte LeftStrength = 0, byte RightStrength = 0,
    int Acknowledged = 0, int ProcessId = 0, DateTimeOffset Updated = default,
    bool Automatic = false, bool BrakeLights = false, int GearSelected = 0,
    byte? GearMode = null, string? GearModeName = null, byte? GearboxLevel = null);

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly Icon applicationIcon;
    private readonly ToolStripMenuItem statusItem = new("Подключение…") { Enabled = false };
    private readonly ToolStripMenuItem enabledItem = new("Отдача включена") { Checked = true, CheckOnClick = true };
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 500 };
    private readonly CancellationTokenSource cancellation = new();
    private readonly EventWaitHandle stopEvent = new(false, EventResetMode.AutoReset, "Local\\SnowRunnerApex5.Stop");
    private readonly Task worker;
    private readonly Process? launchGame;
    private volatile bool enabled = true;
    private double intensity = 1.15;
    private LiveStatus current = new("Подключение…");

    public TrayContext(Process? launchGame = null)
    {
        this.launchGame = launchGame;
        string settings = Path.Combine(Program.DataFolder, "settings.json");
        try
        {
            if (File.Exists(settings))
                intensity = Math.Clamp(JsonSerializer.Deserialize<double>(File.ReadAllText(settings)), 0.4, 1.5);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            Program.Log("Settings: " + error.Message);
        }
        ContextMenuStrip menu = new();
        menu.Items.Add(new ToolStripMenuItem("AdaptiveRunner") { Enabled = false });
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        enabledItem.CheckedChanged += (_, _) =>
        {
            enabled = enabledItem.Checked;
            Program.Trace("feedback_enabled", new { Enabled = enabled });
        };
        menu.Items.Add(enabledItem);
        ToolStripMenuItem strength = new("Сила отдачи");
        foreach ((string name, double value) in new[] { ("Мягкая", 0.8), ("Умеренная", 1.15), ("Выраженная", 1.5) })
        {
            ToolStripMenuItem item = new(name) { Checked = Math.Abs(intensity - value) < 0.01 };
            item.Click += (_, _) =>
            {
                Volatile.Write(ref intensity, value);
                Program.Trace("intensity_changed", new { Intensity = value });
                foreach (ToolStripMenuItem sibling in strength.DropDownItems)
                    sibling.Checked = ReferenceEquals(item, sibling);
                File.WriteAllText(settings, JsonSerializer.Serialize(value));
            };
            strength.DropDownItems.Add(item);
        }
        menu.Items.Add(strength);
        menu.Items.Add("Открыть журнал", null, (_, _) =>
            Process.Start(new ProcessStartInfo(Program.DataFolder) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выключить и выйти", null, (_, _) => ExitThread());
        using (Stream iconStream = typeof(Program).Assembly.GetManifestResourceStream("AdaptiveRunner.Icon")
            ?? throw new InvalidOperationException("AdaptiveRunner icon resource is missing."))
        using (Icon embeddedIcon = new(iconStream, SystemInformation.SmallIconSize))
            applicationIcon = (Icon)embeddedIcon.Clone();
        icon = new NotifyIcon { Icon = applicationIcon, Text = "AdaptiveRunner",
            ContextMenuStrip = menu, Visible = true };
        uiTimer.Tick += (_, _) =>
        {
            if (this.launchGame?.HasExited == true)
            {
                Program.Trace("launch_game_exited", new { GameProcessId = this.launchGame.Id });
                ExitThread();
                return;
            }
            LiveStatus state = Volatile.Read(ref current);
            statusItem.Text = state.Status;
            string tooltip = "AdaptiveRunner · " + state.Status;
            icon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
            if (stopEvent.WaitOne(0))
                ExitThread();
        };
        uiTimer.Start();
        Program.Trace("settings_loaded", new { Intensity = intensity, Enabled = enabled });
        worker = Task.Run(RunFeedback);
        Program.Log("Started. Smooth pedal pressure and bounded shift pulse; NextGen telemetry + read-only wheel physics.");
    }

    private async Task RunFeedback()
    {
        using GaugesReader gauges = new();
        using PhysicsReader physics = new();
        using ApexController controller = new();
        Feedback feedback = new();
        long retryAt = 0;
        long lastReport = 0;
        long frameId = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                controller.FrameId = ++frameId;
                try
                {
                    if (!controller.Connected && now >= retryAt)
                    {
                        if (controller.Connect())
                            controller.ReleaseEffects();
                        retryAt = now + 2000;
                    }
                    TruckState? truck = gauges.Read();
                    if (truck is null && now - lastReport > 1000)
                    {
                        gauges.Connect();
                        truck = gauges.Read();
                    }
                    physics.Connect(gauges.ProcessId);
                    PhysicsState? raw = physics.Read(truck);
                    XInputState input = default;
                    bool hasInput = false;
                    for (uint index = 0; index < 4; index++)
                    {
                        if (Native.XInputGetState(index, out input) != 0)
                            continue;
                        hasInput = true;
                        break;
                    }
                    bool foreground = gauges.IsForeground();
                    bool active = enabled && hasInput && foreground && truck?.Driving == true;
                    double frameIntensity = Volatile.Read(ref intensity);
                    var (left, right) = feedback.Calculate(truck, raw, input.LeftTrigger, input.RightTrigger,
                        active, frameIntensity, now);
                    Program.Trace("feedback_frame", new { FrameId = frameId, Enabled = enabled,
                        HasInput = hasInput, Foreground = foreground, Active = active,
                        Intensity = frameIntensity, GameProcessId = gauges.ProcessId,
                        Truck = truck, Physics = raw, Input = new { input.Packet, input.Buttons,
                            input.LeftX, input.LeftY, input.RightX, input.RightY },
                        LeftPedal = input.LeftTrigger, RightPedal = input.RightTrigger,
                        LeftEffect = left, RightEffect = right, Decision = feedback.Decision,
                        ControllerConnected = controller.Connected });
                    if (controller.Connected)
                    {
                        controller.SetEffects(left, right);
                        controller.ReadAcknowledgements();
                    }
                    string status = !controller.Connected ? "Подключи Apex 5 через USB или приёмник" :
                        truck is null ? "Ожидание SnowRunner с NextGen Gauges" :
                        !enabled ? "Отдача выключена" :
                        !active ? "Курки свободны · ожидание езды" :
                        $"{truck.Rpm:0} об/мин · {Math.Abs(truck.Speed):0.0} км/ч · передача {truck.Gear}";
                    if (Program.Diagnostics?.Error is not null)
                        status = "Журнал диагностики недоступен · " + status;
                    LiveStatus snapshot = new(status, active, truck?.Rpm ?? 0, truck?.Speed ?? 0,
                        truck?.Gear ?? 0, raw?.WheelSpeed ?? 0, raw?.GroundedDrivenWheels ?? 0,
                        raw?.Throttle ?? 0, input.LeftTrigger, input.RightTrigger,
                        left.Mode, right.Mode, left.Mode == 1 ? left.Pressure : left.Strength,
                        right.Mode == 1 ? right.Pressure : right.Strength,
                        controller.Acknowledged, gauges.ProcessId, DateTimeOffset.Now,
                        raw?.Automatic ?? false, raw?.BrakeLights ?? false, raw?.GearSelected ?? 0,
                        truck?.GearMode, truck?.GearModeName, truck?.GearboxLevel);
                    Volatile.Write(ref current, snapshot);
                    if (now - lastReport >= 1000)
                    {
                        string json = JsonSerializer.Serialize(snapshot, Program.JsonOptions);
                        string temporary = Path.Combine(Program.DataFolder, "status.tmp");
                        File.WriteAllText(temporary, json);
                        File.Move(temporary, Path.Combine(Program.DataFolder, "status.json"), true);
                        Program.Log(JsonSerializer.Serialize(snapshot));
                        lastReport = now;
                    }
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or
                    System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
                {
                    Program.Log("Connection: " + error.Message);
                    Program.Trace("connection_error", new { FrameId = frameId, Error = error.ToString() });
                    if (controller.Connected)
                    {
                        try { controller.ReleaseEffects(); }
                        catch (IOException releaseError)
                        {
                            Program.Log("Release: " + releaseError.Message);
                            Program.Trace("release_error", new { FrameId = frameId, Error = releaseError.ToString() });
                        }
                    }
                    controller.Dispose();
                    feedback.Reset();
                    retryAt = now + 2000;
                    Volatile.Write(ref current, new LiveStatus("Переподключение Apex 5…"));
                }
                await Task.Delay(50, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (controller.Connected)
            {
                try { controller.ReleaseEffects(); }
                catch (IOException error)
                {
                    Program.Log("Release: " + error.Message);
                    Program.Trace("release_error", new { FrameId = frameId, Error = error.ToString() });
                }
            }
            LiveStatus stopped = new("Программа остановлена", Updated: DateTimeOffset.Now);
            Volatile.Write(ref current, stopped);
            File.WriteAllText(Path.Combine(Program.DataFolder, "status.json"),
                JsonSerializer.Serialize(stopped, Program.JsonOptions));
            Program.Log("Stopped; trigger effects released.");
            Program.Trace("session_stop", new { FrameId = frameId });
        }
    }

    protected override void ExitThreadCore()
    {
        uiTimer.Stop();
        cancellation.Cancel();
        worker.GetAwaiter().GetResult();
        icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            uiTimer.Dispose();
            icon.Dispose();
            applicationIcon.Dispose();
            stopEvent.Dispose();
            cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
