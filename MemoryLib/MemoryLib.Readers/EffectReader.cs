using System.Text.Json;
using MemoryLib;
using MemoryLib.Models;

namespace MemoryLib.Readers;

public class EffectReader
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private ulong _combatantVtable;
    private ulong _combatantAddr;
    private readonly string _cacheDir;

    // Discovered layout offsets for the Effect object
    private int _nameOffset;
    private int _iidOffset;
    private int _durationOffset;
    private int _remainingOffset;
    private bool _layoutDiscovered;

    public EffectReader(ProcessMemory memory, MemoryRegionScanner scanner)
    {
        _memory = memory;
        _scanner = scanner;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectGorgonTools");
    }

    public void SetCombatantAddress(ulong combatantAddr)
    {
        _combatantAddr = combatantAddr;
    }

    public bool AutoDiscover()
    {
        // Strategy 1 - Cache
        try
        {
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
            if (File.Exists(cachePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));

                ulong cachedVtable = 0;
                if (doc.RootElement.TryGetProperty("combatantVtable", out var vtProp))
                {
                    string? hex = vtProp.GetString();
                    if (hex != null && ulong.TryParse(hex.TrimStart('0', 'x').TrimStart('0', 'X'),
                        System.Globalization.NumberStyles.HexNumber, null, out ulong v))
                        cachedVtable = v;
                }

                ulong cachedAddr = 0;
                if (doc.RootElement.TryGetProperty("combatantAddr", out var addrProp))
                {
                    string? hex = addrProp.GetString();
                    if (hex != null && ulong.TryParse(hex.TrimStart('0', 'x').TrimStart('0', 'X'),
                        System.Globalization.NumberStyles.HexNumber, null, out ulong v))
                        cachedAddr = v;
                }

                if (cachedAddr > 0x10000 && cachedVtable > 0x10000 && ValidateCombatant(cachedAddr))
                {
                    _combatantVtable = cachedVtable;
                    _combatantAddr = cachedAddr;
                    TryDiscoverEffectLayout();
                    return true;
                }
            }
        }
        catch { }

        // Strategy 2 - Find Combatant via vtable scan
        var stringHits = _scanner.ScanForUtf16String("Combatant", maxResults: 200);
        foreach (var hit in stringHits)
        {
            ulong strObjAddr = hit.Address - 0x14;
            var ptrMatches = _scanner.ScanForPointerTo(strObjAddr, maxResults: 50);
            foreach (var ptrMatch in ptrMatches)
            {
                for (int backStep = 0; backStep <= 16; backStep++)
                {
                    ulong candidateBase = ptrMatch.Address - (ulong)(backStep * 8);
                    ulong vtable = _memory.ReadPointer(candidateBase);
                    if (vtable <= 0x10000 || vtable > 0x7FFF_FFFF_FFFF_FFFFul) continue;

                    if (!ValidateCombatant(candidateBase)) continue;

                    bool isLocal = _memory.ReadBool(candidateBase + 0x98);
                    if (!isLocal) continue;

                    _combatantVtable = vtable;
                    _combatantAddr = candidateBase;
                    SaveCache();
                    TryDiscoverEffectLayout();
                    return true;
                }
            }
        }

        // Fallback: scan all regions for any combatant with forLocalPlayer==true
        if (_combatantVtable > 0)
        {
            ulong found = FindLocalPlayerCombatant();
            if (found > 0)
            {
                _combatantAddr = found;
                SaveCache();
                TryDiscoverEffectLayout();
                return true;
            }
        }

        return false;
    }

    private bool ValidateCombatant(ulong addr)
    {
        ulong vtable = _memory.ReadPointer(addr);
        if (vtable <= 0x10000 || vtable > 0x7FFF_FFFF_FFFF_FFFFul) return false;

        byte isDead = _memory.ReadByte(addr + 0xA9);
        if (isDead > 1) return false;

        ulong attrDict = _memory.ReadPointer(addr + 0x90);
        if (attrDict <= 0x10000 || attrDict > 0x7FFF_FFFF_FFFF_FFFFul) return false;

        return true;
    }

    private ulong FindLocalPlayerCombatant()
    {
        foreach (var region in _scanner.GetGameRegions())
        {
            ulong regionOffset = 0;
            while (regionOffset < region.Size)
            {
                ulong remaining = region.Size - regionOffset;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                byte[]? chunk = _memory.ReadBytes(region.BaseAddress + regionOffset, readSize);
                if (chunk != null)
                {
                    for (int i = 0; i <= chunk.Length - 8; i += 8)
                    {
                        if (BitConverter.ToUInt64(chunk, i) == _combatantVtable)
                        {
                            ulong objAddr = region.BaseAddress + regionOffset + (ulong)i;
                            if (ValidateCombatant(objAddr) && _memory.ReadBool(objAddr + 0x98))
                                return objAddr;
                        }
                    }
                }
                regionOffset += (ulong)readSize;
            }
        }
        return 0;
    }

    private void TryDiscoverEffectLayout()
    {
        if (_combatantAddr == 0) return;

        ulong effectsListPtr = _memory.ReadPointer(_combatantAddr + 0xB8);
        if (effectsListPtr == 0) return;

        ulong itemsArrayPtr = _memory.ReadPointer(effectsListPtr + 0x10);
        int size = _memory.ReadInt32(effectsListPtr + 0x18);
        if (size <= 0 || size > 200 || itemsArrayPtr == 0) return;

        ulong firstEffectPtr = _memory.ReadPointer(itemsArrayPtr + 0x20);
        if (firstEffectPtr == 0) return;

        // Try to find a string pointer (effect name) at offsets 0x10-0x60
        _nameOffset = 0;
        for (int off = 0x10; off <= 0x60; off += 8)
        {
            ulong strPtr = _memory.ReadPointer(firstEffectPtr + (ulong)off);
            if (strPtr <= 0x10000 || strPtr > 0x7FFF_FFFF_FFFF_FFFFul) continue;
            string? s = _memory.ReadMonoString(strPtr, maxLength: 128);
            if (s != null && s.Length >= 3 && s.All(c => c < 128))
            {
                _nameOffset = off;
                break;
            }
        }

        // Try to find IID (positive int < 1_000_000) at early offsets
        _iidOffset = 0;
        for (int off = 0x10; off <= 0x30; off += 4)
        {
            int val = _memory.ReadInt32(firstEffectPtr + (ulong)off);
            if (val > 0 && val < 1_000_000)
            {
                _iidOffset = off;
                break;
            }
        }

        // Try to find duration float (0-7200, non-zero) after the name/iid
        _durationOffset = 0;
        for (int off = 0x1C; off <= 0x50; off += 4)
        {
            float val = _memory.ReadFloat(firstEffectPtr + (ulong)off);
            if (val > 0f && val <= 7200f && !float.IsNaN(val))
            {
                _durationOffset = off;
                break;
            }
        }

        // Remaining is likely the next float slot after duration
        _remainingOffset = _durationOffset > 0 ? _durationOffset + 4 : 0;

        _layoutDiscovered = _nameOffset > 0 || _iidOffset > 0 || _durationOffset > 0;
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");

            var dict = new Dictionary<string, string>();
            if (File.Exists(cachePath))
            {
                try
                {
                    using var existing = JsonDocument.Parse(File.ReadAllText(cachePath));
                    foreach (var prop in existing.RootElement.EnumerateObject())
                        dict[prop.Name] = prop.Value.GetString() ?? "";
                }
                catch { }
            }

            dict["combatantVtable"] = $"0x{_combatantVtable:X}";
            dict["combatantAddr"] = $"0x{_combatantAddr:X}";
            File.WriteAllText(cachePath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public List<EffectSnapshot>? ReadEffects(ulong? combatantAddr = null)
    {
        ulong addr = combatantAddr ?? _combatantAddr;
        if (addr == 0) return null;

        ulong effectsListPtr = _memory.ReadPointer(addr + 0xB8);
        if (effectsListPtr == 0) return [];

        ulong itemsArrayPtr = _memory.ReadPointer(effectsListPtr + 0x10);
        int size = _memory.ReadInt32(effectsListPtr + 0x18);
        if (size < 0 || size > 200 || itemsArrayPtr == 0) return [];

        var results = new List<EffectSnapshot>();

        for (int i = 0; i < size; i++)
        {
            ulong effectObjPtr = _memory.ReadPointer(itemsArrayPtr + 0x20 + (ulong)(i * 8));
            if (effectObjPtr == 0) continue;

            string name = TryReadEffectName(effectObjPtr);
            int iid = TryReadEffectIID(effectObjPtr);
            float duration = TryReadEffectDuration(effectObjPtr);
            float remaining = TryReadEffectRemaining(effectObjPtr);

            results.Add(new EffectSnapshot
            {
                ObjectAddress = effectObjPtr,
                EffectIID = iid,
                Name = name,
                Duration = duration,
                RemainingTime = remaining,
                StackCount = 1,
                IsDebuff = false,
            });
        }

        return results;
    }

    private string TryReadEffectName(ulong objAddr)
    {
        // Use discovered offset first
        if (_layoutDiscovered && _nameOffset > 0)
        {
            ulong strPtr = _memory.ReadPointer(objAddr + (ulong)_nameOffset);
            if (strPtr > 0x10000 && strPtr < 0x7FFF_FFFF_FFFF_FFFFul)
            {
                string? s = _memory.ReadMonoString(strPtr, maxLength: 128);
                if (s != null && s.Length >= 3 && s.All(c => c < 128))
                    return s;
            }
        }

        int[] offsets = [0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40];
        foreach (int offset in offsets)
        {
            ulong strPtr = _memory.ReadPointer(objAddr + (ulong)offset);
            if (strPtr <= 0x10000 || strPtr > 0x7FFF_FFFF_FFFF_FFFFul) continue;
            string? s = _memory.ReadMonoString(strPtr, maxLength: 128);
            if (s != null && s.Length >= 3 && s.All(c => c < 128))
                return s;
        }

        return "";
    }

    private int TryReadEffectIID(ulong objAddr)
    {
        if (_layoutDiscovered && _iidOffset > 0)
        {
            int val = _memory.ReadInt32(objAddr + (ulong)_iidOffset);
            if (val > 0 && val < 1_000_000) return val;
        }

        int[] offsets = [0x10, 0x14, 0x18, 0x1C];
        foreach (int offset in offsets)
        {
            int val = _memory.ReadInt32(objAddr + (ulong)offset);
            if (val > 0 && val < 1_000_000) return val;
        }

        return 0;
    }

    private float TryReadEffectDuration(ulong objAddr)
    {
        if (_layoutDiscovered && _durationOffset > 0)
        {
            float val = _memory.ReadFloat(objAddr + (ulong)_durationOffset);
            if (val >= 0f && val <= 7200f && !float.IsNaN(val) && val != 0f)
                return val;
        }

        int[] offsets = [0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30];
        foreach (int offset in offsets)
        {
            float val = _memory.ReadFloat(objAddr + (ulong)offset);
            if (val > 0f && val <= 7200f && !float.IsNaN(val))
                return val;
        }

        return 0f;
    }

    private float TryReadEffectRemaining(ulong objAddr)
    {
        if (_layoutDiscovered && _remainingOffset > 0)
        {
            float val = _memory.ReadFloat(objAddr + (ulong)_remainingOffset);
            if (val >= 0f && val <= 7200f && !float.IsNaN(val))
                return val;
        }

        int[] offsets = [0x20, 0x24, 0x28, 0x2C, 0x30, 0x34];
        foreach (int offset in offsets)
        {
            float val = _memory.ReadFloat(objAddr + (ulong)offset);
            if (val >= 0f && val <= 7200f && !float.IsNaN(val))
                return val;
        }

        return 0f;
    }

    public void DumpEffectObject(ulong addr)
    {
        Console.WriteLine($"Effect object at 0x{addr:X}:");
        Console.WriteLine($"  {"Offset",-8} {"Int32",-12} {"Float",-12} {"Pointer / String"}");
        Console.WriteLine("  " + new string('-', 60));

        for (int off = 0; off <= 0x60; off += 8)
        {
            ulong fieldAddr = addr + (ulong)off;
            int i32 = _memory.ReadInt32(fieldAddr);
            float f32 = _memory.ReadFloat(fieldAddr);
            ulong ptr = _memory.ReadPointer(fieldAddr);
            string extra = "";
            if (ptr > 0x10000 && ptr < 0x7FFF_FFFF_FFFF_FFFFul)
            {
                string? s = _memory.ReadMonoString(ptr, maxLength: 64);
                extra = s != null ? $"-> \"{s}\"" : $"-> 0x{ptr:X}";
            }
            Console.WriteLine($"  +0x{off:X2,-6} {i32,-12} {f32,-12:G6} {extra}");
        }
    }
}
