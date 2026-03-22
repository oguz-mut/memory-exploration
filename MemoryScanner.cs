using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;

public class MemoryScanner
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProc, IntPtr baseAddr, byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(IntPtr hProc, IntPtr addr, out MEMORY_BASIC_INFORMATION64 info, IntPtr length);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    static void Main(string[] args)
    {
        int pid = int.Parse(args[0]);
        int maxRegions = args.Length > 1 ? int.Parse(args[1]) : 200;

        IntPtr h = OpenProcess(0x0410, false, pid); // QUERY_INFO | VM_READ
        if (h == IntPtr.Zero)
        {
            Console.WriteLine("Failed to open process. Error: " + Marshal.GetLastWin32Error());
            return;
        }

        var info = new MEMORY_BASIC_INFORMATION64();
        int infoSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64));
        ulong addr = 0;
        int scanned = 0;
        long totalRead = 0;

        // Memory region stats
        long privateCommit = 0, mappedCommit = 0, imageCommit = 0;
        int privateCount = 0, mappedCount = 0, imageCount = 0;
        var protectionStats = new Dictionary<string, long>();

        // String collection
        var strings = new Dictionary<string, int>();

        while (true)
        {
            IntPtr ret = VirtualQueryEx(h, (IntPtr)addr, out info, (IntPtr)infoSize);
            if (ret == IntPtr.Zero) break;

            if (info.State == 0x1000) // MEM_COMMIT
            {
                long sz = (long)info.RegionSize;
                string typeStr;
                switch (info.Type)
                {
                    case 0x20000: typeStr = "Private"; privateCommit += sz; privateCount++; break;
                    case 0x40000: typeStr = "Mapped"; mappedCommit += sz; mappedCount++; break;
                    case 0x1000000: typeStr = "Image"; imageCommit += sz; imageCount++; break;
                    default: typeStr = "Other"; break;
                }

                string protStr = GetProtectionString(info.Protect);
                string key = typeStr + "|" + protStr;
                if (!protectionStats.ContainsKey(key)) protectionStats[key] = 0;
                protectionStats[key] += sz;

                // Read readable regions
                bool isReadable = (info.Protect & 0x66) != 0;
                if (isReadable && info.RegionSize > 0 && info.RegionSize < 100000000)
                {
                    int readSize = (int)Math.Min(info.RegionSize, 4 * 1024 * 1024);
                    byte[] buf = new byte[readSize];
                    IntPtr bytesRead;

                    if (ReadProcessMemory(h, (IntPtr)info.BaseAddress, buf, (IntPtr)readSize, out bytesRead))
                    {
                        int read = (int)bytesRead;
                        totalRead += read;
                        ExtractStrings(buf, read, 10, strings);
                        scanned++;
                    }
                    if (scanned >= maxRegions) break;
                }
            }

            ulong next = info.BaseAddress + info.RegionSize;
            if (next <= addr) break;
            addr = next;
        }

        CloseHandle(h);

        // Output results
        Console.WriteLine("=== MEMORY REGION BREAKDOWN ===");
        Console.WriteLine("Private: {0} regions, {1:N0} MB", privateCount, privateCommit / 1048576.0);
        Console.WriteLine("Mapped:  {0} regions, {1:N0} MB", mappedCount, mappedCommit / 1048576.0);
        Console.WriteLine("Image:   {0} regions, {1:N0} MB", imageCount, imageCommit / 1048576.0);
        Console.WriteLine();

        Console.WriteLine("=== BY TYPE & PROTECTION ===");
        foreach (var kv in protectionStats.OrderByDescending(x => x.Value))
        {
            Console.WriteLine("  {0,-35} {1,10:N1} MB", kv.Key, kv.Value / 1048576.0);
        }

        Console.WriteLine();
        Console.WriteLine("Scanned {0} regions, read {1:N0} MB, found {2:N0} unique strings", scanned, totalRead / 1048576.0, strings.Count);
        Console.WriteLine();

        // Categorize strings
        PrintCategory("GAME ENTITIES / MECHANICS", strings, new[] {
            "Player", "Monster", "NPC", "Item", "Quest", "Skill", "Inventory",
            "Spell", "Armor", "Weapon", "Health", "Damage", "Gorgon", "Level",
            "Ability", "Recipe", "Loot", "Combat", "XP", "Buff", "Debuff"
        }, 40);

        PrintCategory("ASSET / FILE PATHS", strings, new[] {
            ".png", ".jpg", ".asset", ".prefab", ".unity", ".bundle", ".shader",
            ".mat", ".mesh", ".ogg", ".wav", ".mp3", ".dll", ".json", ".xml",
            "Assets/", "Resources/", "Textures/", "Materials/"
        }, 30);

        PrintCategory("NETWORK / SERVER", strings, new[] {
            "http", "www.", ".com", "socket", "server", "connect", "tcp",
            "port", "packet", "auth", "login", "session"
        }, 20);

        PrintCategory("UNITY ENGINE", strings, new[] {
            "UnityEngine", "GameObject", "Transform", "Renderer", "Shader",
            "MonoBehaviour", "Collider", "Camera", "Canvas", "Texture",
            "Material", "AssetBundle"
        }, 20);

        PrintCategory("UI / TEXT", strings, new[] {
            "Button", "Panel", "Window", "Menu", "Dialog", "Chat", "Message",
            "Label", "Tooltip", "Click", "Select"
        }, 20);

        // Also dump some of the longest unique strings (often the most interesting)
        Console.WriteLine();
        Console.WriteLine("=== LONGEST STRINGS (often most interesting) ===");
        var longest = strings.Keys.OrderByDescending(s => s.Length).Take(30);
        foreach (var s in longest)
        {
            string display = s.Length > 150 ? s.Substring(0, 150) + "..." : s;
            Console.WriteLine("  [{0}] {1}", s.Length, display);
        }
    }

    static void ExtractStrings(byte[] buf, int length, int minLen, Dictionary<string, int> results)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            byte b = buf[i];
            if (b >= 32 && b < 127)
            {
                sb.Append((char)b);
            }
            else
            {
                if (sb.Length >= minLen)
                {
                    string s = sb.ToString();
                    if (!results.ContainsKey(s)) results[s] = 0;
                    results[s]++;
                }
                sb.Clear();
            }
        }
        if (sb.Length >= minLen)
        {
            string s = sb.ToString();
            if (!results.ContainsKey(s)) results[s] = 0;
            results[s]++;
        }
    }

    static void PrintCategory(string name, Dictionary<string, int> strings, string[] keywords, int max)
    {
        var matches = strings.Keys
            .Where(s => keywords.Any(k => s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderByDescending(s => s.Length)
            .Take(max)
            .ToArray();

        if (matches.Length == 0) return;

        Console.WriteLine("=== {0} ({1} matches) ===", name, matches.Length);
        foreach (var s in matches)
        {
            string display = s.Length > 130 ? s.Substring(0, 130) + "..." : s;
            Console.WriteLine("  {0}", display);
        }
        Console.WriteLine();
    }

    static string GetProtectionString(uint protect)
    {
        switch (protect)
        {
            case 0x01: return "NoAccess";
            case 0x02: return "ReadOnly";
            case 0x04: return "ReadWrite";
            case 0x08: return "WriteCopy";
            case 0x10: return "Execute";
            case 0x20: return "ExecRead";
            case 0x40: return "ExecReadWrite";
            case 0x80: return "ExecWriteCopy";
            default: return "0x" + protect.ToString("X");
        }
    }
}
