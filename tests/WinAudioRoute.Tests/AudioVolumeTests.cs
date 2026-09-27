namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="AudioVolume"/> 的转换与钳制契约测试。
/// </summary>
public class AudioVolumeTests
{
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1.0f)]
    public void Clamp_KeepsInRangeValues(float value)
    {
        Assert.Equal(value, AudioVolume.Clamp(value));
    }

    [Theory]
    [InlineData(-0.001f)]
    [InlineData(-1.0f)]
    [InlineData(float.MinValue)]
    public void Clamp_ClampsBelowZero_ToZero(float value)
    {
        Assert.Equal(0.0f, AudioVolume.Clamp(value));
    }

    [Theory]
    [InlineData(1.0001f)]
    [InlineData(2.0f)]
    [InlineData(float.MaxValue)]
    public void Clamp_ClampsAboveOne_ToOne(float value)
    {
        Assert.Equal(1.0f, AudioVolume.Clamp(value));
    }

    [Fact]
    public void Clamp_NaN_BecomesZero()
    {
        Assert.Equal(0.0f, AudioVolume.Clamp(float.NaN));
    }

    [Theory]
    [InlineData(0, 0.0f)]
    [InlineData(1, 0.01f)]
    [InlineData(35, 0.35f)]
    [InlineData(50, 0.5f)]
    [InlineData(100, 1.0f)]
    public void FromPercent_ConvertsInRange(int percent, float expected)
    {
        Assert.Equal(expected, AudioVolume.FromPercent(percent), precision: 6);
    }

    [Theory]
    [InlineData(-1, 0.0f)]
    [InlineData(-100, 0.0f)]
    [InlineData(101, 1.0f)]
    [InlineData(int.MaxValue, 1.0f)]
    public void FromPercent_ClampsOutOfRange(int percent, float expected)
    {
        Assert.Equal(expected, AudioVolume.FromPercent(percent));
    }

    [Theory]
    [InlineData(0.0f, 0)]
    [InlineData(0.5f, 50)]
    [InlineData(1.0f, 100)]
    [InlineData(0.35f, 35)]
    [InlineData(0.999f, 100)]
    [InlineData(0.004f, 0)]
    public void ToPercent_ConvertsAndRounds(float scalar, int expected)
    {
        Assert.Equal(expected, AudioVolume.ToPercent(scalar));
    }

    [Theory]
    [InlineData(-1.0f, 0)]
    [InlineData(2.0f, 100)]
    [InlineData(float.NaN, 0)]
    public void ToPercent_ClampsOutOfRange(float scalar, int expected)
    {
        Assert.Equal(expected, AudioVolume.ToPercent(scalar));
    }

    [Theory]
    [InlineData(0.35f, 35)]
    [InlineData(0.5f, 50)]
    [InlineData(1.0f, 100)]
    [InlineData(-0.5f, 0)]
    public void Percent_RoundTrip(float scalar, int expectedPercent)
    {
        Assert.Equal(expectedPercent, AudioVolume.ToPercent(AudioVolume.FromPercent(AudioVolume.ToPercent(scalar))));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void ClampPercent_Clamps(int value, int expected)
    {
        Assert.Equal(expected, AudioVolume.ClampPercent(value));
    }

    [Theory]
    [InlineData(0.0f, true)]
    [InlineData(0.5f, true)]
    [InlineData(1.0f, true)]
    [InlineData(-0.01f, false)]
    [InlineData(1.01f, false)]
    [InlineData(float.NaN, false)]
    public void IsValidScalar_Check(float value, bool expected)
    {
        Assert.Equal(expected, AudioVolume.IsValidScalar(value));
    }

    [Fact]
    public void Constants_AreExpected()
    {
        Assert.Equal(0.0f, AudioVolume.MinScalar);
        Assert.Equal(1.0f, AudioVolume.MaxScalar);
        Assert.Equal(0, AudioVolume.MinPercent);
        Assert.Equal(100, AudioVolume.MaxPercent);
    }
}
