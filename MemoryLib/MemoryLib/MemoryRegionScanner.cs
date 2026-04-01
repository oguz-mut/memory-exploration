using System;
using System.Collections.Generic;
using System.Text;
using MemoryLib.Models;

namespace MemoryLib;

public sealed class MemoryRegionScanner
{
    private const uint MEM_COMMIT       = 0x1000u;
    private const uint MEM_PRIVATE      = 0x20000u;
    private const uint PAGE_READABLE_MASK = 0x66u;

    private readonly ProcessMemory _memory;

    public MemoryRegionScanner(ProcessMemory memory)
    {
        _memory = memory;
    }

    public List<MemoryRegion> GetGameRegions()
    {
        var regions = new List<MemoryRegion>();
        ulong addr = 0;

        while (true)
        {
            if (!_memory.QueryRegion(addr, out var info))
                break;

            if (info.State == MEM_COMMIT &&
                info.Type == MEM_PRIVATE &&
                (info.Protect & PAGE_READABLE_MASK) != 0)
            {
                regions.Add(new MemoryRegion
                {
                    BaseAddress = info.BaseAddress,
                    Size        = info.RegionSize
                });
            }

            ulong next = info.BaseAddress + info.RegionSize;
            if (next <= addr) break;
            addr = next;
        }

        return regions;
    }

    public List<ScanMatch> ScanForUtf16String(string text, int maxResults = 100)
    {
        byte[] pattern = Encoding.Unicode.GetBytes(text);
        return ScanForBytePattern(pattern, null, maxResults);
    }

    public List<ScanMatch> ScanForAsciiString(string text, int maxResults = 100)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(text);
        return ScanForBytePattern(pattern, null, maxResults);
    }

    public List<ScanMatch> ScanForBytePattern(byte[] pattern, byte[]? mask, int maxResults = 100)
    {
        var results = new List<ScanMatch>();
        if (pattern.Length == 0) return results;

        int chunkSize = 8 * 1024 * 1024;
        int overlap   = pattern.Length - 1;

        foreach (var region in GetGameRegions())
        {
            if (results.Count >= maxResults) break;

            ulong regionReadOffset = 0;
            byte[] carryOver = Array.Empty<byte>();
            ulong carryBase  = region.BaseAddress;

            while (regionReadOffset < region.Size && results.Count < maxResults)
            {
                ulong remaining = region.Size - regionReadOffset;
                int readSize    = (int)Math.Min(remaining, (ulong)chunkSize);
                ulong readAddr  = region.BaseAddress + regionReadOffset;

                byte[]? newChunk = _memory.ReadBytes(readAddr, readSize);
                if (newChunk == null)
                {
                    // Skip this chunk; reset carry so we don't produce bogus cross-chunk matches
                    carryOver = Array.Empty<byte>();
                    carryBase = readAddr + (ulong)readSize;
                    regionReadOffset += (ulong)readSize;
                    continue;
                }

                // Combine carry-over with new chunk for cross-boundary pattern matching
                byte[] data;
                ulong dataBase;
                if (carryOver.Length > 0)
                {
                    data = new byte[carryOver.Length + newChunk.Length];
                    Array.Copy(carryOver, 0, data, 0, carryOver.Length);
                    Array.Copy(newChunk, 0, data, carryOver.Length, newChunk.Length);
                    dataBase = carryBase;
                }
                else
                {
                    data     = newChunk;
                    dataBase = readAddr;
                }

                // Naive scan
                int searchEnd = data.Length - pattern.Length + 1;
                for (int i = 0; i < searchEnd && results.Count < maxResults; i++)
                {
                    bool match = true;
                    for (int j = 0; j < pattern.Length; j++)
                    {
                        if (mask != null && mask[j] == 0x00) continue; // wildcard
                        if (data[i + j] != pattern[j]) { match = false; break; }
                    }
                    if (match)
                    {
                        ulong matchAddr = dataBase + (ulong)i;
                        results.Add(new ScanMatch
                        {
                            Address      = matchAddr,
                            Region       = region,
                            RegionOffset = matchAddr - region.BaseAddress
                        });
                    }
                }

                // Save last `overlap` bytes of new chunk as carry for next iteration
                int newCarryLen = Math.Min(overlap, newChunk.Length);
                if (newCarryLen > 0)
                {
                    carryOver = new byte[newCarryLen];
                    Array.Copy(newChunk, newChunk.Length - newCarryLen, carryOver, 0, newCarryLen);
                    carryBase = readAddr + (ulong)(newChunk.Length - newCarryLen);
                }
                else
                {
                    carryOver = Array.Empty<byte>();
                    carryBase = readAddr + (ulong)newChunk.Length;
                }

                regionReadOffset += (ulong)readSize;
            }
        }

        return results;
    }

    public List<ScanMatch> ScanForPointerTo(ulong targetAddr, int maxResults = 100)
    {
        byte[] pattern = BitConverter.GetBytes(targetAddr);
        return ScanForBytePattern(pattern, null, maxResults);
    }

    public string HexDump(ulong addr, int count)
    {
        byte[]? data = _memory.ReadBytes(addr, count);
        if (data == null) return "(read failed)";

        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            ulong rowAddr = addr + (ulong)i;
            int rowLen    = Math.Min(16, data.Length - i);

            var hex   = new StringBuilder();
            var ascii = new StringBuilder();

            for (int j = 0; j < 16; j++)
            {
                if (j < rowLen)
                {
                    byte b = data[i + j];
                    if (j > 0) hex.Append(' ');
                    hex.Append(b.ToString("X2"));
                    ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                else
                {
                    if (j > 0) hex.Append(' ');
                    hex.Append("  ");
                    ascii.Append(' ');
                }
            }

            sb.Append($"0x{rowAddr:X12}: {hex}  {ascii}\n");
        }

        return sb.ToString();
    }
}
