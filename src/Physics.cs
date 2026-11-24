using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnowRunnerApex5;

internal sealed record PhysicsState(float Throttle, float Pull,
    float WheelSpeed, int GroundedDrivenWheels,
    bool Automatic = true, bool BrakeLights = false, int GearSelected = 1);

internal sealed class PhysicsReader : IDisposable
{
    private IntPtr process;
    private int processId;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, UIntPtr address,
        byte[] buffer, UIntPtr size, out UIntPtr read);

    public bool Connect(int pid)
    {
        if (pid == processId)
            return process != IntPtr.Zero;
        Dispose();
        processId = pid;
        if (pid == 0)
            return false;
        try
        {
            using Process game = Process.GetProcessById(pid);
            string? path = game.MainModule?.FileName;
            if (path is null)
                return false;
            using BinaryReader file = new(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            file.BaseStream.Position = 0x3C;
            int pe = file.ReadInt32();
            file.BaseStream.Position = pe;
            if (file.ReadUInt32() != 0x4550)
                return false;
            file.BaseStream.Position = pe + 8;
            // The user's running November 2, 2026 Steam build, verified against
            // NextGen's truck.h. Other builds retain shared-memory RPM effects.
            if (file.ReadUInt32() != 0x6ADB8627)
                return false;
            process = OpenProcess(0x1010, false, pid); // QUERY_LIMITED_INFORMATION | VM_READ
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or ArgumentException)
        {
            return false;
        }
        return process != IntPtr.Zero;
    }

    private byte[]? Read(ulong address, int size)
    {
        if (process == IntPtr.Zero || address < 0x10000 || address > 0x00007FFFFFFFFFFF)
            return null;
        byte[] buffer = new byte[size];
        if (!ReadProcessMemory(process, (UIntPtr)address, buffer, (UIntPtr)size, out UIntPtr read) ||
            (ulong)read != (ulong)size)
            return null;
        return buffer;
    }

    public PhysicsState? Read(TruckState? truck)
    {
        if (truck is null || !truck.Driving)
            return null;
        byte[]? actionPointer = Read(truck.Vehicle + 0x68, 8);
        byte[]? wheelRange = Read(truck.Vehicle + 0x200, 16);
        byte[]? flags = Read(truck.Vehicle + 0x768, 1);
        if (actionPointer is null || wheelRange is null || flags is null)
            return null;
        ulong action = BitConverter.ToUInt64(actionPointer);
        byte[]? controls = Read(action + 0x38, 0x80);
        if (controls is null)
            return null;
        float throttle = BitConverter.ToSingle(controls, 0x0C);
        float pull = BitConverter.ToSingle(controls, 0x7C);
        int selectedGear = BitConverter.ToInt32(controls, 0x3C);
        if (!float.IsFinite(throttle) || Math.Abs(throttle) > 1.1 ||
            !float.IsFinite(pull) || pull < 0 || pull > 1.5 || selectedGear < -1 || selectedGear > 32)
            return null;
        bool awd = controls[0x11] != 0;
        ulong begin = BitConverter.ToUInt64(wheelRange);
        ulong end = BitConverter.ToUInt64(wheelRange, 8);
        if (end <= begin || end - begin > 32 * 8 || (end - begin) % 8 != 0)
            return null;
        byte[]? wheelModels = Read(begin, (int)(end - begin));
        if (wheelModels is null)
            return null;
        List<float> speeds = [];
        for (int i = 0; i < wheelModels.Length; i += 8)
        {
            byte[]? wheelPointer = Read(BitConverter.ToUInt64(wheelModels, i) + 0x2C8, 8);
            if (wheelPointer is null)
                continue;
            byte[]? wheel = Read(BitConverter.ToUInt64(wheelPointer), 0x1A8);
            if (wheel is null)
                continue;
            int drive = BitConverter.ToInt32(wheel, 0xE8);
            float radius = BitConverter.ToSingle(wheel, 0x94);
            float rotation = Math.Abs(BitConverter.ToSingle(wheel, 0x16C));
            bool driven = drive == 1 || (drive == 2 && awd);
            bool grounded = BitConverter.ToInt32(wheel, 0x1A4) != 0;
            if (driven && grounded && float.IsFinite(radius) && radius is > 0.1f and < 3 &&
                float.IsFinite(rotation) && rotation < 500)
                speeds.Add(radius * rotation);
        }
        // A median keeps a single unloaded or bouncing wheel from dominating.
        speeds.Sort();
        float median = speeds.Count > 0 ? speeds[speeds.Count / 2] : 0;
        return new PhysicsState(throttle, pull, median, speeds.Count,
            controls[4] != 0, (flags[0] & 32) != 0, selectedGear);
    }

    public void Dispose()
    {
        if (process != IntPtr.Zero)
            Native.CloseHandle(process);
        process = IntPtr.Zero;
        processId = 0;
    }
}
