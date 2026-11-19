using Comet.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

internal static class DiagnosticWarmupTests
{
    // The warmup must discover this closed anonymous envelope from IL without calling us.
    private static int producerCalls,
        getterCalls;

    private sealed class Sentinel
    {
        public string Value
        {
            get
            {
                getterCalls++;
                return "payload";
            }
        }
    }

    private static object Envelope()
    {
        producerCalls++;
        return new
        {
            feature = "cover",
            data = new Sentinel(),
            nested = new[] { new { score = 42.0, reasons = new[] { "hit" } } },
        };
    }

    private sealed class CountingResolver : CamelCasePropertyNamesContractResolver
    {
        internal readonly HashSet<Type> Created = new();

        protected override JsonContract CreateContract(Type type)
        {
            Created.Add(type);
            return base.CreateContract(type);
        }
    }

    internal static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("diagnostic warmup: " + why);
        }
        producerCalls = getterCalls = 0;
        var resolver = new CountingResolver();
        var report = DiagnosticWarmup.Prepare(
            typeof(DiagnosticWarmupTests).Assembly,
            resolver,
            false
        );
        Check(report.Contracts > 100, "real COMET payload contracts primed");
        Check(producerCalls == 0 && getterCalls == 0, "no producers or getters executed");
        var envelope = Envelope();
        Check(
            resolver.Created.Contains(envelope.GetType()),
            "closed anonymous producer envelope discovered from IL"
        );
        Check(
            resolver.Created.Contains(typeof(Sentinel)),
            "nested payload primed without calling getter"
        );
        var settings = new JsonSerializerSettings { ContractResolver = resolver };
        int before = resolver.Created.Count;
        string json = JsonConvert.SerializeObject(envelope, settings);
        Check(before == resolver.Created.Count, "first serialization creates no new contracts");
        Check(
            json
                == JsonConvert.SerializeObject(
                    envelope,
                    new JsonSerializerSettings
                    {
                        ContractResolver = new CamelCasePropertyNamesContractResolver(),
                    }
                ),
            "payload unchanged"
        );
        Check(getterCalls == 2 && producerCalls == 1, "serialization keeps its normal behavior");
        var prepared = new PreparedDiagnosticResolver();
        DiagnosticWarmup.Prepare(typeof(DiagnosticWarmupTests).Assembly, prepared, false);
        Check(
            getterCalls == 2 && producerCalls == 1,
            "eager readers never execute payload getters/producers"
        );
        Check(
            prepared.ReadersPrepared > 100 && prepared.ReadersFallback == 0,
            "readers compiled before first event"
        );
        var preparedSettings = new JsonSerializerSettings { ContractResolver = prepared };
        Check(
            json == JsonConvert.SerializeObject(envelope, preparedSettings),
            "prepared JSON matches default reader contract"
        );
        Check(
            json == Diagnostics.Serialize(envelope),
            "pooled serializer preserves complete payload"
        );
        var concurrent = Enumerable
            .Range(0, 128)
            .Select(i =>
                Task.Run(() =>
                {
                    var payload = new
                    {
                        sequence = i,
                        text = "escaped \\\" value",
                        nested = new[] { i, i + 1 },
                        missing = (string?)null,
                    };
                    return Diagnostics.Serialize(payload)
                        == JsonConvert.SerializeObject(payload, Diagnostics.Json);
                })
            )
            .ToArray();
        Task.WaitAll(concurrent);
        Check(
            concurrent.All(x => x.Result),
            "thread-local serializers cannot mix concurrent raid events"
        );
        try
        {
            Diagnostics.Serialize(new ThrowingPayload());
            throw new Exception("missing failure");
        }
        catch (Exception ex)
            when (ex is JsonSerializationException || ex is InvalidOperationException) { }
        Check(
            Diagnostics.Serialize(new { ok = true }) == "{\"ok\":true}",
            "writer recovers after a failed property getter"
        );
        Check(
            Diagnostics.Serialize(new ReentrantPayload()).Contains("nested"),
            "recursive diagnostic serialization preserves outer writer"
        );
        var originalDefaults = JsonConvert.DefaultSettings;
        try
        {
            JsonConvert.DefaultSettings = () =>
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            Check(
                Diagnostics.Serialize(new { missing = (string?)null }) == "{\"missing\":null}",
                "backend null filtering cannot remove diagnostic fields"
            );
        }
        finally
        {
            JsonConvert.DefaultSettings = originalDefaults;
        }
        Check(
            Diagnostics.Serialize(new { missing = (string?)null })
                == JsonConvert.SerializeObject(new { missing = (string?)null }, Diagnostics.Json),
            "writer refreshes when application defaults change"
        );
        var sample = new
        {
            schema = 1,
            feature = "A01",
            decision = "Hold",
            candidates = Enumerable
                .Range(0, 12)
                .Select(i => new Comet.Core.Candidate
                {
                    Decision = "Search" + i,
                    Score = i,
                    Eligible = true,
                    Reason = "available",
                })
                .ToArray(),
        };
        string expected = JsonConvert.SerializeObject(sample, Diagnostics.Json);
        Check(
            Diagnostics.Serialize(sample) == expected,
            "representative scored decision is byte-identical"
        );
        (long bytes, double ms) Measure(Func<string> emit)
        {
            for (int i = 0; i < 20; i++)
                emit();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
                emit();
            return (GC.GetAllocatedBytesForCurrentThread() - before, sw.Elapsed.TotalMilliseconds);
        }
        var allocating = Measure(() => JsonConvert.SerializeObject(sample, Diagnostics.Json));
        var pooled = Measure(() => Diagnostics.Serialize(sample));
        Check(
            pooled.bytes < allocating.bytes,
            "pooling reduces allocations without removing diagnostic fields"
        );
        Console.WriteLine(
            $"Diagnostic serialization benchmark (CoreCLR, 1000 events): before={allocating.bytes}B/{allocating.ms:F1}ms after={pooled.bytes}B/{pooled.ms:F1}ms; raid timing remains unverified."
        );
        var oldSerializer = JsonSerializer.CreateDefault(Diagnostics.Json);
        var oldBuffer = new System.Text.StringBuilder(4096);
        string PreviousSnapshot()
        {
            oldBuffer.Clear();
            using var output = new StringWriter(
                oldBuffer,
                System.Globalization.CultureInfo.InvariantCulture
            );
            using var writer = new JsonTextWriter(output);
            oldSerializer.Serialize(writer, sample);
            writer.Flush();
            return oldBuffer.ToString();
        }
        Check(
            PreviousSnapshot() == Diagnostics.Serialize(sample),
            "previous pooled writer and typed snapshot are byte-identical"
        );
        // Compare to the preceding build's pooled implementation, not only JsonConvert.
        // Multiple rounds reduce first-tier JIT noise; these are desktop measurements.
        var baselineRounds = new List<(long bytes, double ms)>();
        var typedRounds = new List<(long bytes, double ms)>();
        for (int round = 0; round < 5; round++)
        {
            baselineRounds.Add(Measure(PreviousSnapshot));
            typedRounds.Add(Measure(() => Diagnostics.Serialize(sample)));
        }
        var baseline = baselineRounds.OrderBy(x => x.ms).ElementAt(2);
        var typed = typedRounds.OrderBy(x => x.ms).ElementAt(2);
        Check(
            typed.bytes < baseline.bytes,
            "typed DTO properties reduce allocations beyond existing pooling"
        );
        Console.WriteLine(
            $"Typed vs previous pooled snapshot (CoreCLR, median 5x1000): before={baseline.bytes}B/{baseline.ms:F1}ms after={typed.bytes}B/{typed.ms:F1}ms."
        );
        var originalConfig = new Comet.Core.CometConfig();
        string expectedConfig = JsonConvert.SerializeObject(
            originalConfig,
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
            }
        );
        Check(
            expectedConfig == JsonConvert.SerializeObject(originalConfig, preparedSettings),
            "full configuration payload unchanged"
        );
        var plainSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
        };
        // Newtonsoft's default Populate behavior appends to initialized collections.
        // Compare both readers after the same round trip, not against the original.
        var expectedRoundTrip = JsonConvert.SerializeObject(
            JsonConvert.DeserializeObject<Comet.Core.CometConfig>(expectedConfig, plainSettings),
            plainSettings
        );
        Check(
            JsonConvert.SerializeObject(
                JsonConvert.DeserializeObject<Comet.Core.CometConfig>(
                    expectedConfig,
                    preparedSettings
                ),
                preparedSettings
            ) == expectedRoundTrip,
            "reader retains native setters and collection population behavior"
        );
        Check(
            expectedConfig == Diagnostics.Serialize(originalConfig),
            "typed writer preserves complete configuration"
        );
        var fast = new SnapshotDiagnosticResolver();
        int gettersBefore = getterCalls;
        DiagnosticWarmup.Prepare(typeof(DiagnosticWarmupTests).Assembly, fast, false);
        Check(
            fast.WritersPrepared > 100 && getterCalls == gettersBefore,
            "typed writers compiled without running getters"
        );
        int dtoCount = 0;
        foreach (
            var type in typeof(Comet.Core.CometConfig)
                .Assembly.GetTypes()
                .Where(t =>
                    t.Namespace == "Comet.Core"
                    && !t.IsAbstract
                    && !t.ContainsGenericParameters
                    && t.GetConstructor(Type.EmptyTypes) != null
                )
        )
        {
            object value = Activator.CreateInstance(type)!;
            Check(
                JsonConvert.SerializeObject(value, plainSettings) == Diagnostics.Serialize(value),
                "typed DTO unchanged: " + type.Name
            );
            dtoCount++;
        }
        Check(dtoCount > 100, "broad production DTO coverage");
        var special = new
        {
            missing = (string?)null,
            number = (int?)7,
            absent = (int?)null,
            flag = (bool?)true,
            enumValue = DayOfWeek.Friday,
            text = "quote \" slash \\ newline\n한국어",
            nan = double.NaN,
            infinity = float.PositiveInfinity,
            guid = Guid.Parse("b993df34-34e9-49f0-864b-0ef963be48b7"),
            time = new DateTime(2026, 11, 7, 1, 2, 3, DateTimeKind.Utc),
            bytes = new byte[] { 0, 1, 255 },
            dict = new Dictionary<string, object?> { { "Score", 2.5 }, { "Null", null } },
            token = Newtonsoft.Json.Linq.JObject.Parse("{\"unchangedName\":true}"),
        };
        Check(
            JsonConvert.SerializeObject(special, plainSettings) == Diagnostics.Serialize(special),
            "null, nullable, enum, unicode, special numbers and containers unchanged"
        );
        var mutable = new Comet.Core.CrossfireTrace { GroundPaused = true, GroundPauses = 1 };
        string frozen = Diagnostics.Serialize(new { trace = mutable });
        mutable.GroundPaused = false;
        mutable.GroundPauses = 2;
        Check(
            frozen.Contains("\"groundPaused\":true") && frozen.Contains("\"groundPauses\":1"),
            "event-time DTO snapshot detached before background writing"
        );
        string isolated = Diagnostics.Serialize(special);
        try
        {
            int converterCalls = 0;
            foreach (
                var global in new[]
                {
                    new JsonSerializerSettings(),
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore },
                    new JsonSerializerSettings
                    {
                        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
                    },
                    new JsonSerializerSettings
                    {
                        Converters = { new BackendConverter(() => converterCalls++) },
                        DefaultValueHandling = DefaultValueHandling.Ignore,
                    },
                    new JsonSerializerSettings { Error = (_, e) => e.ErrorContext.Handled = true },
                }
            )
            {
                JsonConvert.DefaultSettings = () => global;
                Check(
                    Diagnostics.Serialize(special) == isolated,
                    "game defaults cannot alter journal schema"
                );
                Check(
                    (bool)
                        Newtonsoft.Json.Linq.JObject.Parse(
                            Diagnostics.Serialize(Diagnostics.SerializerMetrics())
                        )["typedSnapshots"]!,
                    "backend converters never disable prepared writers"
                );
            }
            Check(converterCalls == 0, "backend converter not probed on the combat thread");
            try
            {
                Diagnostics.Serialize(new ThrowingPayload());
                throw new Exception("missing failure");
            }
            catch (Exception e)
                when (e is JsonSerializationException || e is InvalidOperationException) { }
            var oldGame = JsonSerializer.CreateDefault(Diagnostics.Json);
            string OldGame()
            {
                oldBuffer.Clear();
                using var output = new StringWriter(oldBuffer);
                using var writer = new JsonTextWriter(output);
                oldGame.Serialize(writer, sample);
                return oldBuffer.ToString();
            }
            var beforeGame = Measure(OldGame);
            var afterGame = Measure(() => Diagnostics.Serialize(sample));
            Check(
                afterGame.bytes < beforeGame.bytes,
                "typed snapshots reduce allocations with backend settings installed"
            );
            Console.WriteLine(
                $"Backend-default snapshots (CoreCLR, 1000): before={beforeGame.bytes}B/{beforeGame.ms:F1}ms after={afterGame.bytes}B/{afterGame.ms:F1}ms; raid timing unverified."
            );
        }
        finally
        {
            JsonConvert.DefaultSettings = originalDefaults;
        }
        Check(
            JsonConvert.SerializeObject(special, plainSettings) == Diagnostics.Serialize(special),
            "switch back to typed contracts after customized defaults"
        );
        Console.WriteLine(
            $"Typed snapshots: {dtoCount} production DTOs, {fast.WritersPrepared} prepared writers."
        );
        var repeated = DiagnosticWarmup.Prepare(
            typeof(DiagnosticWarmupTests).Assembly,
            resolver,
            false
        );
        Check(
            before == resolver.Created.Count && repeated.Contracts == report.Contracts,
            "repeated preparation reuses cached metadata"
        );
        Console.WriteLine(
            $"Diagnostic metadata: {report.Contracts} contracts, {report.Failed} unavailable, {report.Milliseconds:F1} ms at startup"
        );
        return n;
    }

    private sealed class BackendConverter(Action call) : JsonConverter
    {
        public override bool CanConvert(Type t)
        {
            call();
            return false;
        }

        public override void WriteJson(JsonWriter w, object? v, JsonSerializer s) =>
            throw new NotSupportedException();

        public override object? ReadJson(JsonReader r, Type t, object? v, JsonSerializer s) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingPayload
    {
        public string Value => throw new InvalidOperationException("fixture");
    }

    private sealed class ReentrantPayload
    {
        public string Value => Diagnostics.Serialize(new { nested = true });
    }
}
