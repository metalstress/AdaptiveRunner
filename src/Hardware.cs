using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnowRunnerApex5;

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, UIntPtr size);
    [DllImport("kernel32.dll")]
    internal static extern bool UnmapViewOfFile(IntPtr address);
    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("xinput1_4.dll")]
    internal static extern uint XInputGetState(uint index, out XInputState state);
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputState
{
    public uint Packet;
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LeftX, LeftY, RightX, RightY;
}

internal sealed record TruckState(
    ulong Vehicle, float Rpm, float Boost, float Speed, float Redline,
    float BoostTop, int Gear, uint Flags, uint Lamps, ulong Tick,
    byte? GearMode = null, byte? GearboxLevel = null)
{
    public bool Driving => Vehicle != 0 && (Flags & 1) != 0;
    public bool EngineOn => (Flags & 4) != 0;
    public string? GearModeName => GearMode switch
    {
        0 => "Number", 1 => "Automatic", 2 => "H", 3 => "L-", 4 => "L", 5 => "L+", _ => null
    };
}

internal sealed class GaugesReader : IDisposable
{
    private IntPtr view;
    public int ProcessId { get; private set; }

    public bool Connect()
    {
        Dispose();
        foreach (Process process in Process.GetProcessesByName("SnowRunner"))
        {
            using (process)
            {
                IntPtr mapping = Native.OpenFileMapping(4, false,
                    $"Local\\SnowRunnerGauges.State.{process.Id}");
                if (mapping == IntPtr.Zero)
                    continue;
                view = Native.MapViewOfFile(mapping, 4, 0, 0, UIntPtr.Zero);
                Native.CloseHandle(mapping);
                if (view != IntPtr.Zero)
                {
                    ProcessId = process.Id;
                    return true;
                }
            }
        }
        return false;
    }

    public TruckState? Read()
    {
        if (view == IntPtr.Zero)
            return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int before = Marshal.ReadInt32(view, 12);
            if ((before & 1) != 0)
                continue;
            Thread.MemoryBarrier();
            int size = Marshal.ReadInt32(view, 8);
            if (Marshal.ReadInt32(view) != 0x47554147 || size < 64)
                return null;
            byte[] copy = new byte[256];
            Marshal.Copy(view, copy, 0, Math.Min(size, copy.Length));
            Thread.MemoryBarrier();
            if (Marshal.ReadInt32(view, 12) != before)
                continue;
            ulong tick = BitConverter.ToUInt64(copy, 16);
            if (tick > Native.GetTickCount64() || Native.GetTickCount64() - tick > 2000)
                return null;
            float Value(int offset) => size >= offset + 4 ? BitConverter.ToSingle(copy, offset) : 0;
            float[] values = [Value(40), Value(44), Value(64), Value(72), Value(100)];
            if (values.Any(value => !float.IsFinite(value)))
                return null;
            return new TruckState(BitConverter.ToUInt64(copy, 24), values[0], values[1],
                values[2], values[3], values[4], size >= 84 ? BitConverter.ToInt32(copy, 80) : 0,
                BitConverter.ToUInt32(copy, 48), size >= 128 ? BitConverter.ToUInt32(copy, 124) : 0, tick,
                size >= 85 ? copy[84] : null, size >= 87 ? copy[86] : null);
        }
        return null;
    }

    public bool IsForeground()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foreground);
        return foreground == ProcessId;
    }

    public void Dispose()
    {
        if (view != IntPtr.Zero)
            Native.UnmapViewOfFile(view);
        view = IntPtr.Zero;
        ProcessId = 0;
    }
}

internal sealed record TriggerEffect(byte Mode, byte Start = 0, byte Pressure = 0,
    byte Strength = 0, byte Frequency = 0)
{
    public static readonly TriggerEffect Off = new(0);

    public byte[] Packet(byte side)
    {
        // Flydigi NewXInput SetForceTrigger. Match-input stays off so the
        // effect cannot shorten or rescale either pedal's analogue travel.
        byte[] packet = new byte[32];
        packet[0] = 3;
        packet[1] = 0x5A;
        packet[2] = 0xA5;
        packet[3] = 81;
        packet[4] = 10;
        packet[5] = 1;
        packet[6] = side;
        packet[7] = Mode;
        packet[8] = Start;
        packet[9] = Pressure;
        packet[10] = Strength;
        packet[11] = Frequency;
        return packet;
    }
}

internal sealed class ApexController : IDisposable
{
    private IntPtr handle;
    private readonly Dictionary<byte, TriggerEffect> lastEffects = new();
    private long lastRefresh;
    public int Acknowledged { get; private set; }
    public long FrameId { get; set; }
    public string Product { get; private set; } = "";
    public bool Connected => handle != IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct HidInfo
    {
        public IntPtr Path;
        public ushort Vendor, ProductId;
        public IntPtr Serial;
        public ushort Release;
        public IntPtr Manufacturer, ProductName;
        public ushort UsagePage, Usage;
        public int Interface;
        public IntPtr Next;
    }

