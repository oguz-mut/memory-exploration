using Xunit;

public class MonoRandomTests
{
    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var rng1 = new MonoRandom(42);
        var rng2 = new MonoRandom(42);

        for (int i = 0; i < 100; i++)
            Assert.Equal(rng1.Next(1000), rng2.Next(1000));
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var rng1 = new MonoRandom(42);
        var rng2 = new MonoRandom(99);

        bool anyDifferent = false;
        for (int i = 0; i < 20; i++)
        {
            if (rng1.Next(1000) != rng2.Next(1000))
            {
                anyDifferent = true;
                break;
            }
        }
        Assert.True(anyDifferent);
    }

    [Fact]
    public void Clone_ProducesIdenticalSequence()
    {
        var rng = new MonoRandom(123);
        // Advance a bit
        for (int i = 0; i < 10; i++) rng.Next(100);

        var clone = (MonoRandom)rng.Clone();

        for (int i = 0; i < 100; i++)
            Assert.Equal(rng.Next(1000), clone.Next(1000));
    }

    [Fact]
    public void Next_ReturnsValuesInRange()
    {
        var rng = new MonoRandom(7);
        for (int i = 0; i < 500; i++)
        {
            int val = rng.Next(10);
            Assert.InRange(val, 0, 9);
        }
    }

    [Fact]
    public void Next_MaxValue1_AlwaysReturnsZero()
    {
        var rng = new MonoRandom(55);
        for (int i = 0; i < 50; i++)
            Assert.Equal(0, rng.Next(1));
    }

    [Fact]
    public void CallCount_TracksCorrectly()
    {
        var rng = new MonoRandom(1);
        Assert.Equal(0, rng.CallCount);

        for (int i = 0; i < 25; i++) rng.Next(100);
        Assert.Equal(25, rng.CallCount);
    }
}
