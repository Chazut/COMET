using System;
using System.Collections.Generic;

namespace Comet.Core;

// Port of R_F's HiddenEnemyUtility.Score and VisibleEnemyUtility.Score at
// fbb86b838e0fa692b759b2c0b11fc30158a53876. Scores use x100 for the RR/UI contract.
// Cross-raid player learning stays excluded, as requested for COMET.
public static class ZzapUtility
{
    public static double Unit(double x) => Math.Max(0, Math.Min(1, x));

    public static double Clamp(double x, double low, double high) =>
        Math.Max(low, Math.Min(high, x));

    public static Dictionary<Tactic, double> Hidden(
        Situation s,
        double aggression,
        double heldFor,
        bool holdBanned
    )
    {
        double h = s.Health,
            ammo = s.Ammo,
            weak = s.Weakness,
            age = s.ContactAge;
        double quiet = Unit((age - 8) / 12),
            info = Unit(age / 30),
            dist = Unit((s.PathLength - 5) / 35);
        double cqb = s.Loadout?.Cqb ?? .5,
            longGun = s.Loadout?.Long ?? 0,
            ideal = s.Loadout?.IdealRange ?? 30;
        double fear = s.Fear?.Level ?? 0;
        bool busy = s.Busy && !s.GrenadeOut,
            justLost = age < 3 && !s.HoldsAngle && !s.BrokeOffToCover;
        bool hurtOpen = !s.InCover && h <= .35 && s.CanHeal;
        double push =
            .15 * cqb
            - .3 * longGun
            + .25
            + .3 * weak
            + (busy ? .25 : 0)
            + .15 * s.Numbers
            + .15 * aggression
            + .1 * quiet
            - (s.HoldsAngle ? .35 : 0)
            + (justLost ? .15 : 0)
            - (s.BrokeOffToCover ? .2 : 0)
            - .35 * (1 - h)
            - (ammo < .5 ? .3 : 0)
            - .15 * dist;
        double nade =
            .3
            + .05 * Math.Min(s.Loadout?.Grenades ?? 0, 3)
            + ((s.Loadout?.Grenades ?? 0) >= 3 ? .1 : 0)
            + .4 * quiet
            + (busy ? .15 : 0)
            + (s.Indoor ? .1 : 0)
            + (s.HoldsAngle ? .1 : 0)
            - .25 * info;
        double hold =
            .2 * longGun
            + .25
            + (s.Coming ? .35 : 0)
            + (s.InCover ? .25 : .05) * (1 - h)
            + (s.HoldsAngle ? .1 : 0)
            + .15 * (1 - aggression)
            - (justLost ? .05 : 0)
            + (s.InCover ? .1 : 0)
            - .3 * quiet;
        double flank =
            .2 + (s.HoldsAngle ? .3 : 0) + .15 * Math.Max(0, s.Numbers) + .1 * quiet - .2 * (1 - h);
        double search = -.15 * longGun + .15 + .35 * info + .1 * aggression - .2 * (1 - h);
        double fallBack =
            (longGun > .7 && s.PathLength < ideal * .4 ? .35 : 0)
            + .05
            + .45 * (1 - h)
            + (ammo < .25 ? .3 : 0)
            + (s.Numbers < 0 ? .25 * -s.Numbers : 0)
            + (hurtOpen ? .2 : 0);
        if (s.GrenadeOut)
        {
            push -= .35;
            flank -= .15;
            search -= .25;
            fallBack += s.PathLength < 25 ? .3 : .1;
            hold += .1;
        }
        push -= .4 * fear;
        flank -= .1 * fear;
        hold += .2 * fear;
        fallBack += .5 * fear + (fear > .75 ? .4 : 0);
        if (!s.Coming)
            hold -= Clamp((heldFor - 10) * .03, 0, .45);
        if (!s.InCover && s.OpenGround)
            hold -= .3;
        if (!s.Coming && !s.HoldsAngle)
            fallBack -= Clamp((age - 15) * .03, 0, .45);
        hold -=
            .35
            * Unit((s.KnownDistance - s.FreezeDistance) / Math.Max(1, s.FreezeDistance))
            * (1 - .6 * longGun);
        double caution = s.EnemyRole?.Caution(aggression) ?? 0;
        push -= caution * (s.OpenGround ? .80 : .55);
        if (s.InCover)
            hold += .30 * caution;
        else
            fallBack += .28 * caution;
        if (holdBanned)
            hold = -1;
        return new()
        {
            [Tactic.Push] = 100 * push,
            [Tactic.Grenade] = 100 * nade,
            [Tactic.Hold] = 100 * hold,
            [Tactic.Flank] = 100 * flank,
            [Tactic.Search] = 100 * search,
            [Tactic.FallBack] = 100 * fallBack,
        };
    }