    static ApexController()
    {
        NativeLibrary.SetDllImportResolver(typeof(ApexController).Assembly, (name, _, _) =>
            name == "flydigi_hidapi"
                ? NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles), "Flydigi Space Station", "hidapi.dll"))
                : IntPtr.Zero);
    }

    [DllImport("flydigi_hidapi", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hid_enumerate(ushort vendor, ushort product);
    [DllImport("flydigi_hidapi", CallingConvention = CallingConvention.Cdecl)]
    private static extern void hid_free_enumeration(IntPtr list);
    [DllImport("flydigi_hidapi", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hid_open_path(IntPtr path);
    [DllImport("flydigi_hidapi", CallingConvention = CallingConvention.Cdecl)]
    private static extern void hid_close(IntPtr device);
    [DllImport("flydigi_hidapi", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_write(IntPtr device, byte[] data, UIntPtr length);
    [DllImport("flydigi_hidapi", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_read_timeout(IntPtr device, byte[] data, UIntPtr length, int timeout);

    public bool Connect()
    {
        Dispose();
        IntPtr list = hid_enumerate(0x37D7, 0);
        try
        {
            for (IntPtr item = list; item != IntPtr.Zero;)
            {
                HidInfo info = Marshal.PtrToStructure<HidInfo>(item);
                string product = Marshal.PtrToStringUni(info.ProductName) ?? "";
                if (info.UsagePage == 0xFFA0 && product.Contains("APEX5", StringComparison.OrdinalIgnoreCase))
                {
                    handle = hid_open_path(info.Path);
                    if (handle != IntPtr.Zero)
                    {
                        Product = product.Trim();
                        Program.Trace("controller_connected", new { Product, FrameId,
                            info.Vendor, info.ProductId, info.UsagePage, info.Interface });
                        return true;
                    }
                }
                item = info.Next;
            }
        }
        finally
        {
            hid_free_enumeration(list);
        }
        return false;
    }

    public void SetEffects(TriggerEffect left, TriggerEffect right, bool force = false)
    {
        bool refresh = (left != TriggerEffect.Off || right != TriggerEffect.Off) &&
            Environment.TickCount64 - lastRefresh > 700;
        SetSide(1, left, force || refresh);
        SetSide(2, right, force || refresh);
        if (refresh || force)
            lastRefresh = Environment.TickCount64;
    }

    public void ReleaseEffects()
    {
        IOException? failure = null;
        foreach (byte side in new byte[] { 1, 2 })
        {
            try { SetSide(side, TriggerEffect.Off, force: true); }
            catch (IOException error) { failure ??= error; }
        }
        if (failure is not null)
            throw failure;
    }

    private void SetSide(byte side, TriggerEffect effect, bool force)
    {
        if (!force && lastEffects.TryGetValue(side, out TriggerEffect? previous) && previous == effect)
            return;
        byte[] packet = effect.Packet(side);
        long started = Environment.TickCount64;
        int written = handle == IntPtr.Zero ? -1 : hid_write(handle, packet, (UIntPtr)packet.Length);
        Program.Trace("hid_write", new { FrameId, Side = side, Effect = effect, Forced = force,
            Packet = Convert.ToHexString(packet), BytesWritten = written, ExpectedLength = packet.Length,
            ElapsedMs = Environment.TickCount64 - started, Completed = written == packet.Length });
        if (written != packet.Length)
            throw new IOException("Apex 5 stopped accepting trigger commands.");
        lastEffects[side] = effect;
    }

    public void ReadAcknowledgements()
    {
        byte[] buffer = new byte[64];
        for (int i = 0; i < 100; i++)
        {
            int count = hid_read_timeout(handle, buffer, (UIntPtr)buffer.Length, 0);
            if (count < 0)
                throw new IOException("Apex 5 disconnected.");
            if (count == 0)
                return;
            bool triggerAck = count >= 7 && buffer[1] == 0x5A && buffer[2] == 0xA5 &&
                buffer[3] == 81 && buffer[6] == 1;
            if (count >= 4 && buffer[1] == 0x5A && buffer[2] == 0xA5)
                Program.Trace("hid_reply", new { FrameId, CommandId = buffer[3],
                    RawReport = Convert.ToHexString(buffer.AsSpan(0, count)),
                    CountedAsTriggerAck = triggerAck, Source = "shared_hid_stream" });
            if (triggerAck)
                Acknowledged++;
        }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            Program.Trace("controller_closed", new { FrameId, Product });
            hid_close(handle);
        }
        handle = IntPtr.Zero;
        lastEffects.Clear();
    }
}
