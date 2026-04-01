using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MemoryLib;

public sealed class ProcessMemory : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProc, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr hProc, IntPtr addr, out MEMORY_BASIC_INFORMATION64 info, IntPtr len);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __align1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __align2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    internal readonly IntPtr Handle;

    private ProcessMemory(IntPtr handle)
    {
        Handle = handle;
    }

    public static int? FindGameProcess()
    {
        const uint TH32CS_SNAPPROCESS = 0x2;
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return null;

        try
        {
            var entry = new PROCESSENTRY32();
            entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            if (!Process32First(snapshot, ref entry))
                return null;

            do
            {
                string exe = entry.szExeFile ?? string.Empty;
                if (exe.IndexOf("WindowsPlayer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    exe.IndexOf("Gorgon", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (int)entry.th32ProcessID;
                }
            }
            while (Process32Next(snapshot, ref entry));

            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public static ProcessMemory Open(int pid)
    {
        IntPtr handle = OpenProcess(0x0410, false, pid);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to open process {pid}. Error: {Marshal.GetLastWin32Error()}");
        return new ProcessMemory(handle);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            CloseHandle(Handle);
    }

    public byte[]? ReadBytes(ulong addr, int count)
    {
        if (count <= 0) return null;
        byte[] buf = new byte[count];
        try
        {
            if (!ReadProcessMemory(Handle, (IntPtr)addr, buf, (IntPtr)count, out IntPtr bytesRead))
                return null;
            if ((long)bytesRead < count)
                return null;
            return buf;
        }
        catch
        {
            return null;
        }
    }

    public int ReadInt32(ulong addr)
    {
        byte[]? buf = ReadBytes(addr, 4);
        return buf == null ? 0 : BitConverter.ToInt32(buf, 0);
    }

    public ushort ReadUInt16(ulong addr)
    {
        byte[]? buf = ReadBytes(addr, 2);
        return buf == null ? (ushort)0 : BitConverter.ToUInt16(buf, 0);
    }

    public byte ReadByte(ulong addr)
    {
        byte[]? buf = ReadBytes(addr, 1);
        return buf == null ? (byte)0 : buf[0];
    }

    public float ReadFloat(ulong addr)
    {
        byte[]? buf = ReadBytes(addr, 4);
        return buf == null ? 0f : BitConverter.ToSingle(buf, 0);
    }

    public double ReadDouble(ulong addr)
    {
        byte[]? buf = ReadBytes(addr, 8);
        return buf == null ? 0.0 : BitConverter.ToDouble(buf, 0);
    }

    public bool ReadBool(ulong addr)
    {
        return ReadByte(addr) != 0;
    }

    public ulong ReadPointer(ulong addr)
    {
        byte[]? buf = ReadBytes(addr, 8);
        return buf == null ? 0UL : BitConverter.ToUInt64(buf, 0);
    }

    // Mono string layout: vtable(8) + sync(8) + length(4 at +0x10) + UTF-16 chars at +0x14
    public string? ReadMonoString(ulong addr, int maxLength = 256)
    {
        int length = ReadInt32(addr + 0x10);
        if (length < 1 || length > maxLength)
            return null;
        byte[]? chars = ReadBytes(addr + 0x14, length * 2);
        if (chars == null)
            return null;
        return Encoding.Unicode.GetString(chars);
    }

    public bool QueryRegion(ulong addr, out MEMORY_BASIC_INFORMATION64 info)
    {
        IntPtr size = (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64));
        return VirtualQueryEx(Handle, (IntPtr)addr, out info, size) != IntPtr.Zero;
    }
}
