using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Comet.Client;

// The original JSON still goes to disk and RR. Only the background summaries use
// this projection, so large feature/configuration snapshots need no JToken tree.
internal sealed class DiagnosticEventReader
{
    internal long ParsedEvents,
        ProjectedEvents,
        SkippedProperties,
        LegacyFallbacks;

    internal static JObject Read(string json, out int skipped)
    {
        skipped = 0;
        if (json.Length > 262144)
            throw new JsonReaderException("Diagnostic event exceeds the summary limit.");
        using var reader = new JsonTextReader(new StringReader(json))
        {
            MaxDepth = 48,
            DateParseHandling = DateParseHandling.None,
        };
        while (reader.Read() && reader.TokenType == JsonToken.Comment) { }
        if (reader.TokenType != JsonToken.StartObject)
            throw new JsonReaderException("Expected diagnostic object.");
        var result = new JObject();
        while (reader.Read())
        {
            if (reader.TokenType == JsonToken.Comment)
                continue;
            if (reader.TokenType == JsonToken.EndObject)
                return result;
            if (reader.TokenType != JsonToken.PropertyName)
                throw new JsonReaderException("Expected diagnostic property.");
            string property = (string)reader.Value!;
            do
            {
                if (!reader.Read())
                    throw new JsonReaderException("Missing diagnostic value.");
            } while (reader.TokenType == JsonToken.Comment);

            // This union covers both summary consumers. It does not depend on
            // property order or on a feature ID appearing inside a nested object.
            if (Needed(property))
                result[property] = JToken.ReadFrom(reader);
            else
            {
                reader.Skip();
                skipped++;
            }
        }
        throw new JsonReaderException("Unterminated diagnostic object.");
    }

    private static bool Needed(string property) =>
        property
            is "schema"
                or "session"
                or "feature"
                or "stage"
                or "realtime"
                or "botInstance"
                or "profileId"
                or "personality"
                or "name"
                or "enabled"
                or "configRevision"
                or "maxOpportunities"
                or "decision"
                or "reason"
                or "tacticObservationVersion"
                or "mixing"
                or "candidates"
                or "doorFakeGrenade"
                or "doorFakeMedical"
                or "sequence"
                or "combatEnabled"
                or "weaponEnabled"
                or "count"
                or "combatJournal"
                or "weaponJournal"
                or "controlJournal";

    internal void Observe(string json, TacticCounters counters, CombatReport journal)
    {
        JObject e;
        try
        {
            e = Read(json, out int skipped);
            ParsedEvents++;
            SkippedProperties += skipped;
            if (skipped != 0)
                ProjectedEvents++;
        }
        catch
        {
            // Preserve each consumer's existing malformed/oversize accounting.
            // No corrupt event may terminate the writer or hide later events.
            LegacyFallbacks++;
            counters.Observe(json);
            journal.Observe(json);
            return;
        }
        counters.Observe(e);
        journal.Observe(e);
    }
}
