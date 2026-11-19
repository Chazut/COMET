using SAIN.Classes.Bot.Search;
using SAIN.SAINComponent.Classes.EnemyClasses;

namespace Comet.Client;

internal static class SearchExecutionAdapter
{
    internal static bool CanStart(SearchClass search, Enemy enemy, out string reason)
    {
        if (search.Decider.ShallStartSearch(enemy, out var reasons))
        {
            reason = "";
            return true;
        }
        reason =
            reasons.PathCalcFailReason != null && reasons.PathCalcFailReason.Length > 0
                ? "search-native-path-refused:" + reasons.PathCalcFailReason
                : "search-native-refused:"
                    + reasons.NotSearchReason
                    + "/"
                    + reasons.WantSearchReasons.NotWantToSearchReason
                    + "/"
                    + reasons.WantSearchReasons.CantStartReason;
        return false;
    }

    internal static bool Prepare(SearchClass search, Enemy enemy, out string reason)
    {
        var place = enemy.KnownPlaces.LastKnownPlace;
        if (place == null)
        {
            reason = "search-no-known-place";
            return false;
        }
        if (place.HasArrivedPersonal || place.HasArrivedSquad)
        {
            reason = "search-place-already-visited";
            return false;
        }
        // SAIN's normal decider initializes TargetPlace as a side effect of CheckEnemyPath.
        // Choosing Search directly must perform that initialization too. Repeating it on
        // every tick would reset native peek/wait states, so keep an unchanged place.
        if (ReferenceEquals(search.PathFinder.TargetPlace, place))
        {
            reason = "search-target-retained";
            return true;
        }
        if (!search.PathFinder.CheckEnemyPath(enemy, out var failure))
        {
            reason = "search-native-path-refused:" + failure;
            return false;
        }
        reason = "search-target-prepared";
        return true;
    }
}
