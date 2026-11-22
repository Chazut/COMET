using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace SAIN.Preset.Shared.Enums
{
    public enum CoverStatus { InCover, CloseToCover }
}

namespace SAIN.SAINComponent.SubComponents.CoverFinder
{
    public class CoverPoint
    {
        public CoverData CoverData = new();
        public Vector3 Position;
        public bool BotInThisCover = true, Spotted;
        public Collider Collider = new();
        private CoverStatus distanceStatus = CoverStatus.CloseToCover;

        public float GetDistance(Vector3 from) => Vector3.Distance(from, Position);

        public CoverStatus GetStraightDistanceStatus(float distance)
        {
            float limit = distanceStatus == CoverStatus.InCover ? 1.5f : 1.25f;
            return distanceStatus = distance <= limit ? CoverStatus.InCover : CoverStatus.CloseToCover;
        }
    }
}
