using System;
using System.Runtime.CompilerServices;

namespace Comet.Core;

// Event time and identity are read once on the game thread. Only detached
// value data enters the serializer; no bot, component or policy is retained.
public readonly struct ReplayStamp
{
    private readonly string session, utc, profileId, name, botInstance;
    private readonly long sequence, configRevision;
    private readonly float realtime;

    public ReplayStamp(string session, long sequence, float realtime, string utc,
        long configRevision, string profileId, string name, string botInstance)
    {
        this.session = session;
        this.sequence = sequence;
        this.realtime = realtime;
        this.utc = utc;
        this.configRevision = configRevision;
        this.profileId = profileId;
        this.name = name;
        this.botInstance = botInstance;
    }

    public object Capture(TacticalDevicesTrace trace, TacticalDevicesConfig? config) =>
        new
        {
            schema = 1, session, sequence, feature = "L02", stage = "evaluated",
            decision = "TacticalDevices", reason = trace.Reason, realtime, utc, configRevision,
            profileId, name, botInstance,
            tacticalDevices = trace.CopyForReplay(),
            tacticalDevicesConfig = ConfigSnapshot<TacticalDevicesConfig>.Get(config, configRevision, static c => c.CopyForReplay()),
        };

    public object Capture(WeaponRangeTrace trace, WeaponRangeConfig? config) =>
        new
        {
            schema = 1, session, sequence, feature = "W05", stage = "evaluated",
            decision = "WeaponRange", reason = trace.Reason, realtime, utc, configRevision,
            profileId, name, botInstance,
            weaponRange = trace.CopyForReplay(),
            weaponRangeConfig = ConfigSnapshot<WeaponRangeConfig>.Get(config, configRevision, static c => c.CopyForReplay()),
        };

    public object Capture(CoverTravelTrace trace, CoverTravelConfig? config) =>
        new
        {
            schema = 1, session, sequence, feature = "M09", stage = "evaluated",
            decision = "CoverTravel", reason = trace.Reason, realtime, utc, configRevision,
            profileId, name, botInstance,
            coverTravel = trace.CopyForReplay(),
            coverTravelConfig = ConfigSnapshot<CoverTravelConfig>.Get(config, configRevision, static c => c.CopyForReplay()),
        };

    // A configuration revision is immutable once published. Do not retain
    // the live section in the worker, or clone it for every bot observation.
    private sealed class ConfigSnapshot<T> where T : class
    {
        private static readonly ConditionalWeakTable<T, ConfigSnapshot<T>> Copies = new();
        private long revision;
        private T? value;

        internal static T? Get(T? source, long revision, Func<T, T> copy)
        {
            if (source == null) return null;
            if (revision <= 0) return copy(source);
            var slot = Copies.GetOrCreateValue(source);
            lock (slot)
            {
                if (slot.value == null || slot.revision != revision)
                {
                    slot.value = copy(source);
                    slot.revision = revision;
                }
                return slot.value;
            }
        }
    }
}
