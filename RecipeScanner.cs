using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class RecipeScanner
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProc, IntPtr baseAddr, byte[] buffer, IntPtr size, out IntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQueryEx(IntPtr hProc, IntPtr addr, out MEMORY_BASIC_INFORMATION64 info, IntPtr length);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress, AllocationBase;
        public uint AllocationProtect, __alignment1;
        public ulong RegionSize;
        public uint State, Protect, Type, __alignment2;
    }

    static void Main(string[] args)
    {
        int pid = int.Parse(args[0]);
        long maxBytes = 4L * 1024 * 1024 * 1024;

        IntPtr h = OpenProcess(0x0410, false, pid);
        if (h == IntPtr.Zero) { Console.WriteLine("Failed to open process"); return; }

        var info = new MEMORY_BASIC_INFORMATION64();
        int infoSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64));
        ulong addr = 0;
        long totalRead = 0;

        var recipes = new HashSet<string>();
        var cookingStrings = new HashSet<string>();
        var ingredients = new HashSet<string>();
        var craftingUI = new HashSet<string>();

        Console.WriteLine($"Scanning PID {pid} for recipe data...");

        while (totalRead < maxBytes)
        {
            IntPtr ret = VirtualQueryEx(h, (IntPtr)addr, out info, (IntPtr)infoSize);
            if (ret == IntPtr.Zero) break;

            if (info.State == 0x1000 && (info.Protect & 0x66) != 0 && info.RegionSize > 4096)
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

                        // Both ASCII and UTF-16
                        var allStrings = ExtractAscii(buf, read, 8);
                        allStrings.AddRange(ExtractUtf16(buf, read, 6));

                        foreach (var s in allStrings)
                        {
                            // Skip engine noise
                            if (s.StartsWith("UnityEngine") || s.StartsWith("System.") ||
                                s.StartsWith("Mono.") || s.Contains("_Injected") ||
                                s.StartsWith("Unity.") || s.StartsWith("Microsoft.") ||
                                s.Contains("Program Files") || s.Contains("AppData") ||
                                s.Contains("uintBitsToFloat") || s.Contains("xlat"))
                                continue;

                            // Recipe-related strings
                            if (Regex.IsMatch(s, @"\b(recipe|Recipe)\b") && s.Length > 10 && s.Length < 400 &&
                                !s.Contains("mastercharactercreator"))
                            {
                                AddCapped(recipes, s, 500);
                            }

                            // Cooking-specific
                            if (Regex.IsMatch(s, @"\b(cook|bake|grill|roast|fry|stew|soup|broth|casserole|pie|cake|bread|sandwich|salad|snack|meal|dish|food|fried|boiled|grilled|roasted|sauteed|poached)\b", RegexOptions.IgnoreCase) &&
                                s.Length > 10 && s.Length < 400 &&
                                !s.Contains("mastercharactercreator") && !s.Contains("cookie") && !s.Contains("Assets/"))
                            {
                                AddCapped(cookingStrings, s, 500);
                            }

                            // Ingredients
                            if (Regex.IsMatch(s, @"\b(meat|cheese|milk|butter|egg|flour|salt|pepper|sugar|onion|garlic|potato|carrot|cabbage|mushroom|apple|guava|lemon|orange|tomato|corn|wheat|rice|barley|oat|fish|crab|lobster|shrimp|snail|venison|mutton|pork|beef|chicken|bacon)\b", RegexOptions.IgnoreCase) &&
                                s.Length > 8 && s.Length < 300 &&
                                !s.Contains("mastercharactercreator") && !s.Contains("Assets/") && !s.Contains("bundle"))
                            {
                                AddCapped(ingredients, s, 500);
                            }

                            // Crafting UI strings
                            if (Regex.IsMatch(s, @"\b(Crafting|Use Current Recipe|Craft Item|ingredient|result|requires|produces|yields)\b", RegexOptions.IgnoreCase) &&
                                s.Length > 12 && s.Length < 300)
                            {
                                AddCapped(craftingUI, s, 200);
                            }
                        }
                    }
                    offset += chunkSize;
                }
            }

            ulong next = info.BaseAddress + info.RegionSize;
            if (next <= addr) break;
            addr = next;
        }

        CloseHandle(h);

        Console.WriteLine($"Scanned {totalRead / 1048576} MB");
        Console.WriteLine();

        PrintSection("RECIPE STRINGS", recipes, 100);
        PrintSection("COOKING-RELATED", cookingStrings, 100);
        PrintSection("INGREDIENTS", ingredients, 80);
        PrintSection("CRAFTING UI", craftingUI, 40);
    }

    static void AddCapped(HashSet<string> set, string s, int max)
    {
        if (set.Count >= max) return;
        set.Add(s.Length > 400 ? s.Substring(0, 400) : s);
    }

    static List<string> ExtractAscii(byte[] buf, int length, int minLen)
    {
        var r = new List<string>(); var sb = new StringBuilder();
        for (int i = 0; i < length; i++) {
            byte b = buf[i];
            if (b >= 32 && b < 127) sb.Append((char)b);
            else { if (sb.Length >= minLen) r.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= minLen) r.Add(sb.ToString());
        return r;
    }

    static List<string> ExtractUtf16(byte[] buf, int length, int minLen)
    {
        var r = new List<string>(); var sb = new StringBuilder();
        for (int i = 0; i < length - 1; i += 2) {
            char c = (char)(buf[i] | (buf[i + 1] << 8));
            if (c >= 32 && c < 127) sb.Append(c);
            else { if (sb.Length >= minLen) r.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= minLen) r.Add(sb.ToString());
        return r;
    }

    static void PrintSection(string name, HashSet<string> items, int max)
    {
        if (items.Count == 0) return;
        Console.WriteLine($"=== {name} ({items.Count} found) ===");
        foreach (var s in items.OrderByDescending(x => x.Length).Take(max))
            Console.WriteLine($"  {(s.Length > 250 ? s.Substring(0, 250) + "..." : s)}");
        Console.WriteLine();
    }
}
