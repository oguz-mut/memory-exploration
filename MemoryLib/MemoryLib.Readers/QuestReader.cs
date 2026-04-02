namespace MemoryLib.Readers;

using MemoryLib;
using MemoryLib.Models;
using System.Text.Json;

public sealed class QuestReader
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private ulong _questControllerClassPtr;
    private readonly string _cacheDir;
    private HashSet<int> _trackedQuestIds = new();

    public QuestReader(ProcessMemory memory, MemoryRegionScanner scanner)
    {
        _memory = memory;
        _scanner = scanner;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectGorgonTools");
    }

    public bool AutoDiscover()
    {
        // Strategy 1 - Cache
        try
        {
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
            if (File.Exists(cachePath))
            {
                string json = File.ReadAllText(cachePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("questControllerClassPtr", out var prop))
                {
                    string? hex = prop.GetString();
                    if (hex != null && ulong.TryParse(hex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong cached))
                    {
                        if (cached > 0x10000 && ValidateQuestControllerPtr(cached))
                        {
                            _questControllerClassPtr = cached;
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy 2 - Find via known quest name strings
        string[] probeQuestNames = { "Tutorial", "Words of Power", "Ivyn's Curious Dagger", "A Favor for Joeh", "The Right Equipment" };
        foreach (string probe in probeQuestNames)
        {
            var hits = _scanner.ScanForUtf16String(probe, maxResults: 20);
            foreach (var hit in hits)
            {
                // String object base is hit.Address - 0x14 (skip vtable+sync+length prefix)
                ulong strObjAddr = hit.Address - 0x14;

                // Quest.Name is at +0x20, so a Quest object could be at strObjAddr - 0x20
                ulong questPtrCandidate = strObjAddr - 0x20;

                // Check if this looks like a valid Quest
                int questId = _memory.ReadInt32(questPtrCandidate + 0x10);
                if (questId <= 0 || questId > 100_000) continue;

                ulong internalNamePtr = _memory.ReadPointer(questPtrCandidate + 0x18);
                if (internalNamePtr == 0) continue;
                string? internalName = _memory.ReadMonoString(internalNamePtr);
                if (internalName == null || internalName.Length == 0) continue;

                // Scan for pointers to this Quest object (QuestState.Quest at +0x10)
                var questStatePtrs = _scanner.ScanForPointerTo(questPtrCandidate, maxResults: 10);
                foreach (var qsPtrMatch in questStatePtrs)
                {
                    // QuestState.Quest is at +0x10, so QuestState base = match - 0x10
                    ulong questStateBase = qsPtrMatch.Address - 0x10;

                    // Scan for pointer to this QuestState in list items array
                    var listItemPtrs = _scanner.ScanForPointerTo(questStateBase, maxResults: 10);
                    foreach (var liMatch in listItemPtrs)
                    {
                        // Items array element: liMatch.Address could be itemsArray + 0x20 + i*8
                        // The List._items array pointer is in a List object at +0x10
                        // Try to find a List object whose _items pointer covers this address
                        ulong itemsArrayPtr = liMatch.Address & ~0x7UL; // align to 8
                        // array header: klass(8)+monitor(8)+bounds(8)+max_length(8) = 0x20 bytes before data
                        ulong arrayBase = itemsArrayPtr - ((itemsArrayPtr - 0x20) % 8);
                        // Walk back to find array base: items start at +0x20
                        // liMatch.Address is within data section, offset from array base must be >= 0x20
                        // Try reading array max_length to find a valid array
                        for (int backBytes = 0; backBytes <= 128; backBytes += 8)
                        {
                            ulong candidateArrayBase = liMatch.Address - (ulong)backBytes;
                            if (candidateArrayBase < 0x20) continue;

                            int maxLen = _memory.ReadInt32(candidateArrayBase + 0x18);
                            if (maxLen < 1 || maxLen > 200) continue;

                            // This array should be pointed to by a List._items at +0x10
                            var listCandidates = _scanner.ScanForPointerTo(candidateArrayBase, maxResults: 10);
                            foreach (var lcMatch in listCandidates)
                            {
                                // List._items is at +0x10; List base = lcMatch.Address - 0x10
                                ulong listBase = lcMatch.Address - 0x10;

                                int listSize = _memory.ReadInt32(listBase + 0x18);
                                if (listSize < 1 || listSize > 50) continue;

                                // UIQuestController static fields: _quests List at +0x00
                                // The static fields block has _quests (List ptr) at offset 0
                                // Scan for pointer to this list
                                var ctrlCandidates = _scanner.ScanForPointerTo(listBase, maxResults: 10);
                                foreach (var ccMatch in ctrlCandidates)
                                {
                                    ulong candidateCtrlPtr = ccMatch.Address; // this is classPtr + 0x00
                                    if (ValidateQuestControllerPtr(candidateCtrlPtr))
                                    {
                                        _questControllerClassPtr = candidateCtrlPtr;
                                        SaveClassPtrCache();
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Strategy 3 - Brute-force list scan
        return BruteForceDiscover();
    }

    private bool BruteForceDiscover()
    {
        // Scan for List-like objects where _size is 1-50 and items lead to valid QuestState/Quest chains
        foreach (var region in _scanner.GetGameRegions())
        {
            ulong chunkBase = 0;
            while (chunkBase < region.Size)
            {
                ulong remaining = region.Size - chunkBase;
                int readSize = (int)Math.Min(remaining, (ulong)(4 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + chunkBase;

                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    // Walk in 8-byte strides looking for a plausible _size (1-50) at offset +0x18
                    for (int i = 0; i <= chunk.Length - 0x20; i += 8)
                    {
                        int candidateSize = BitConverter.ToInt32(chunk, i + 0x18);
                        if (candidateSize < 1 || candidateSize > 50) continue;

                        ulong listBase = readAddr + (ulong)i;
                        ulong itemsArrayPtr = _memory.ReadPointer(listBase + 0x10);
                        if (itemsArrayPtr == 0 || itemsArrayPtr < 0x10000) continue;

                        int arrMaxLen = _memory.ReadInt32(itemsArrayPtr + 0x18);
                        if (arrMaxLen < candidateSize || arrMaxLen > 200) continue;

                        // Validate first QuestState pointer
                        ulong firstStatePtr = _memory.ReadPointer(itemsArrayPtr + 0x20);
                        if (firstStatePtr == 0 || firstStatePtr < 0x10000) continue;

                        ulong questPtr = _memory.ReadPointer(firstStatePtr + 0x10);
                        if (questPtr == 0 || questPtr < 0x10000) continue;

                        int questId = _memory.ReadInt32(questPtr + 0x10);
                        if (questId <= 0 || questId > 100_000) continue;

                        ulong namePtr = _memory.ReadPointer(questPtr + 0x20);
                        if (namePtr == 0) continue;
                        string? name = _memory.ReadMonoString(namePtr);
                        if (name == null || name.Length < 2 || name.Length > 100) continue;

                        // Found a plausible list; now find the controller pointer to this list
                        var ctrlCandidates = _scanner.ScanForPointerTo(listBase, maxResults: 10);
                        foreach (var ccMatch in ctrlCandidates)
                        {
                            ulong candidateCtrlPtr = ccMatch.Address;
                            if (ValidateQuestControllerPtr(candidateCtrlPtr))
                            {
                                _questControllerClassPtr = candidateCtrlPtr;
                                SaveClassPtrCache();
                                return true;
                            }
                        }
                    }
                }

                chunkBase += (ulong)readSize;
            }
        }

        return false;
    }

    private bool ValidateQuestControllerPtr(ulong ptr)
    {
        if (ptr == 0 || ptr < 0x10000) return false;

        ulong questListPtr = _memory.ReadPointer(ptr + 0x00);
        if (questListPtr == 0 || questListPtr < 0x10000) return false;

        int listSize = _memory.ReadInt32(questListPtr + 0x18);
        if (listSize < 0 || listSize > 100) return false;

        ulong itemsArrayPtr = _memory.ReadPointer(questListPtr + 0x10);
        if (itemsArrayPtr == 0 || itemsArrayPtr < 0x10000) return false;

        if (listSize == 0) return true; // empty list is valid

        ulong firstStatePtr = _memory.ReadPointer(itemsArrayPtr + 0x20);
        if (firstStatePtr == 0 || firstStatePtr < 0x10000) return false;

        ulong questPtr = _memory.ReadPointer(firstStatePtr + 0x10);
        if (questPtr == 0 || questPtr < 0x10000) return false;

        int questId = _memory.ReadInt32(questPtr + 0x10);
        if (questId <= 0 || questId > 100_000) return false;

        ulong namePtr = _memory.ReadPointer(questPtr + 0x20);
        if (namePtr == 0) return false;
        string? name = _memory.ReadMonoString(namePtr);
        return name != null && name.Length >= 1 && name.Length <= 100;
    }

    private void SaveClassPtrCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");

            Dictionary<string, string> data = new();
            if (File.Exists(cachePath))
            {
                try
                {
                    string existing = File.ReadAllText(cachePath);
                    using var doc = JsonDocument.Parse(existing);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        data[prop.Name] = prop.Value.GetString() ?? "";
                }
                catch { }
            }

            data["questControllerClassPtr"] = $"0x{_questControllerClassPtr:X}";
            File.WriteAllText(cachePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public List<QuestSnapshot>? ReadActiveQuests()
    {
        if (_questControllerClassPtr == 0) return null;

        _trackedQuestIds.Clear();
        ReadTrackedQuestIds();

        ulong questListPtr = _memory.ReadPointer(_questControllerClassPtr + 0x00);
        if (questListPtr == 0) return null;

        ulong itemsArrayPtr = _memory.ReadPointer(questListPtr + 0x10);
        int size = _memory.ReadInt32(questListPtr + 0x18);
        if (size < 0 || size > 100) return new List<QuestSnapshot>();

        var results = new List<QuestSnapshot>();
        for (int i = 0; i < size; i++)
        {
            ulong questStatePtr = _memory.ReadPointer(itemsArrayPtr + 0x20 + (ulong)(i * 8));
            if (questStatePtr == 0) continue;

            ulong questPtr = _memory.ReadPointer(questStatePtr + 0x10);
            if (questPtr == 0) continue;

            int questId = _memory.ReadInt32(questPtr + 0x10);
            string? internalName = _memory.ReadMonoString(_memory.ReadPointer(questPtr + 0x18));
            string? name         = _memory.ReadMonoString(_memory.ReadPointer(questPtr + 0x20));
            string? desc         = _memory.ReadMonoString(_memory.ReadPointer(questPtr + 0x28));
            string? location     = _memory.ReadMonoString(_memory.ReadPointer(questPtr + 0x30));
            bool isReadyForTurnIn = _memory.ReadBool(questStatePtr + 0x20);
            bool isWorkOrder      = _memory.ReadBool(questPtr + 0x76);
            bool isTracked        = _trackedQuestIds.Contains(questId);

            var objectives = ReadObjectives(questPtr, questStatePtr);

            results.Add(new QuestSnapshot
            {
                ObjectAddress    = questStatePtr,
                QuestId          = questId,
                InternalName     = internalName ?? "",
                Name             = name ?? $"Quest_{questId}",
                Description      = desc ?? "",
                DisplayedLocation = location ?? "",
                IsReadyForTurnIn = isReadyForTurnIn,
                IsTracked        = isTracked,
                IsWorkOrder      = isWorkOrder,
                Objectives       = objectives,
            });
        }

        return results.OrderBy(q => q.Name).ToList();
    }

    private void ReadTrackedQuestIds()
    {
        ulong listPtr = _memory.ReadPointer(_questControllerClassPtr + 0x60);
        if (listPtr == 0) return;

        ulong itemsArrayPtr = _memory.ReadPointer(listPtr + 0x10);
        int size = _memory.ReadInt32(listPtr + 0x18);
        if (size < 0 || size > 200 || itemsArrayPtr == 0) return;

        for (int i = 0; i < size; i++)
        {
            int questId = _memory.ReadInt32(itemsArrayPtr + 0x20 + (ulong)(i * 4));
            _trackedQuestIds.Add(questId);
        }
    }

    private List<ObjectiveSnapshot> ReadObjectives(ulong questPtr, ulong questStatePtr)
    {
        ulong objectivesArrayPtr = _memory.ReadPointer(questPtr + 0x58);
        ulong statesArrayPtr     = _memory.ReadPointer(questStatePtr + 0x18);
        if (objectivesArrayPtr == 0) return new List<ObjectiveSnapshot>();

        int objCount = _memory.ReadInt32(objectivesArrayPtr + 0x18); // array max_length
        if (objCount < 0 || objCount > 20) return new List<ObjectiveSnapshot>();

        int statesCount = statesArrayPtr != 0 ? _memory.ReadInt32(statesArrayPtr + 0x18) : 0;

        var results = new List<ObjectiveSnapshot>();
        const int objStructSize = 0x38;

        for (int i = 0; i < objCount; i++)
        {
            ulong objBase = objectivesArrayPtr + 0x20 + (ulong)(i * objStructSize);

            string? type        = _memory.ReadMonoString(_memory.ReadPointer(objBase + 0x00));
            string? description = _memory.ReadMonoString(_memory.ReadPointer(objBase + 0x08));
            int targetCount     = _memory.ReadInt32(objBase + 0x1C);
            int currentState    = (i < statesCount && statesArrayPtr != 0)
                ? _memory.ReadInt32(statesArrayPtr + 0x20 + (ulong)(i * 4))
                : 0;

            results.Add(new ObjectiveSnapshot
            {
                Type         = type ?? "",
                Description  = description ?? "",
                TargetCount  = targetCount,
                CurrentState = currentState,
                IsComplete   = targetCount > 0 && currentState >= targetCount,
            });
        }

        return results;
    }
}
