using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class LootScanner
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
        long maxBytes = 3L * 1024 * 1024 * 1024; // 3GB

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

        var items = new HashSet<string>();
        var enemies = new HashSet<string>();
        var lootTables = new HashSet<string>();
        var abilities = new HashSet<string>();
        var recipes = new HashSet<string>();
        var areas = new HashSet<string>();
        var gameData = new HashSet<string>();
        var dialogText = new HashSet<string>();
        var playerStats = new HashSet<string>();
        var uiText = new HashSet<string>();

        Console.WriteLine("Scanning PID {0} for game content data (up to 3GB)...", pid);

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

                        // Extract both ASCII and UTF-16
                        var allStrings = ExtractAscii(buf, read, 10);
                        allStrings.AddRange(ExtractUtf16(buf, read, 8));

                        foreach (var s in allStrings)
                        {
                            // Skip engine noise
                            if (s.StartsWith("UnityEngine") || s.Contains("_Injected") ||
                                s.StartsWith("System.") || s.StartsWith("Mono.") ||
                                s.StartsWith("Microsoft.") || s.Contains("Marshalling") ||
                                s.Contains("uintBitsToFloat") || s.Contains("xlat") ||
                                s.Contains("__Static") || s.Contains("FixedBuffer") ||
                                s.Contains("DllImport") || s.Contains("LowLevel.Unsafe") ||
                                s.Contains("LowLevel.Binding") || s.Contains("Profiling.") ||
                                s.Contains("Collections.LowLevel") || s.Contains("Jobs.LowLevel") ||
                                s.Contains("Burst.LowLevel") || s.Contains("IO.LowLevel") ||
                                s.StartsWith("Unity.") || s.Contains("MarshallingTests") ||
                                s.Contains("CompilerError") || s.Contains("InternalCall") ||
                                s.Contains("Program Files") || s.Contains("AppData") ||
                                s.Contains("outVertices") || s.Contains("inVertices") ||
                                s.Contains("inSkin_buf") || s.Contains("inMatrices") ||
                                s.All(c => !char.IsLetter(c)))
                                continue;

                            // Items - look for item name patterns
                            if (Regex.IsMatch(s, @"\b(Sword|Dagger|Staff|Shield|Helm|Boots?|Gloves?|Belt|Ring|Necklace|Amulet|Chest|Legs?|Pants|Robe|Cloak|Cape|Potion|Elixir|Scroll|Gem|Ore|Ingot|Leather|Cloth|Cotton|Silk|Bone|Fang|Claw|Pelt|Meat|Cheese|Milk|Mushroom|Flower|Herb|Seed|Fruit|Vegetable|Apple|Carrot|Potato|Cabbage|Guava|Aster|Marigold)\b", RegexOptions.IgnoreCase) &&
                                !s.Contains("mastercharactercreator") && s.Length > 12 && s.Length < 300)
                            {
                                AddCapped(items, s, 500);
                            }

                            // Enemy/creature names
                            if (Regex.IsMatch(s, @"\b(Rat|Spider|Wolf|Panther|Tiger|Bear|Boar|Cow|Pig|Deer|Bat|Snake|Goblin|Skeleton|Zombie|Ghost|Mantis|Snail|Myconian|Fairy|Fae|Dragon|Worm|Slug|Beetle|Wasp|Scorpion|Golem|Elemental|Demon|Ogre|Troll|Giant|Bandit|Rogue|Mage|Warrior|Archer|Boss|Elite|Feral|Cunning|Ferocious|Psychic|Fire|Acid|Brain Bug|Gnasher)\b", RegexOptions.IgnoreCase) &&
                                !s.Contains("mastercharactercreator") && !s.Contains("eq-") && !s.Contains("bundle") &&
                                s.Length > 12 && s.Length < 300)
                            {
                                AddCapped(enemies, s, 400);
                            }

                            // Loot/drop related
                            if (Regex.IsMatch(s, @"\b(drop|loot|treasure|reward|obtain|pickup|collect|gather|harvest|forage|mine|dig|fish|catch|salvage|scavenge)\b", RegexOptions.IgnoreCase) &&
                                s.Length > 15 && s.Length < 300)
                            {
                                AddCapped(lootTables, s, 300);
                            }

                            // Abilities with actual names (not just types)
                            if (Regex.IsMatch(s, @"\b(Slash|Strike|Bash|Fireball|Ice|Lightning|Heal|Cure|Buff|Debuff|Stun|Root|Slow|Bleed|Poison|Bite|Kick|Punch|Parry|Block|Dodge|Taunt|Rage|Roar|Howl|Summon|Resurrect|Teleport)\b", RegexOptions.IgnoreCase) &&
                                s.Length > 12 && s.Length < 300)
                            {
                                AddCapped(abilities, s, 300);
                            }

                            // Recipes
                            if (Regex.IsMatch(s, @"\b(recipe|craft|cook|brew|forge|smelt|tan|sew|carve|enchant|transmut|augment|combine)\b", RegexOptions.IgnoreCase) &&
                                !s.Contains("UnityEngine") && s.Length > 15 && s.Length < 300)
                            {
                                AddCapped(recipes, s, 300);
                            }

                            // Areas/zones
                            if (Regex.IsMatch(s, @"\b(Serbule|Eltibule|Kur|Rahu|Ilmari|Sun Vale|Gazluk|Casino|Povus|Statehelm|Hogan|Fae Realm|Myconian|Dungeon|Cave|Crypt|Tomb|Sewer|Labyrinth|Goblin|Keep|Tower|Mine)\b", RegexOptions.IgnoreCase) &&
                                !s.Contains("UnityEngine") && s.Length > 15 && s.Length < 300)
                            {
                                AddCapped(areas, s, 200);
                            }

                            // JSON game data
                            if (s.Contains("{") && s.Contains(":") &&
                                Regex.IsMatch(s, @"(item|drop|loot|skill|level|damage|health|armor|power|ability|recipe|quest|npc|monster|creature|mob|spawn)", RegexOptions.IgnoreCase) &&
                                !s.Contains("DllImport") && !s.Contains("StructLayout"))
                            {
                                AddCapped(gameData, s, 300);
                            }

                            // Player stats / attribute values
                            if (Regex.IsMatch(s, @"(MAX_HEALTH|CUR_HEALTH|MAX_POWER|CUR_POWER|MAX_ARMOR|CUR_ARMOR|REGEN|DAMAGE|CRIT|EVASION|MITIGATION|ACCURACY|VULNERABILITY|RESISTANCE)", RegexOptions.IgnoreCase))
                            {
                                AddCapped(playerStats, s, 100);
                            }

                            // Interesting UI text / game messages
                            if (Regex.IsMatch(s, @"\b(You (gain|lose|earn|receive|learn|discover)|Your .* (increased|decreased|improved)|Favor (Level|gained|advanced)|Level Up|New Ability|Recipe Learned)\b", RegexOptions.IgnoreCase) &&
                                s.Length > 15)
                            {
                                AddCapped(uiText, s, 200);
                            }
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

        PrintSection("ITEMS / EQUIPMENT / MATERIALS", items, 120);
        PrintSection("ENEMIES / CREATURES", enemies, 100);
        PrintSection("LOOT / DROPS / GATHERING", lootTables, 80);
        PrintSection("COMBAT ABILITIES", abilities, 80);
        PrintSection("RECIPES / CRAFTING", recipes, 80);
        PrintSection("AREAS / ZONES / DUNGEONS", areas, 60);
        PrintSection("JSON GAME DATA", gameData, 80);
        PrintSection("PLAYER STATS / ATTRIBUTES", playerStats, 40);
        PrintSection("UI MESSAGES / GAME TEXT", uiText, 60);
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
            else { if (sb.Length >= minLen) results.Add(sb.ToString()); sb.Clear(); }
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
            else { if (sb.Length >= minLen) results.Add(sb.ToString()); sb.Clear(); }
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
        foreach (var s in items.OrderByDescending(x => x.Length).Take(max))
        {
            string display = s.Length > 250 ? s.Substring(0, 250) + "..." : s;
            Console.WriteLine("  {0}", display);
        }
        Console.WriteLine();
    }
}
