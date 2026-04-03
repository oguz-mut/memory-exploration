// MonoRandom — Mono's System.Random (Knuth subtractive generator)

class MonoRandom : ICloneable
{
    private const int MBIG = int.MaxValue;
    private const int MSEED = 161803398;

    private int[] _seedArray = new int[56];
    private int _inext;
    private int _inextp;
    public int CallCount { get; private set; }

    public MonoRandom(int seed)
    {
        int ii;
        int mj, mk;

        mj = MSEED - Math.Abs(seed);
        _seedArray[55] = mj;
        mk = 1;
        for (int i = 1; i < 55; i++)
        {
            ii = (21 * i) % 55;
            _seedArray[ii] = mk;
            mk = mj - mk;
            if (mk < 0) mk += MBIG;
            mj = _seedArray[ii];
        }
        for (int k = 1; k <= 4; k++)
        {
            for (int i = 1; i < 56; i++)
            {
                _seedArray[i] -= _seedArray[1 + (i + 30) % 55];
                if (_seedArray[i] < 0) _seedArray[i] += MBIG;
            }
        }
        _inext = 0;
        _inextp = 21;
        CallCount = 0;
    }

    /// <summary>Reconstruct from memory-read PRNG state.</summary>
    public MonoRandom(int[] seedArray, int inext, int inextp)
    {
        Array.Copy(seedArray, _seedArray, 56);
        _inext = inext;
        _inextp = inextp;
        CallCount = 0;
    }

    private MonoRandom() { CallCount = 0; }

    private int InternalSample()
    {
        int retVal;
        int locINext = _inext;
        int locINextp = _inextp;

        if (++locINext >= 56) locINext = 1;
        if (++locINextp >= 56) locINextp = 1;

        retVal = _seedArray[locINext] - _seedArray[locINextp];
        if (retVal < 0) retVal += MBIG;

        _seedArray[locINext] = retVal;
        _inext = locINext;
        _inextp = locINextp;

        return retVal;
    }

    public int Next(int maxValue)
    {
        CallCount++;
        return (int)(InternalSample() * (1.0 / MBIG) * maxValue);
    }

    public object Clone()
    {
        var clone = new MonoRandom();
        Array.Copy(_seedArray, clone._seedArray, 56);
        clone._inext = _inext;
        clone._inextp = _inextp;
        clone.CallCount = CallCount;
        return clone;
    }
}
