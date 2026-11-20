using SAIN.Components;
using SAIN.Models.Structs;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace SAIN.Models.Structs
{
    public struct SAINHearingReport
    {
        public SAINSoundType soundType;
        public Vector3 position;
    }
}

namespace SAIN.Components.BotComponentSpace.Classes.EnemyClasses
{
    public class EnemyHearing(BotComponent bot)
    {
        public BotComponent Bot { get; } = bot;
        public void UpdateHeardPosition(SAINHearingReport report, float currentTime) { }
    }
}

namespace Comet.Core
{
    public class HearingConsumerToggle
    {
        public bool Enabled;
    }
}
