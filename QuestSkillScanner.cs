using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class QuestSkillScanner
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
        long maxBytes = 2L * 1024 * 1024 * 1024; // scan up to 2GB

        IntPtr h = OpenProcess(0x0410, false, pid);
        if (h == IntPtr.Zero)
        {
            Console.WriteLine("Failed to open process. Error: " + Marshal.GetLastWin32Error());
            return;
        }

        var info = new MEMORY_BASIC_INFORMATION64();
        int infoSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64));
        ulong addr = 0;
        long totalRead = 0;
        int scanned = 0;

        // Broader keyword sets
        string[] questKeywords = {
            "quest", "objective", "mission", "task", "bounty", "reward", "favor",
            "complete", "deliver", "collect", "kill", "talk to", "bring", "find",
            "finished", "accepted", "progress", "requirement", "prerequisite",
            "hangout", "work order", "accomplishment"
        };
        string[] skillKeywords = {
            "skill", "ability", "spell", "recipe", "craft", "power", "talent",
            "training", "xp", "experience", "level", "combat", "sword", "fire magic",
            "ice magic", "psychology", "mentalism", "animal handling", "archery",
            "shield", "staff", "unarmed", "lycanthropy", "necromancy", "druid",
            "bard", "battle chemistry", "sigil", "calligraphy", "carpentry",
            "cooking", "brewing", "butchering", "skinning", "tanning", "tailoring",
            "leatherworking", "blacksmithing", "toolcrafting", "foraging",
            "gardening", "fishing", "mycology", "anatomy", "pathology",
            "first aid", "endurance", "armor patching", "fletching",
            "treasure hunting", "lore", "meditation", "performance", "dance",
            "industry", "augmentation", "synergy", "research"
        };
        string[] npcKeywords = {
            "npc", "vendor", "merchant", "trainer", "instructor", "council",
            "Elahil", "Azalak", "Velkort", "Yetta", "Flia", "Mushroom Jack",
            "Ivyn", "Sie Antry", "Marna", "Joeh", "Harry", "Blanche",
            "Therese", "Hulon", "Norala", "Tyler Green", "Jesina",
            "Serbule", "Eltibule", "Kur Mountains", "Rahu", "Ilmari",
            "Sun Vale", "Gazluk", "Casino", "Red Wing"
        };
        string[] inventoryKeywords = {
            "inventory", "gold", "council", "currency", "slot", "stack",
            "equipped", "worn", "backpack", "storage", "vendor trash",
            "amazing", "exceptional", "nice", "common", "uncommon", "rare",
            "legendary", "epic"
        };

        var questHits = new HashSet<string>();
        var skillHits = new HashSet<string>();
        var npcHits = new HashSet<string>();
        var inventoryHits = new HashSet<string>();
        var jsonHits = new HashSet<string>();

        Console.WriteLine("Deep scanning PID {0} for quest/skill/NPC data (up to 2GB)...", pid);

        while (totalRead < maxBytes)
        {
            IntPtr ret = VirtualQueryEx(h, (IntPtr)addr, out info, (IntPtr)infoSize);
            if (ret == IntPtr.Zero) break;

            bool isCommitted = info.State == 0x1000;
            bool isReadable = (info.Protect & 0x66) != 0;

            if (isCommitted && isReadable && info.RegionSize > 4096)
            {
                long regionSize = (long)info.RegionSize;
                long offset = 0;
                while (offset < regionSize && totalRead < maxBytes)
                {
                    int chunkSize = (int)Math.Min(regionSize - offset, 8 * 1024 * 1024);
                    byte[] buf = new byte[chunkSize];
                    IntPtr bytesRead;

                    IntPtr readAddr = (IntPtr)((long)info.BaseAddress + offset);
                    if (ReadProcessMemory(h, readAddr, buf, (IntPtr)chunkSize, out bytesRead))
                    {
                        int read = (int)bytesRead;
                        totalRead += read;

                        // Extract both ASCII and UTF-16 strings
                        var asciiStrings = ExtractAscii(buf, read, 8);
                        var utf16Strings = ExtractUtf16(buf, read, 6);

                        foreach (var s in asciiStrings.Concat(utf16Strings))
                        {
                            // Skip engine noise
                            if (s.StartsWith("UnityEngine.") || s.Contains("_Injected") ||
                                s.StartsWith("System.") || s.StartsWith("Mono.") ||
                                s.Contains("Marshalling") || s.StartsWith("Microsoft.") ||
                                s.Contains("uintBitsToFloat") || s.Contains("floatBitsToUint") ||
                                s.Contains("xlat") || s.Contains("__Static") ||
                                s.Contains("FixedBuffer") || s.Contains("DllImport"))
                                continue;

                            // JSON/structured data with game keywords
                            if ((s.Contains("{") || s.Contains("[")) &&
                                ContainsAny(s, new[]{"quest","skill","item","level","xp","reward",
                                    "ability","recipe","favor","gold","inventory","damage","health",
                                    "power","buff","slot","npc","complete","objective"}))
                            {
                                AddCapped(jsonHits, s, 200);
                            }

                            if (ContainsAny(s, questKeywords))
                                AddCapped(questHits, s, 300);
                            if (ContainsAny(s, skillKeywords))
                                AddCapped(skillHits, s, 300);
                            if (ContainsAny(s, npcKeywords))
                                AddCapped(npcHits, s, 200);
                            if (ContainsAny(s, inventoryKeywords))
                                AddCapped(inventoryHits, s, 200);
                        }
                    }
                    offset += chunkSize;
                }
                scanned++;
            }

            ulong next = info.BaseAddress + info.RegionSize;
            if (next <= addr) break;
            addr = next;
        }

        CloseHandle(h);

        Console.WriteLine("Scanned {0} regions, read {1:N0} MB", scanned, totalRead / 1048576.0);
        Console.WriteLine();

        PrintSection("JSON / STRUCTURED GAME DATA", jsonHits, 80);
        PrintSection("QUESTS / OBJECTIVES / HANGOUTS", questHits, 80);
        PrintSection("SKILLS / ABILITIES / RECIPES", skillHits, 80);
        PrintSection("NPCs / LOCATIONS / TRAINERS", npcHits, 60);
        PrintSection("INVENTORY / CURRENCY / EQUIPMENT", inventoryHits, 60);
    }

    static bool ContainsAny(string s, string[] keywords)
    {
        foreach (var k in keywords)
            if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    static void AddCapped(HashSet<string> set, string s, int max)
    {
        if (set.Count >= max) return;
        string trimmed = s.Length > 400 ? s.Substring(0, 400) : s;
        set.Add(trimmed);
    }

    static List<string> ExtractAscii(byte[] buf, int length, int minLen)
    {
        var results = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            byte b = buf[i];
            if (b >= 32 && b < 127) sb.Append((char)b);
            else
            {
                if (sb.Length >= minLen) results.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length >= minLen) results.Add(sb.ToString());
        return results;
    }

    static List<string> ExtractUtf16(byte[] buf, int length, int minLen)
    {
        var results = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < length - 1; i += 2)
        {
            char c = (char)(buf[i] | (buf[i + 1] << 8));
            if (c >= 32 && c < 127) sb.Append(c);
            else
            {
                if (sb.Length >= minLen) results.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length >= minLen) results.Add(sb.ToString());
        return results;
    }

    static void PrintSection(string name, HashSet<string> items, int max)
    {
        if (items.Count == 0) return;
        Console.WriteLine("========================================");
        Console.WriteLine("=== {0} ({1} found) ===", name, items.Count);
        Console.WriteLine("========================================");
        // Sort: longer strings first (more context), but also prioritize ones with more keywords
        var sorted = items.OrderByDescending(s => s.Length).Take(max);
        foreach (var s in sorted)
        {
            string display = s.Length > 250 ? s.Substring(0, 250) + "..." : s;
            Console.WriteLine("  {0}", display);
        }
        Console.WriteLine();
    }
}
