using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Comet.Client;

internal static class EssentialReplay
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema", "session", "sequence", "feature", "stage", "decision", "reason", "realtime",
        "profileId", "name", "botInstance", "configRevision", "tacticObservationVersion",
        "contact", "crossfire", "squadGrenade", "siege", "magazineRecovery", "postCombat", "lifecycle",
    };

    internal static string Gesture(string session, long sequence, string feature, string decision, float realtime, string profileId, string name, string botInstance) =>
        ActionEvent(session, sequence, feature, "performed", decision, realtime, profileId, name, botInstance);

    internal static string ActionEvent(string session, long sequence, string feature, string stage, string decision, float realtime, string profileId, string name, string botInstance) =>
        Diagnostics.Serialize(new { schema = 1, session, sequence, feature, stage, decision, realtime, profileId, name, botInstance });

    // Only COMET's own schema-1 producers feed this stream. Their feature and stage
    // precede nested evidence, even when a detailed config snapshot follows.
    internal static bool Keep(string feature, string stage, string decision, bool raidReview)
    {
        if (stage is "activity-start" or "activity-end")
            return feature is "H01" or "G08" or "G09";
        if (stage == "performed" && feature is ("C07" or "D03" or "G05" or "H01" or "H02" or "L03" or "M13" or "W02" or "W03"))
            return true;
        if (stage == "performed")
            return feature is "M03" or "M06" or "M11" or "M12" or "M14" or "D02" or "D04" or "D05" or "D06" or "D07" or "D08";
        if (feature is "S10" or "S11")
            return true;
        if (feature == "W04")
            return decision == "RecoverMagazine";
        if (decision == "Crossfire" && feature is ("S01" or "S02" or "S03" or "S04" or "S05" or "S06"))
            return true;
        // A13 reports a handoff to native AI, not a COMET action.
        if (!raidReview || feature == "A13")
            return false;
        if (feature is "H04" or "C09")
            return stage == "evaluated";
        if (feature == "C13")
            return stage == "cleanup";
        return Comet.Core.ReplayCapture.IsActionStage(stage);
    }

    internal static string? Project(string json, bool raidReview)
    {
        using var text = new StringReader(json);
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
        if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
            return null;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var writer = new JsonTextWriter(output);
        writer.WriteStartObject();
        string feature = "", stage = "", decision = "";
        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
        {
            var key = (string)reader.Value!;
            if (!reader.Read()) return null;
            if (key == "feature") feature = reader.Value as string ?? "";
            if (key == "stage") stage = reader.Value as string ?? "";
            if (key == "decision") decision = reader.Value as string ?? "";
            if (feature.Length != 0 && stage.Length != 0 && decision.Length != 0 &&
                !Keep(feature, stage, decision, raidReview))
                return null;
            if (!Fields.Contains(key))
            {
                reader.Skip();
                continue;
            }
            writer.WritePropertyName(key);
            writer.WriteToken(reader, true);
        }
        if (!Keep(feature, stage, decision, raidReview)) return null;
        writer.WriteEndObject();
        return output.ToString();
    }
}
