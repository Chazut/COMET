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

    // Only COMET's own schema-1 producers feed this stream. Their feature and stage
    // precede nested evidence, even when a detailed config snapshot follows.
    internal static bool Keep(string feature, string stage, string decision, bool raidReview)
    {
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
