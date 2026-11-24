using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Comet.Api
{
    // Optional consumers use reflection and Action<string>. No COMET assembly reference is required.
    public static class Decisions
    {
        public const int SchemaVersion = 1;
        internal static string RaidSession = "";
        internal static bool RaidClosed;

        public static string GetRaidSession() => RaidSession;

        private static readonly object SubscriberLock = new();
        private static Action<string>? published;
        private static Delegate[] subscribers = Array.Empty<Delegate>();
        internal static bool HasSubscribers => Volatile.Read(ref subscribers).Length != 0;

        public static event Action<string>? Published
        {
            add
            {
                lock (SubscriberLock)
                {
                    published += value;
                    Volatile.Write(
                        ref subscribers,
                        published?.GetInvocationList() ?? Array.Empty<Delegate>()
                    );
                }
            }
            remove
            {
                lock (SubscriberLock)
                {
                    published -= value;
                    Volatile.Write(
                        ref subscribers,
                        published?.GetInvocationList() ?? Array.Empty<Delegate>()
                    );
                }
            }
        }

        // Final, value-only T01-T04 pages. May arrive off-thread after raid teardown.
        // Consumers must use the supplied session, never Unity or the current raid.
        public static event Action<string>? SummaryPublished;

        internal static void PublishSummary(string json, Action<string> warning)
        {
            var handlers = SummaryPublished;
            if (handlers == null)
                return;
            foreach (Action<string> subscriber in handlers.GetInvocationList())
                try
                {
                    subscriber(json);
                }
                catch (Exception ex)
                {
                    try
                    {
                        warning("COMET summary subscriber failed: " + ex.Message);
                    }
                    catch { }
                }
        }

        private static readonly Dictionary<string, string> Current =
            new Dictionary<string, string>();
        private static readonly HashSet<Action<string>> FailedSubscribers = new();
        private static readonly Dictionary<string, string> Crossfire = new();

        public static string GetCrossfireDecision(string profileId) =>
            Crossfire.TryGetValue(profileId, out var value) ? value : "";

        internal static void SetCrossfire(string profileId, string value)
        {
            if (value.Length == 0)
                Crossfire.Remove(profileId);
            else
                Crossfire[profileId] = value;
        }

        private static readonly Dictionary<string, string> PostCombat =
            new Dictionary<string, string>();

        public static string GetPostCombatDecision(string profileId) =>
            PostCombat.TryGetValue(profileId, out var value) ? value : "";

        internal static void SetPostCombat(string profileId, string value)
        {
            if (value.Length == 0)
                PostCombat.Remove(profileId);
            else
                PostCombat[profileId] = value;
        }

        public static string GetCurrentDecision(string profileId) =>
            Current.TryGetValue(profileId, out var value) ? value : "";

        internal static void Set(string profileId, string value)
        {
            if (value.Length == 0)
                Current.Remove(profileId);
            else
                Current[profileId] = value;
        }

        private readonly struct Delivery
        {
            internal readonly Client.DiagnosticJson Json;
            internal readonly Delegate[] Handlers;
            internal readonly Action<Exception> Error;

            internal Delivery(Client.DiagnosticJson json, Delegate[] handlers, Action<Exception> error)
            {
                Json = json;
                Handlers = handlers;
                Error = error;
            }
        }

        private static readonly Comet.Core.FrameDispatchQueue<Delivery> Deliveries =
            new(Dispatch, ready: static item => item.Json.Ready);
        private static int deliveryFrame;

        // Called by RR before it closes its recording. This keeps the tail of a burst in that raid.
        public static void FlushPending() => Deliveries.Drain(deliveryFrame, true);

        internal static void Pump(int frame)
        {
            deliveryFrame = frame;
            Deliveries.Drain(frame);
        }

        internal static object DeliveryStats() =>
            new
            {
                delivered = Deliveries.Delivered,
                pending = Deliveries.Pending,
                peakPending = Deliveries.PeakPending,
                forcedDrains = Deliveries.ForcedDrains,
                overBudgetCallbacks = Deliveries.OverBudgetCallbacks,
                maxCallbackMs = Deliveries.MaxCallbackTicks
                    * 1000d
                    / System.Diagnostics.Stopwatch.Frequency,
                budgetMs = 1,
            };

        internal static void Clear()
        {
            FlushPending();
            Current.Clear();
            PostCombat.Clear();
            Crossfire.Clear();
            FailedSubscribers.Clear();
            Deliveries.ResetCounters();
        }

        internal static void Publish(string json, Action<Exception> onError)
        {
            Publish(new Client.DiagnosticJson(json), CaptureSubscribers(), onError);
        }

        internal static Delegate[] CaptureSubscribers() =>
            RaidClosed ? Array.Empty<Delegate>() : Volatile.Read(ref subscribers);

        internal static void Publish(Client.DiagnosticJson json, Delegate[] handlers, Action<Exception> onError)
        {
            if (handlers.Length != 0)
                Deliveries.Enqueue(new Delivery(json, handlers, onError), deliveryFrame);
        }

        private static void Dispatch(Delivery item)
        {
            string json;
            try { json = item.Json.Read(); }
            catch (Exception ex)
            {
                // An encoding error belongs to the event, never to a subscriber.
                try { item.Error(ex); } catch { }
                return;
            }
            foreach (Action<string> subscriber in item.Handlers)
            {
                if (FailedSubscribers.Contains(subscriber))
                    continue;
                try
                {
                    var metrics = Comet.Core.RaidSamplingMetrics.Current;
                    if (metrics == null)
                        subscriber(json);
                    else
                        metrics.InvokeMeasured(subscriber, json);
                }
                catch (Exception ex)
                {
                    FailedSubscribers.Add(subscriber);
                    try
                    {
                        item.Error(ex);
                    }
                    catch { }
                }
            }
        }
    }
}

