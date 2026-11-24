using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Comet.Client;

// The original JSON still goes to disk, Raid Review and Satellite. Only the background summaries use
// this projection, so large feature/configuration snapshots need no JToken tree.
internal sealed class DiagnosticEventReader
{
    private static readonly HashSet<string> SummaryFields = new(StringComparer.Ordinal)
    {
        "schema", "session", "feature", "stage", "realtime", "botInstance", "profileId",
        "personality", "name", "enabled", "configRevision", "maxOpportunities", "decision",
        "reason", "tacticObservationVersion", "mixing", "candidates", "doorFakeGrenade",
        "doorFakeMedical", "sequence", "combatEnabled", "weaponEnabled", "count",
        "combatJournal", "weaponJournal", "controlJournal",
    };
    private static readonly DefaultJsonNameTable PropertyNames = CreateNameTable();
    private static readonly JsonLoadSettings SummarySettings = new()
    {
        LineInfoHandling = LineInfoHandling.Ignore,
    };

    // Populate once. Concurrent readers look up known field names without caching raid data.
    private static DefaultJsonNameTable CreateNameTable()
    {
        var names = new DefaultJsonNameTable();
        foreach (string field in SummaryFields)
            names.Add(field);
        return names;
    }

    private sealed class ReaderBufferPool : IArrayPool<char>
    {
        internal static readonly ReaderBufferPool Instance = new();
        public char[] Rent(int minimumLength) => ArrayPool<char>.Shared.Rent(minimumLength);
        public void Return(char[]? array)
        {
            if (array != null)
                ArrayPool<char>.Shared.Return(array);
        }
    }

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
            ArrayPool = ReaderBufferPool.Instance,
            PropertyNameTable = PropertyNames,
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
            if (SummaryFields.Contains(property))
                result[property] = JToken.ReadFrom(reader, SummarySettings);
            else
            {
                reader.Skip();
                skipped++;
            }
        }
        throw new JsonReaderException("Unterminated diagnostic object.");
    }

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
