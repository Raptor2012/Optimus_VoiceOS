namespace Optimus.Inference.Tests;

using System;
using System.Buffers.Binary;
using Xunit;

public class CommanderVoiceEffectTests
{
    [Fact]
    public void SilenceStaysSilentAndRegisterChangeHasExpectedDuration()
    {
        byte[] output = CommanderVoiceEffect.Apply(new byte[44100], 22050, 0.93, 0.06);
        Assert.Equal((int)(22050 / 0.93) * 2, output.Length);
        Assert.All(output, value => Assert.Equal(0, value));
    }

    [Fact]
    public void NeutralProfilePreservesSamples()
    {
        byte[] input = new byte[400];
        for (int i = 0; i < 200; i++)
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 2, 2), (short)(Math.Sin(i) * 20000));
        Assert.Equal(input, CommanderVoiceEffect.Apply(input, 22050, 1, 0));
    }
}