namespace Comet.Client
{
    internal readonly struct DiagnosticJson
    {
        private readonly string? text;
        private readonly Task<string>? pending;
        internal DiagnosticJson(string text) { this.text = text; pending = null; }
        internal DiagnosticJson(Task<string> pending) { this.pending = pending; text = null; }
        internal bool Ready => pending == null || pending.IsCompleted;
        internal string Read() => text ?? pending!.GetAwaiter().GetResult();
    }
    // Newtonsoft normally compiles its member readers on first GetValue, even after
    // ResolveContract. Build those readers during pre-raid contract preparation,
    // without constructing a payload or executing any property getter.
    internal class PreparedDiagnosticResolver : CamelCasePropertyNamesContractResolver
    {
        private long contractsBuilt,
            contractBuildTicks;
        internal long ContractsBuilt => Interlocked.Read(ref contractsBuilt);
        internal double ContractBuildMilliseconds =>
            Interlocked.Read(ref contractBuildTicks)
            * 1000d
            / System.Diagnostics.Stopwatch.Frequency;

        protected override JsonContract CreateContract(Type type)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return base.CreateContract(type);
            }
            finally
            {
                Interlocked.Increment(ref contractsBuilt);
                Interlocked.Add(
                    ref contractBuildTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - started
                );
            }
        }

        internal int ReadersPrepared { get; private set; }
        internal int ReadersFallback { get; private set; }

        protected override IValueProvider CreateMemberValueProvider(MemberInfo member)
        {
            var fallback = base.CreateMemberValueProvider(member);
            try
            {
                if (member.DeclaringType == null || member.DeclaringType.ContainsGenericParameters)
                    return fallback;
                var instance = Expression.Parameter(typeof(object), "instance");
                Expression? read = null;
                if (
                    member is PropertyInfo property
                    && property.GetIndexParameters().Length == 0
                    && property.GetGetMethod(true) is MethodInfo getter
                )
                    read = Expression.Call(
                        getter.IsStatic ? null : Expression.Convert(instance, member.DeclaringType),
                        getter
                    );
                else if (member is FieldInfo field)
                    read = Expression.Field(
                        field.IsStatic ? null : Expression.Convert(instance, member.DeclaringType),
                        field
                    );
                if (read == null || read.Type.IsByRef || read.Type.IsPointer)
                    return fallback;
                var reader = Expression
                    .Lambda<Func<object, object>>(
                        Expression.Convert(read, typeof(object)),
                        instance
                    )
                    .Compile();
                // Compile prepares the dynamic body. Preparing the delegate also asks
                // the runtime to prepare its thunk; neither operation calls the getter.
                RuntimeHelpers.PrepareDelegate(reader);
                ReadersPrepared++;
                return new Reader(reader, fallback);
            }
            catch
            {
                ReadersFallback++;
                return fallback;
            }
        }

        private sealed class Reader : IValueProvider
        {
            private readonly Func<object, object> get;
            private readonly IValueProvider fallback;

            internal Reader(Func<object, object> get, IValueProvider fallback)
            {
                this.get = get;
                this.fallback = fallback;
            }

            public object GetValue(object target) => get(target);

            public void SetValue(object target, object? value) => fallback.SetValue(target, value);
        }
    }

    // Separate resolver type isolates Newtonsoft's shared contract cache. The ordinary
    // resolver remains untouched for callers using application-wide custom settings.
    internal sealed class SnapshotDiagnosticResolver : PreparedDiagnosticResolver
    {
        internal int WritersPrepared { get; private set; }

        // Raid settings are published as a validated snapshot. Live settings changes
        // replace the section object or advance the revision. Never cache bot state.
        private sealed class CachedSection
        {
            internal long Revision;
            internal string? Json;
        }

        private static readonly ConditionalWeakTable<object, CachedSection> Sections = new();

        public static void WriteConfigSection(JsonWriter writer, object? section, long revision, JsonSerializer serializer)
        {
            if (section == null || writer.Formatting != Formatting.None || revision <= 0)
            {
                serializer.Serialize(writer, section);
                return;
            }

            var entry = Sections.GetValue(section, _ => new CachedSection());
            string json;
            lock (entry)
            {
                if (entry.Json == null || entry.Revision != revision)
                {
                    using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                    using var nested = new JsonTextWriter(text);
                    serializer.Serialize(nested, section);
                    nested.Flush();
                    entry.Json = text.ToString();
                    entry.Revision = revision;
                }
                json = entry.Json;
            }
            writer.WriteRawValue(json);
        }

        protected override JsonObjectContract CreateObjectContract(Type type)
        {
            var contract = base.CreateObjectContract(type);
            if (
                type.Namespace != "Comet.Core"
                && !(
                    type.IsDefined(typeof(CompilerGeneratedAttribute), false)
                    && type.Name.Contains("AnonymousType")
                )
            )
                return contract;
            if (
                contract.Converter != null
                || contract.IsReference == true
                || contract.OnSerializingCallbacks.Count != 0
                || contract.OnSerializedCallbacks.Count != 0
                || contract.ExtensionDataGetter != null
            )
                return contract;
            try
            {
                var writer = Expression.Parameter(typeof(JsonWriter), "writer");
                var value = Expression.Parameter(typeof(object), "value");
                var serializer = Expression.Parameter(typeof(JsonSerializer), "serializer");
                var target = Expression.Variable(type, "target");
                var body = new List<Expression>
                {
                    Expression.Assign(target, Expression.Convert(value, type)),
                    Expression.Call(writer, nameof(JsonWriter.WriteStartObject), Type.EmptyTypes),
                };
                foreach (var property in contract.Properties)
                {
                    if (property.Ignored || !property.Readable)
                        continue;
                    // Custom contracts retain Newtonsoft's complete policy handling.
                    if (
                        property.Converter != null
                        || property.ShouldSerialize != null
                        || property.GetIsSpecified != null
                        || property.NullValueHandling.HasValue
                        || property.DefaultValueHandling.HasValue
                        || property.TypeNameHandling.HasValue
                        || property.IsReference.HasValue
                        || property.ReferenceLoopHandling.HasValue
                        || property.ItemConverter != null
                        || property.ItemIsReference.HasValue
                        || property.ItemReferenceLoopHandling.HasValue
                        || property.ItemTypeNameHandling.HasValue
                    )
                        return contract;
                    var member = Expression.PropertyOrField(target, property.UnderlyingName!);
                    var write = typeof(JsonWriter).GetMethod(
                        nameof(JsonWriter.WriteValue),
                        new[] { member.Type }
                    );
                    // Typed primitive writes avoid boxing and repeated serializer property
                    // dispatch. Nested objects/collections still use Newtonsoft's contracts.
                    Expression writeValue =
                        write != null
                        && member.Type != typeof(object)
                        && write.GetParameters()[0].ParameterType == member.Type
                            ? Expression.Call(writer, write, member)
                            : Expression.Call(
                                serializer,
                                nameof(JsonSerializer.Serialize),
                                Type.EmptyTypes,
                                writer,
                                Expression.Convert(member, typeof(object))
                            );
                    Expression propertyWrite = Expression.Block(
                        Expression.Call(
                            writer,
                            nameof(JsonWriter.WritePropertyName),
                            Type.EmptyTypes,
                            Expression.Constant(property.PropertyName)
                        ),
                        writeValue
                    );
                    if (!member.Type.IsValueType || Nullable.GetUnderlyingType(member.Type) != null)
                    {
                        // Read once even when filtering nulls; a getter must not be
                        // executed twice by the snapshot writer.
                        var memberValue = Expression.Variable(member.Type, "memberValue");
                        Expression writeLocal =
                            write != null
                            && member.Type != typeof(object)
                            && write.GetParameters()[0].ParameterType == member.Type
                                ? Expression.Call(writer, write, memberValue)
                                : Expression.Call(
                                    serializer,
                                    nameof(JsonSerializer.Serialize),
                                    Type.EmptyTypes,
                                    writer,
                                    Expression.Convert(memberValue, typeof(object))
                                );
                        var revision = type.GetProperty("configRevision");
                        if (revision?.PropertyType == typeof(long)
                            && type.IsDefined(typeof(CompilerGeneratedAttribute), false)
                            && property.PropertyName!.EndsWith("Config", StringComparison.Ordinal)
                            && member.Type.Namespace == "Comet.Core" && member.Type.Name.EndsWith("Config", StringComparison.Ordinal))
                            writeLocal = Expression.Call(typeof(SnapshotDiagnosticResolver), nameof(WriteConfigSection),
                                Type.EmptyTypes, writer, Expression.Convert(memberValue, typeof(object)),
                                Expression.Property(target, revision), serializer);
                        propertyWrite = Expression.Block(
                            new[] { memberValue },
                            Expression.Assign(memberValue, member),
                            Expression.IfThen(
                                Expression.OrElse(
                                    Expression.NotEqual(
                                        Expression.Property(
                                            serializer,
                                            nameof(JsonSerializer.NullValueHandling)
                                        ),
                                        Expression.Constant(NullValueHandling.Ignore)
                                    ),
                                    Expression.NotEqual(
                                        memberValue,
                                        Expression.Constant(null, member.Type)
                                    )
                                ),
                                Expression.Block(
                                    Expression.Call(
                                        writer,
                                        nameof(JsonWriter.WritePropertyName),
                                        Type.EmptyTypes,
                                        Expression.Constant(property.PropertyName)
                                    ),
                                    writeLocal
                                )
                            )
                        );
                    }
                    body.Add(propertyWrite);
                }
                body.Add(
                    Expression.Call(writer, nameof(JsonWriter.WriteEndObject), Type.EmptyTypes)
                );
                var writeObject = Expression
                    .Lambda<Action<JsonWriter, object, JsonSerializer>>(
                        Expression.Block(new[] { target }, body),
                        writer,
                        value,
                        serializer
                    )
                    .Compile();
                RuntimeHelpers.PrepareDelegate(writeObject);
                contract.Converter = new PreparedObjectWriter(writeObject);
                WritersPrepared++;
            }
            catch
            { /* Unsupported shapes keep the ordinary serializer. */
            }
            return contract;
        }

        private sealed class PreparedObjectWriter : JsonConverter
        {
            private readonly Action<JsonWriter, object, JsonSerializer> write;

            internal PreparedObjectWriter(Action<JsonWriter, object, JsonSerializer> write) =>
                this.write = write;

            public override bool CanWrite => true;
            public override bool CanRead => false;

            public override bool CanConvert(Type type) => true;

            public override void WriteJson(
                JsonWriter writer,
                object? value,
                JsonSerializer serializer
            )
            {
                if (value == null)
                    writer.WriteNull();
                else
                    write(writer, value, serializer);
            }

            public override object ReadJson(
                JsonReader reader,
                Type type,
                object? existing,
                JsonSerializer serializer
            ) => throw new NotSupportedException();
        }
    }

    // The revision on every decision resolves to one full snapshot per raid/revision.
    // Keep snapshots in both the local journal and RR's ordinary decision stream.
    internal sealed class DecisionConfiguration
    {
        private string session = "";
        private long revision;
        private bool sent;

        internal Comet.Core.CometConfig? Take(Comet.Core.CometConfig? config, string raid)
        {
            if (config == null)
                return null;
            if (sent && session == raid && revision == config.Revision)
                return null;
            sent = true;
            session = raid;
            revision = config.Revision;
            return config;
        }
    }

    internal sealed class Diagnostics : IDisposable
    {
        internal static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new PreparedDiagnosticResolver(),
        };

        [ThreadStatic]
        private static JsonSerializer? serializer;

        [ThreadStatic]
        private static string? snapshotPolicy;

        [ThreadStatic]
        private static System.Text.StringBuilder? buffer;

        [ThreadStatic]
        private static bool serializing;
        internal static readonly JsonSerializerSettings SnapshotJson = new JsonSerializerSettings
        {
            ContractResolver = new SnapshotDiagnosticResolver(),
        };

        internal static object SerializerMetrics() =>
            new
            {
                globalDefaults = JsonConvert.DefaultSettings != null,
                typedSnapshots = serializer?.ContractResolver is SnapshotDiagnosticResolver,
                snapshotCompatibility = serializer == null ? "not-initialized" : snapshotPolicy,
                snapshotContracts = (
                    (PreparedDiagnosticResolver)SnapshotJson.ContractResolver!
                ).ContractsBuilt,
                snapshotPreparationMs = (
                    (PreparedDiagnosticResolver)SnapshotJson.ContractResolver!
                ).ContractBuildMilliseconds,
                compatibilityContracts = (
                    (PreparedDiagnosticResolver)Json.ContractResolver!
                ).ContractsBuilt,
                compatibilityPreparationMs = (
                    (PreparedDiagnosticResolver)Json.ContractResolver!
                ).ContractBuildMilliseconds,
            };

        // Snapshot live values now, exactly as before. Only immutable JSON goes
        // to workers/subscribers. Reuse metadata/writer storage, never payloads.
        internal static string Serialize(object value)
        {
            if (serializing)
            {
                using var nested = new StringWriter(
                    System.Globalization.CultureInfo.InvariantCulture
                );
                JsonSerializer.Create(SnapshotJson).Serialize(nested, value);
                return nested.ToString();
            }
            serializing = true;
            var text = buffer ??= new System.Text.StringBuilder(4096);
            text.Clear();
            try
            {
                if (serializer == null)
                {
                    // Our journals have a private, stable schema. Backend/game
                    // converters must not alter it or disable prepared DTO writers.
                    serializer = JsonSerializer.Create(SnapshotJson);
                    snapshotPolicy = "isolated-diagnostic-schema";
                }
                using var output = new StringWriter(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture
                );
                using var writer = new JsonTextWriter(output);
                serializer.Serialize(writer, value);
                writer.Flush();
                return text.ToString();
            }
            catch
            {
                serializer = null;
                throw;
            }
            finally
            {
                text.Clear();
                if (text.Capacity > 65536)
                    buffer = null;
                serializing = false;
            }
        }

        private readonly struct LogEntry
        {
            internal readonly DiagnosticJson Json;
            internal readonly bool Persist;
            internal readonly bool Observe;

            internal LogEntry(DiagnosticJson json, bool persist, bool observe)
            {
                Json = json;
                Persist = persist;
                Observe = observe;
            }
        }

        private readonly BlockingCollection<LogEntry> queue = new BlockingCollection<LogEntry>(
            4096
        );
        private readonly Action<string> warn;
        private readonly Task writer;
        private long dropped;
        private int closed;
        private int completed;
        private int quiet = Comet.Core.CometConfig.DefaultQuietLogging ? 1 : 0;
        private readonly bool reportReceiverAvailable;
        private int skipOrdinaryReports;
        private long skippedBeforeQueue;
        internal Task Completion => writer;

        // Optional per-raid worker measurements. No profiler dependency in journal harnesses.
        internal Action<long, long>? PerformanceSample;
        internal Func<long>? PerformanceAllocation;
        private readonly Func<object?, string> measureEncoding;
        private long encodedEvents, encodingTicks, maxEncodingTicks;

        private string MeasureEncoding(object? snapshot)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return Serialize(snapshot!); }
            finally
            {
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                // The exclusive encoder is the only writer.
                Interlocked.Increment(ref encodedEvents);
                Interlocked.Add(ref encodingTicks, ticks);
                if (ticks > Interlocked.Read(ref maxEncodingTicks))
                    Interlocked.Exchange(ref maxEncodingTicks, ticks);
            }
        }
        private readonly DecisionConfiguration configuration = new();

        internal void ConfigureLogging(bool enabled, long revision, string session)
        {
            Volatile.Write(ref quiet, enabled ? 1 : 0);
            WriteEssential(
                Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "U01",
                        stage = "logging-configuration",
                        configRevision = revision,
                        quietLogging = enabled,
                        diagnosticMode = enabled ? "quiet" : "detailed",
                        rrTelemetry = "unchanged",
                        performanceCapture = "independent",
                    }
                )
            );
        }

        internal void ConfigureSnapshot(Comet.Core.CometConfig config, string session)
        {
            var snapshot = configuration.Take(config, session);
            if (snapshot == null)
                return;
            WriteEssential(
                Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "U01",
                        stage = "configuration",
                        configRevision = config.Revision,
                        config = snapshot,
                    }
                )
            );
        }

        internal void ConfigureRaidReports(Comet.Core.CometConfig config, bool authority, string session)
        {
            if (!reportReceiverAvailable)
                return;
            bool host = config.Enabled && authority;
            bool combat = host && config.RecordCombatJournal();
            bool weapons = host && config.RecordWeaponJournal();
            bool control = host && config.RecordControlJournal();
            bool counters = host && config.RecordTacticCounters();
            ConfigureJournal(combat, weapons, config.Revision, session);
            ConfigureControlJournal(control, config.Revision, session);
            ConfigureCounters(counters, config.TacticCounters.MaxOpportunities, config.Revision, session);
            Volatile.Write(ref skipOrdinaryReports, combat || weapons || control || counters ? 0 : 1);
        }

        internal void ConfigureJournal(
            bool combatEnabled,
            bool weaponEnabled,
            long revision,
            string session
        ) =>
            WriteEssential(
                Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "T01",
                        stage = "configuration",
                        combatEnabled,
                        weaponEnabled,
                        configRevision = revision,
                    }
                )
            );

        internal void ConfigureControlJournal(bool enabled, long revision, string session) =>
            WriteEssential(
                Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "T04",
                        stage = "configuration",
                        enabled,
                        configRevision = revision,
                    }
                )
            );

        internal void ConfigureCounters(
            bool enabled,
            int maxOpportunities,
            long revision,
            string session
        ) =>
            WriteEssential(
                Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "T02",
                        stage = "configuration",
                        enabled,
                        maxOpportunities,
                        configRevision = revision,
                    }
                )
            );

        // Keep target-selection events bounded as unrelated module configuration grows.
        internal static object ThreatConfig(Comet.Core.CometConfig c) =>
            new { c.Enabled, c.ThreatSelection };

        public Diagnostics(
            string directory,
            string session,
            Action<string> warning,
            long segmentBytes = 64L * 1024 * 1024,
            bool reportReceiverAvailable = true
        )
        {
            this.reportReceiverAvailable = reportReceiverAvailable;
            measureEncoding = MeasureEncoding;
            skipOrdinaryReports = reportReceiverAvailable ? 0 : 1;
            warn = warning;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + session + ".jsonl"
            );
            writer = Task.Run(() =>
            {
                var counters = reportReceiverAvailable ? new TacticCounters(session) : null;
                var journal = reportReceiverAvailable ? new CombatReport(session) : null;
                var eventReader = new DiagnosticEventReader();
                StreamWriter? output = null;
                long bytes = 0,
                    skipped = 0,
                    quietSuppressed = 0;
                int segment = 0;
                string SegmentPath() =>
                    segment == 0
                        ? path
                        : Path.Combine(
                            directory,
                            Path.GetFileNameWithoutExtension(path)
                                + ".part"
                                + segment.ToString("D4")
                                + ".jsonl"
                        );
                void Open()
                {
                    output = new StreamWriter(
                        new FileStream(
                            SegmentPath(),
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.Read
                        )
                    );
                    bytes = 0;
                }
                void Line(string line, bool final = false)
                {
                    if (output == null)
                    {
                        skipped++;
                        return;
                    }
                    try
                    {
                        // Only this background writer touches files. Keep persisted events;
                        // segment boundaries never reset sequence numbers or summaries.
                        if (!final && bytes >= Math.Max(128, segmentBytes))
                        {
                            output.Dispose();
                            output = null;
                            segment++;
                            Open();
                        }
                        output!.WriteLine(line);
                        bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 2;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        try
                        {
                            output?.Dispose();
                        }
                        catch { }
                        output = null;
                        Warn("COMET diagnostic file failed; counters continue: " + ex.Message);
                    }
                }
                try
                {
                    try
                    {
                        Open();
                    }
                    catch (Exception ex)
                    {
                        Warn("COMET diagnostic file unavailable; counters continue: " + ex.Message);
                    }
                    foreach (var entry in queue.GetConsumingEnumerable())
                    {
                        string json;
                        try { json = entry.Json.Read(); }
                        catch (Exception ex)
                        {
                            skipped++;
                            Warn("COMET diagnostic snapshot failed: " + ex.GetBaseException().Message);
                            continue;
                        }
                        var sample = Volatile.Read(ref PerformanceSample);
                        long start =
                            sample != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        long allocated =
                            sample != null ? (PerformanceAllocation?.Invoke() ?? 0) : 0;
                        // Enabled reports also consume events omitted from the local file.
                        if (entry.Observe && counters != null && journal != null)
                            eventReader.Observe(json, counters, journal);
                        if (entry.Persist)
                            Line(json);
                        else
                            quietSuppressed++;
                        if (queue.Count == 0 && output != null)
                            try
                            {
                                output.Flush();
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    output.Dispose();
                                }
                                catch { }
                                output = null;
                                Warn("COMET diagnostic flush failed: " + ex.Message);
                            }
                        if (sample != null)
                            try
                            {
                                sample(
                                    System.Diagnostics.Stopwatch.GetTimestamp() - start,
                                    (PerformanceAllocation?.Invoke() ?? 0) - allocated
                                );
                            }
                            catch { }
                    }
                    foreach (
                        var page in counters != null && journal != null
                            ? System.Linq.Enumerable.Concat(
                                counters.Finish(Interlocked.Read(ref dropped)),
                                journal.Finish(Interlocked.Read(ref dropped)))
                            : System.Linq.Enumerable.Empty<object>()
                    )
                    {
                        string json = JsonConvert.SerializeObject(page, Json);
                        if (json.Length > 32768)
                        {
                            Warn("COMET counter summary page exceeded 32 KiB.");
                            continue;
                        }
                        Line(json, true);
                        Comet.Api.Decisions.PublishSummary(json, Warn);
                    }
                    Line(
                        JsonConvert.SerializeObject(
                            new
                            {
                                schema = 1,
                                session,
                                stage = "diagnostics-end",
                                segments = segment + 1,
                                deferredEncoding = new
                                {
                                    measuredEvents = Interlocked.Read(ref encodedEvents),
                                    workerMs = Interlocked.Read(ref encodingTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency,
                                    maxEventMs = Interlocked.Read(ref maxEncodingTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency,
                                },
                                quietLogging = Volatile.Read(ref quiet) != 0,
                                quietSuppressed = quietSuppressed + Interlocked.Read(ref skippedBeforeQueue),
                                summaryReader = new
                                {
                                    mode = "shared-projection-v1",
                                    parsedEvents = eventReader.ParsedEvents,
                                    projectedEvents = eventReader.ProjectedEvents,
                                    skippedProperties = eventReader.SkippedProperties,
                                    legacyFallbacks = eventReader.LegacyFallbacks,
                                },
                                queueDropped = Interlocked.Read(ref dropped),
                                fileLimitDropped = 0,
                                writeFailed = skipped,
                            },
                            Json
                        ),
                        true
                    );
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref closed, 1);
                    Warn("COMET diagnostics writer failed: " + ex.Message);
                }
                finally
                {
                    try
                    {
                        output?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Warn("COMET diagnostic close failed: " + ex.Message);
                    }
                }
            });
        }

        internal bool WantsOrdinaryEvents => Volatile.Read(ref quiet) == 0 || Volatile.Read(ref skipOrdinaryReports) == 0;

        public void Write(string json) => WriteOrdinary(new DiagnosticJson(json));

        private bool WriteOrdinary(DiagnosticJson json)
        {
            bool persist = Volatile.Read(ref quiet) == 0;
            bool observe = Volatile.Read(ref skipOrdinaryReports) == 0;
            if (persist || observe)
                return Enqueue(json, persist, observe);
            Interlocked.Increment(ref skippedBeforeQueue);
            return false;
        }

        // A single encoder runs independently of disk I/O and the game thread.
        // Both consumers reserve their original FIFO position and share one result.
        private static readonly TaskScheduler SnapshotScheduler =
            new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ExclusiveScheduler;

        internal static void PublishSnapshot(Diagnostics? sink, object snapshot, Action<Exception> error)
        {
            var handlers = Api.Decisions.CaptureSubscribers();
            Func<object?, string> encode = sink != null && Volatile.Read(ref sink.PerformanceSample) != null
                ? sink.measureEncoding : static value => Serialize(value!);
            var job = new Task<string>(encode, snapshot,
                CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            var json = new DiagnosticJson(job);
            bool queued = sink?.WriteOrdinary(json) == true;
            if (!queued && handlers.Length == 0)
                return;
            job.Start(SnapshotScheduler);
            Api.Decisions.Publish(json, handlers, error);
        }

        // Configuration, errors and raid cleanup always reach the writer and the reports.
        public void WriteEssential(string json) => Enqueue(new DiagnosticJson(json), true, reportReceiverAvailable);

        private bool Enqueue(DiagnosticJson json, bool persist, bool observe)
        {
            if (Volatile.Read(ref closed) != 0)
                return false;
            // Capture the mode on enqueue, not later when the worker catches up.
            try
            {
                if (queue.TryAdd(new LogEntry(json, persist, observe)))
                    return true;
                long count = Interlocked.Increment(ref dropped);
                if (count == 1)
                    Warn("COMET diagnostics queue full. Missing events will be counted in the file footer.");
            }
            catch (InvalidOperationException)
            { /* Concurrent disposal; never break gameplay. */
            }
            return false;
        }

        private void Warn(string message)
        {
            try
            {
                warn(message);
            }
            catch { }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref closed, 1);
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;
            queue.CompleteAdding(); /* Writer drains without blocking Unity. */
        }
    }
}
