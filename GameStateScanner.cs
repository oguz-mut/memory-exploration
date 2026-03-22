using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class GameStateScanner
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
        int maxMB = args.Length > 1 ? int.Parse(args[1]) : 512; // scan up to N MB total

        IntPtr h = OpenProcess(0x0410, false, pid);
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
        long maxBytes = (long)maxMB * 1024 * 1024;

        // Collect game-state strings with context
        var gameState = new List<string>();       // JSON-like structures
        var chatMessages = new List<string>();    // chat/dialog text
        var playerData = new List<string>();      // player-related
        var itemData = new List<string>();        // items/inventory
        var skillData = new List<string>();       // skills/abilities
        var questData = new List<string>();       // quests
        var npcData = new List<string>();         // NPCs
        var numericData = new List<string>();     // strings with numbers (stats)
        var networkData = new List<string>();     // protocol messages
        var allInteresting = new List<string>(); // everything interesting

        // Also extract UTF-16 strings (C# / Unity uses UTF-16 internally)
        Console.WriteLine("Scanning PID {0}, up to {1} MB of readable memory...", pid, maxMB);
        Console.WriteLine();

        while (totalRead < maxBytes)
        {
            IntPtr ret = VirtualQueryEx(h, (IntPtr)addr, out info, (IntPtr)infoSize);
            if (ret == IntPtr.Zero) break;

            // Only scan committed, private, read-write regions (where game state lives)
            bool isCommitted = info.State == 0x1000;
            bool isPrivate = info.Type == 0x20000;
            bool isReadable = (info.Protect & 0x66) != 0;

            if (isCommitted && isPrivate && isReadable && info.RegionSize > 4096)
            {
                long regionSize = (long)info.RegionSize;
                // For large regions, read in chunks
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

                        // Extract ASCII strings
                        ExtractAndClassify(buf, read, false,
                            gameState, chatMessages, playerData, itemData,
                            skillData, questData, npcData, numericData,
                            networkData, allInteresting);

                        // Extract UTF-16 LE strings (Unity/C# internal string format)
                        ExtractAndClassify(buf, read, true,
                            gameState, chatMessages, playerData, itemData,
                            skillData, questData, npcData, numericData,
                            networkData, allInteresting);
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

        Console.WriteLine("Scanned {0} regions, read {1:N0} MB total", scanned, totalRead / 1048576.0);
        Console.WriteLine();

        PrintSection("JSON / STRUCTURED DATA", gameState, 60);
        PrintSection("CHAT / DIALOG TEXT", chatMessages, 40);
        PrintSection("PLAYER DATA", playerData, 50);
        PrintSection("ITEMS / INVENTORY / EQUIPMENT", itemData, 50);
        PrintSection("SKILLS / ABILITIES", skillData, 50);
        PrintSection("QUESTS", questData, 40);
        PrintSection("NPCs / CREATURES", npcData, 40);
        PrintSection("NETWORK PROTOCOL", networkData, 30);
        PrintSection("NUMERIC / STATS", numericData, 40);
        PrintSection("OTHER INTERESTING", allInteresting, 40);
    }

    static void ExtractAndClassify(byte[] buf, int length, bool utf16,
        List<string> gameState, List<string> chatMessages, List<string> playerData,
        List<string> itemData, List<string> skillData, List<string> questData,
        List<string> npcData, List<string> numericData, List<string> networkData,
        List<string> allInteresting)
    {
        var strings = new List<string>();
        int minLen = utf16 ? 8 : 12;

        if (utf16)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < length - 1; i += 2)
            {
                char c = (char)(buf[i] | (buf[i + 1] << 8));
                if (c >= 32 && c < 127)
                {
                    sb.Append(c);
                }
                else
                {
                    if (sb.Length >= minLen) strings.Add(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length >= minLen) strings.Add(sb.ToString());
        }
        else
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
                    if (sb.Length >= minLen) strings.Add(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length >= minLen) strings.Add(sb.ToString());
        }

        // Filter out Unity engine noise
        foreach (var s in strings)
        {
            if (s.StartsWith("UnityEngine.") || s.StartsWith("System.") ||
                s.StartsWith("Mono.") || s.Contains("_Injected") ||
                s.StartsWith("Microsoft.") || s.Contains("Marshalling"))
                continue;

            // JSON-like data
            if ((s.Contains("{") && s.Contains(":")) || (s.Contains("[") && s.Contains(",")))
            {
                if (!s.Contains("DllImport") && !s.Contains("StructLayout"))
                    AddUnique(gameState, s, 500);
            }
            // Chat messages (often have timestamps, colons, player names)
            else if (Regex.IsMatch(s, @"\b(say|tell|chat|guild|group|whisper|yell)\b", RegexOptions.IgnoreCase) ||
                     Regex.IsMatch(s, @"^\[?\d{1,2}:\d{2}", RegexOptions.None) ||
                     Regex.IsMatch(s, @"<[^>]+>\s*.+"))
            {
                AddUnique(chatMessages, s, 300);
            }
            // Player data
            else if (Regex.IsMatch(s, @"\b(player|character|avatar|name|gold|coin|favor|councils?)\b", RegexOptions.IgnoreCase))
            {
                if (!s.Contains("PlayerSettings") && !s.Contains("PlayerPrefs"))
                    AddUnique(playerData, s, 300);
            }
            // Items
            else if (Regex.IsMatch(s, @"\b(item|inventory|equip|loot|drop|stack|slot|potion|sword|shield|helm|boot|glove|ring|neck|chest|leg|belt)\b", RegexOptions.IgnoreCase))
            {
                AddUnique(itemData, s, 300);
            }
            // Skills
            else if (Regex.IsMatch(s, @"\b(skill|ability|spell|recipe|craft|power|talent|training|xp|experience)\b", RegexOptions.IgnoreCase))
            {
                if (!s.Contains("SkinnedMesh"))
                    AddUnique(skillData, s, 300);
            }
            // Quests
            else if (Regex.IsMatch(s, @"\b(quest|mission|objective|task|bounty|reward|complete|deliver)\b", RegexOptions.IgnoreCase))
            {
                AddUnique(questData, s, 300);
            }
            // NPCs
            else if (Regex.IsMatch(s, @"\b(npc|vendor|merchant|trainer|guard|goblin|skeleton|spider|wolf|dragon|cow|pig|deer|rat|bat|fairy|fae|elf|orc)\b", RegexOptions.IgnoreCase))
            {
                AddUnique(npcData, s, 300);
            }
            // Network protocol
            else if (Regex.IsMatch(s, @"\b(packet|proto|request|response|GET |POST |HTTP|websocket)\b", RegexOptions.IgnoreCase) ||
                     s.StartsWith("<iq ") || s.StartsWith("SENT:") || s.StartsWith("RECV:"))
            {
                AddUnique(networkData, s, 300);
            }
            // Stats-like (contains meaningful numbers)
            else if (Regex.IsMatch(s, @"\b\d{2,6}\b") && Regex.IsMatch(s, @"[a-zA-Z]{3,}") &&
                     s.Length > 15 && !s.Contains("Program Files") && !s.Contains("AppData") &&
                     !s.Contains("0x") && !s.Contains("$$"))
            {
                AddUnique(numericData, s, 300);
            }
            // Anything else that looks game-specific
            else if (s.Length > 30 && !s.Contains("Program Files") && !s.Contains("AppData") &&
                     !s.Contains("Windows") && !s.All(c => c == '?' || c == '$' || c == '%' || c == '(' || c == ')' || c == '\'') &&
                     Regex.IsMatch(s, @"[a-zA-Z]{4,}") && !s.Contains("Binding::"))
            {
                AddUnique(allInteresting, s, 300);
            }
        }
    }

    static void AddUnique(List<string> list, string s, int maxEntries)
    {
        if (list.Count >= maxEntries) return;
        // Avoid exact duplicates
        string trimmed = s.Length > 300 ? s.Substring(0, 300) : s;
        if (!list.Contains(trimmed))
            list.Add(trimmed);
    }

    static void PrintSection(string name, List<string> items, int max)
    {
        if (items.Count == 0) return;
        Console.WriteLine("========================================");
        Console.WriteLine("=== {0} ({1} found) ===", name, items.Count);
        Console.WriteLine("========================================");
        var sorted = items.OrderByDescending(s => s.Length).Take(max);
        foreach (var s in sorted)
        {
            string display = s.Length > 200 ? s.Substring(0, 200) + "..." : s;
            Console.WriteLine("  {0}", display);
        }
        Console.WriteLine();
    }
}
