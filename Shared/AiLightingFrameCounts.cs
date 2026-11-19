using System;

namespace Comet.Core;

internal sealed class AiLightingFrameCounts
{
    private int lastFrame = -1;
    private int samplesThisFrame;
    internal int MaxSamplesPerFrame { get; private set; }
    internal int FramesOverBudget { get; private set; }

    internal void Record(int frame)
    {
        if (frame != lastFrame)
        {
            lastFrame = frame;
            samplesThisFrame = 0;
        }
        samplesThisFrame++;
        MaxSamplesPerFrame = Math.Max(MaxSamplesPerFrame, samplesThisFrame);
        if (samplesThisFrame == 2)
            FramesOverBudget++;
    }
}
