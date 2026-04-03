namespace MemoryLib;

public static class ObscuredValue
{
    // Size constants for struct embedding
    public const int ObscuredIntSize = 16;   // 4 ints
    public const int ObscuredFloatSize = 20; // 4 ints + ACTkByte4

    // Read an ObscuredInt at a given address in the target process
    // addr points to the START of the ObscuredInt struct (the hash field)
    public static int ReadObscuredInt(ProcessMemory memory, ulong addr)
    {
        int hiddenValue = memory.ReadInt32(addr + 0x04);
        int cryptoKey = memory.ReadInt32(addr + 0x08);
        if (cryptoKey == 0) return hiddenValue;
        return hiddenValue ^ cryptoKey;
    }

    // Read an ObscuredFloat
    public static float ReadObscuredFloat(ProcessMemory memory, ulong addr)
    {
        int hiddenValue = memory.ReadInt32(addr + 0x04);
        int cryptoKey = memory.ReadInt32(addr + 0x08);
        int rawBits = (cryptoKey == 0) ? hiddenValue : hiddenValue ^ cryptoKey;
        return BitConverter.Int32BitsToSingle(rawBits);
    }

    // Standalone decrypt (for when you already have the values)
    public static int DecryptInt(int hiddenValue, int cryptoKey)
    {
        if (cryptoKey == 0) return hiddenValue;
        return hiddenValue ^ cryptoKey;
    }

    public static float DecryptFloat(int hiddenValue, int cryptoKey)
    {
        int rawBits = (cryptoKey == 0) ? hiddenValue : hiddenValue ^ cryptoKey;
        return BitConverter.Int32BitsToSingle(rawBits);
    }

    // Validate: check if a memory region looks like a plausible ObscuredInt
    // (key should be nonzero and decrypted value should be in expected range)
    public static bool LooksLikeObscuredInt(ProcessMemory memory, ulong addr, int minValue, int maxValue)
    {
        int hiddenValue = memory.ReadInt32(addr + 0x04);
        int cryptoKey = memory.ReadInt32(addr + 0x08);
        if (cryptoKey == 0) return false; // If key is 0, it's probably not an ObscuredInt
        int decrypted = hiddenValue ^ cryptoKey;
        return decrypted >= minValue && decrypted <= maxValue;
    }
}
