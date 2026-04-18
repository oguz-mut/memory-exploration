using System.Text;
using System.Threading.Channels;
using MemoryLib;
using RunePuzzleSolver.Models;

namespace RunePuzzleSolver;

public class PuzzleStateReader
{
    public static readonly string[] Symbols = ["7", "B", "C", "F", "K", "M", "P", "Q", "S", "T", "W", "X"];

    public Channel<PuzzleState> StateChannel { get; } = Channel.CreateBounded<PuzzleState>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

    public int GuessRowCount { get; private set; }
    public PuzzleState? LastState { get; private set; }

    private ProcessMemory? _memory;
    private MemoryRegionScanner? _scanner;
    private ulong _ctrlPtr;
    private DateTime _lastScanTime = DateTime.MinValue;

    private const double RescanIntervalSeconds = 5.0;
    // Need to read up to NumGuessesAllowed+4 bytes from object base: 0x68+4 = 0x6C
    private const int MinSpan = Offsets.RunePuzzleSubController.NumGuessesAllowed + 4;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Wait for WindowsPlayer.exe
            while (!ct.IsCancellationRequested)
            {
                int? pid = ProcessMemory.FindGameProcess();
                if (pid.HasValue)
                {
                    try
                    {
                        _memory = ProcessMemory.Open(pid.Value);
                        _scanner = new MemoryRegionScanner(_memory);
                        Console.WriteLine($"[reader] attached to WindowsPlayer.exe (pid={pid.Value})");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[reader] failed to open process: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("[reader] waiting for WindowsPlayer.exe...");
                }
                await Task.Delay(3000, ct);
            }

            if (_memory == null) return;

            bool wasActive = false;

            while (!ct.IsCancellationRequested)
            {
                bool currentlyActive = false;
                try
                {
                    if (EnsureController())
                    {
                        ulong windowPtr = _memory.ReadPointer(_ctrlPtr + Offsets.RunePuzzleSubController.PuzzleWindow);
                        if (windowPtr >= Offsets.MinValidPtr &&
                            _memory.ReadByte(windowPtr + Offsets.GameObject.ActiveSelf) != 0)
                        {
                            int codeLength = _memory.ReadInt32(_ctrlPtr + Offsets.RunePuzzleSubController.CodeLength);
                            int numGuesses = _memory.ReadInt32(_ctrlPtr + Offsets.RunePuzzleSubController.NumGuessesAllowed);

                            if (codeLength >= 3 && codeLength <= 8 && numGuesses >= 5 && numGuesses <= 20)
                            {
                                var history = ReadGuessHistory(_ctrlPtr);
                                GuessRowCount = history.Count;

                                var state = new PuzzleState(true, codeLength, numGuesses, history, history.Count);
                                await StateChannel.Writer.WriteAsync(state, ct);
                                currentlyActive = true;
                            }
                            else
                            {
                                _ctrlPtr = 0; // stale pointer, force re-scan
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[reader] poll error: {ex.Message}");
                    _ctrlPtr = 0;
                }

                if (!currentlyActive && wasActive)
                {
                    GuessRowCount = 0;
                    var inactive = new PuzzleState(false, 0, 0, [], 0);
                    try { await StateChannel.Writer.WriteAsync(inactive, ct); } catch { }
                }
                wasActive = currentlyActive;

                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[reader] fatal: {ex.Message}");
        }
        finally
        {
            _memory?.Dispose();
        }
    }

    private bool EnsureController()
    {
        if (_memory == null) return false;

        // Quick validation of cached pointer — same strictness as the scan itself,
        // otherwise a pooled object that briefly matched could be kept alive.
        if (_ctrlPtr >= Offsets.MinValidPtr)
        {
            int codeLength = _memory.ReadInt32(_ctrlPtr + Offsets.RunePuzzleSubController.CodeLength);
            int numGuesses = _memory.ReadInt32(_ctrlPtr + Offsets.RunePuzzleSubController.NumGuessesAllowed);
            if (codeLength >= 3 && codeLength <= 8 && numGuesses >= 5 && numGuesses <= 20)
            {
                ulong inputListPtr = _memory.ReadPointer(_ctrlPtr + Offsets.RunePuzzleSubController.InputDisplayButtons);
                if (inputListPtr >= Offsets.MinValidPtr)
                {
                    int inputCount = _memory.ReadInt32(inputListPtr + Offsets.IL2CppList.Size);
                    if (inputCount == codeLength) return true;
                }
            }
            _ctrlPtr = 0;
        }

        // Throttle full re-scans to every RescanIntervalSeconds
        if ((DateTime.UtcNow - _lastScanTime).TotalSeconds < RescanIntervalSeconds)
            return false;

        _lastScanTime = DateTime.UtcNow;
        _ctrlPtr = ScanForController();
        return _ctrlPtr >= Offsets.MinValidPtr;
    }

    private ulong ScanForController()
    {
        if (_memory == null || _scanner == null) return 0;

        Console.WriteLine("[reader] scanning for RunePuzzleSubController...");
        _scanner.InvalidateCache();

        foreach (var region in _scanner.GetGameRegions())
        {
            ulong chunkOffset = 0;
            while (chunkOffset < region.Size)
            {
                ulong remaining = region.Size - chunkOffset;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + chunkOffset;

                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    for (int i = 0; i + MinSpan <= chunk.Length; i += 8)
                    {
                        // Vtable pointer must be a valid heap address
                        ulong vtable = BitConverter.ToUInt64(chunk, i);
                        if (vtable < Offsets.MinValidPtr) continue;

                        // codeLength at +0x64: must be in [3, 8]
                        int codeLength = BitConverter.ToInt32(chunk, i + Offsets.RunePuzzleSubController.CodeLength);
                        if (codeLength < 3 || codeLength > 8) continue;

                        // numGuesses at +0x68: must be in [5, 20]
                        int numGuesses = BitConverter.ToInt32(chunk, i + Offsets.RunePuzzleSubController.NumGuessesAllowed);
                        if (numGuesses < 5 || numGuesses > 20) continue;

                        // Validate vtable: first method pointer must be a valid code address
                        ulong method0 = _memory.ReadPointer(vtable);
                        if (method0 < Offsets.MinValidPtr) continue;

                        // PuzzleWindow at +0x20 must be a valid heap pointer AND activeSelf=true
                        ulong windowPtr = BitConverter.ToUInt64(chunk, i + Offsets.RunePuzzleSubController.PuzzleWindow);
                        if (windowPtr < Offsets.MinValidPtr) continue;
                        byte activeSelf = _memory.ReadByte(windowPtr + Offsets.GameObject.ActiveSelf);
                        if (activeSelf == 0) continue;

                        // DEFINITIVE CHECK: inputDisplayButtons.Count must equal codeLength.
                        // The game populates this list only when a puzzle is actively shown,
                        // and its size exactly matches the code length. Filters out pooled/
                        // prefab objects left over from previous puzzles of different lengths.
                        ulong inputListPtr = BitConverter.ToUInt64(chunk, i + Offsets.RunePuzzleSubController.InputDisplayButtons);
                        if (inputListPtr < Offsets.MinValidPtr) continue;
                        int inputCount = _memory.ReadInt32(inputListPtr + Offsets.IL2CppList.Size);
                        if (inputCount != codeLength) continue;

                        ulong ctrlPtr = readAddr + (ulong)i;
                        Console.WriteLine($"[reader] found at 0x{ctrlPtr:X} (codeLength={codeLength}, numGuesses={numGuesses}, inputs={inputCount})");
                        return ctrlPtr;
                    }
                }

                chunkOffset += (ulong)readSize;
            }
        }

        Console.WriteLine("[reader] RunePuzzleSubController not found");
        return 0;
    }

    private List<(string Guess, int WrongPos, int RightPos)> ReadGuessHistory(ulong ctrlPtr)
    {
        var history = new List<(string Guess, int WrongPos, int RightPos)>();
        if (_memory == null) return history;

        try
        {
            // GuessContainer is a RectTransform (which is a Transform) at ctrl+0x50
            ulong containerPtr = _memory.ReadPointer(ctrlPtr + Offsets.RunePuzzleSubController.GuessContainer);
            if (containerPtr < Offsets.MinValidPtr) return history;

            // Transform.m_Children is a List<Transform> at Transform+0x50
            ulong listPtr = _memory.ReadPointer(containerPtr + 0x50);
            if (listPtr < Offsets.MinValidPtr) return history;

            ulong itemsArrayPtr = _memory.ReadPointer(listPtr + Offsets.IL2CppList.Items);
            if (itemsArrayPtr < Offsets.MinValidPtr) return history;

            int size = _memory.ReadInt32(listPtr + Offsets.IL2CppList.Size);
            if (size <= 0 || size > 100) return history;

            for (int i = 0; i < size; i++)
            {
                try
                {
                    ulong childPtr = _memory.ReadPointer(itemsArrayPtr + Offsets.IL2CppArray.FirstElem + (ulong)(i * 8));
                    if (childPtr < Offsets.MinValidPtr)
                    {
                        Console.WriteLine($"[reader] child[{i}] ptr invalid, stopping");
                        break;
                    }

                    // TextMeshProUGUI at child+0x28
                    ulong tmpPtr = _memory.ReadPointer(childPtr + Offsets.UIRunePuzzleGuessRow.Text);
                    if (tmpPtr < Offsets.MinValidPtr)
                    {
                        Console.WriteLine($"[reader] child[{i}] TMP ptr invalid, stopping");
                        break;
                    }

                    // m_text string at tmp+0x90
                    ulong strPtr = _memory.ReadPointer(tmpPtr + Offsets.TextMeshProUGUI.TextStr);
                    if (strPtr < Offsets.MinValidPtr)
                    {
                        Console.WriteLine($"[reader] child[{i}] string ptr invalid, stopping");
                        break;
                    }

                    string? text = _memory.ReadMonoString(strPtr, maxLength: 64);
                    if (text == null)
                    {
                        Console.WriteLine($"[reader] child[{i}] text read failed, stopping");
                        break;
                    }

                    // Parse "x,y" → WrongPos=x, RightPos=y
                    int wrongPos = 0, rightPos = 0;
                    int commaIdx = text.IndexOf(',');
                    if (commaIdx > 0)
                    {
                        int.TryParse(text.AsSpan(0, commaIdx).Trim(), out wrongPos);
                        int.TryParse(text.AsSpan(commaIdx + 1).Trim(), out rightPos);
                    }

                    string guessStr = ReadRuneGuess(childPtr);
                    history.Add((guessStr, wrongPos, rightPos));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[reader] child[{i}] exception: {ex.Message}, stopping");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[reader] ReadGuessHistory error: {ex.Message}");
        }

        return history;
    }

    private string ReadRuneGuess(ulong childPtr)
    {
        if (_memory == null) return string.Empty;

        try
        {
            // Sprite[] at child+0x20
            ulong runesArrayPtr = _memory.ReadPointer(childPtr + Offsets.UIRunePuzzleGuessRow.Runes);
            if (runesArrayPtr < Offsets.MinValidPtr) return string.Empty;

            int runesLen = _memory.ReadInt32(runesArrayPtr + Offsets.IL2CppArray.Length);
            if (runesLen <= 0 || runesLen > 20) return string.Empty;

            var sb = new StringBuilder(runesLen);
            for (int j = 0; j < runesLen; j++)
            {
                ulong spritePtr = _memory.ReadPointer(runesArrayPtr + Offsets.IL2CppArray.FirstElem + (ulong)(j * 8));
                if (spritePtr < Offsets.MinValidPtr) break;

                // Sprite.name is first field at +0x10 (pointer to IL2CPP string object)
                ulong spriteNameStrPtr = _memory.ReadPointer(spritePtr + 0x10);
                if (spriteNameStrPtr < Offsets.MinValidPtr) break;

                string? spriteName = _memory.ReadMonoString(spriteNameStrPtr, maxLength: 32);
                if (string.IsNullOrEmpty(spriteName)) break;

                sb.Append(spriteName[0]);
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[reader] ReadRuneGuess error: {ex.Message}");
            return string.Empty;
        }
    }
}