    public static Dictionary<VisibleTactic, double> Visible(VisibleSituation v, double aggression)
    {
        var s = v.Contact;
        double h = s.Health,
            ammo = s.Ammo,
            weak = s.Weakness,
            dist = v.EnemyDistance,
            cover = v.CoverDistance;
        double cqb = s.Loadout?.Cqb ?? .5,
            longGun = s.Loadout?.Long ?? 0,
            ideal = s.Loadout?.IdealRange ?? 30;
        double ratio = dist / Math.Max(1, ideal),
            lk = s.Looking ? 1 : .3,
            exposure = v.CurrentExposure,
            fear = s.Fear?.Level ?? 0;
        bool good = ratio > .5 && ratio < 1.6,
            tooClose = ratio < .35 && longGun > .7,
            tooFar = ratio > 2 && cqb > .7;
        double closeness = Unit((4 - cover) / 3),
            near = Unit((30 - dist) / 20),
            reachable = Unit((6 - cover) / 5);
        double runCost = cover > 12 ? 0 : Clamp(cover / 3.5, 0, 3) * (.15 + .2 * near) * lk;
        bool finish = weak >= .45 && dist < 30 && ammo >= .15;
        double shoot =
            .45
            + (s.Looking ? 0 : .25)
            + .15 * weak
            + (s.Busy ? .15 : 0)
            + .1 * s.VisibleMates
            + (good ? .15 : 0)
            - (tooClose ? .15 : 0)
            - (tooFar ? .2 : 0)
            - .3 * exposure * lk
            - (v.RecentHit ? .2 * closeness : 0)
            - (ammo < .15 ? (cover <= 8 ? .4 : .15) : 0)
            - .2 * (1 - h)
            + (finish ? .2 : 0);
        double coverScore =
            .1
            + .35 * (1 - h) * (.4 + .6 * reachable)
            + (ammo < .25 ? .35 : 0)
            + (v.RecentHit ? .1 * reachable + .15 * closeness : 0)
            + .3 * exposure * lk
            + .15 * (1 - aggression)
            + .2 * reachable
            - runCost
            - (cover > 12 ? (dist < 25 ? .7 : .35) : 0)
            - (s.Looking ? 0 : .2)
            + (cover <= 12 ? (tooClose ? .3 : 0) + (tooFar ? .2 : 0) : 0)
            - (finish ? .15 : 0)
            - (s.Busy && cover > 2 ? .25 : 0);
        double push =
            .1
            + .3 * weak
            + (s.Busy && !s.GrenadeOut ? .35 : 0)
            - (s.GrenadeOut ? .3 : 0)
            + .2 * aggression
            + .15 * s.VisibleMates
            + (dist < 12 ? .15 : 0)
            - .3 * (1 - h)
            - (ammo < .4 ? .3 : 0)
            - (dist > 30 ? .25 : 0)
            - (s.Looking && !s.Busy ? .1 : 0)
            + .15 * cqb
            - .25 * longGun
            - (tooClose ? .3 : 0);
        push -= .4 * fear;
        shoot -= .1 * fear;
        coverScore += .4 * fear + (fear > .75 ? .3 : 0);
        if (dist < 12 && cover > 3 && (s.Looking || v.RecentHit))
            coverScore -= .6 + .3 * Unit((12 - dist) / 6);
        double caution = s.EnemyRole?.Caution(aggression) ?? 0;
        push -= caution * (.55 + .25 * Unit(exposure));
        if (s.InCover)
            shoot += .25 * caution;
        else if (v.CoverReachable)
            coverScore += .28 * caution;
        return new()
        {
            [VisibleTactic.Shoot] = 100 * shoot,
            [VisibleTactic.Cover] = 100 * coverScore,
            [VisibleTactic.Push] = 100 * push,
        };
    }
}
