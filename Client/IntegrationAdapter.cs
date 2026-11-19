using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SPT.Common.Http;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly IntegrationBoot integration = new();

    private void RunIntegration(string stage, Action action) => integration.Step(stage, action);

    private static void VerifyIntegrationFiles()
    {
        VerifyFile(
            typeof(BotComponent).Assembly,
            "c8942b646463283d1247b5673d018c1d3f2edfbe754a06061cb33bd09c368489"
        );
        VerifyFile(
            typeof(ECombatDecision).Assembly,
            "cd68e6dc65508c8bbbe71103f2ae7de083c5281d6a57bc7d3cb846c133fc1f58"
        );
        NativeAdapter.ValidateContracts();
        IntegrationManifest.ValidateEnums();
        Contract(
            typeof(EnemyDecisionClass),
            "GetDecision",
            typeof(bool),
            typeof(ECombatDecision).MakeByRefType(),
            typeof(Enemy),
            typeof(EnemyList)
        );
        Contract(
            typeof(BotDecisionManager),
            "SetDecisions",
            typeof(void),
            typeof(ECombatDecision),
            typeof(ESquadDecision),
            typeof(ESelfActionType),
            typeof(Enemy)
        );
        Contract(typeof(BotDecisionManager), "ContinueMoveToCover", typeof(bool));
        foreach (Tactic tactic in Enum.GetValues(typeof(Tactic)))
            if (!Enum.IsDefined(typeof(ECombatDecision), NativeAdapter.Map(tactic)))
                throw new InvalidOperationException("Unsupported SAIN decision: " + tactic);
    }

    private static void VerifyFile(Assembly assembly, string expected)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(assembly.Location);
        var actual = BitConverter
            .ToString(sha.ComputeHash(stream))
            .Replace("-", "")
            .ToLowerInvariant();
        if (actual != expected)
            throw new InvalidOperationException(
                assembly.GetName().Name
                    + " does not match official SAIN 4.5.1. Use the official release; COMET cannot run on a modified fork or mixed DLLs."
            );
    }

    private static void Contract(Type owner, string name, Type result, params Type[] parameters)
    {
        var method = AccessTools.Method(owner, name, parameters);
        if (method == null || method.ReturnType != result || method.IsStatic)
            throw new MissingMethodException(
                owner.FullName + "." + name + " has an unsupported signature."
            );
    }

    private void VerifyInstalledIntegration()
    {
        var hooks = new List<(string Target, string Kind, string Callback)>();
        var targets = new HashSet<MethodBase>();
        var stateSlots =
            new List<(
                string Target,
                string Owner,
                string Kind,
                string Callback,
                string StateType,
                bool Writes
            )>();
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null)
                continue;
            void Add(IEnumerable<Patch> patches, string kind)
            {
                foreach (var patch in patches.Where(p => p.owner == "com.chazut.comet"))
                {
                    if (patch.PatchMethod.DeclaringType?.Assembly != typeof(Plugin).Assembly)
                        throw new InvalidOperationException("Unexpected COMET patch owner.");
                    targets.Add(method);
                    hooks.Add(
                        (
                            method.Module.ModuleVersionId + ":" + method.MetadataToken,
                            kind,
                            patch.PatchMethod.DeclaringType == typeof(Plugin)
                                ? patch.PatchMethod.Name
                                : patch.PatchMethod.DeclaringType!.FullName
                                    + "."
                                    + patch.PatchMethod.Name
                        )
                    );
                    var state = patch
                        .PatchMethod.GetParameters()
                        .FirstOrDefault(p => p.Name == "__state");
                    if (state != null)
                    {
                        var type = state.ParameterType.IsByRef
                            ? state.ParameterType.GetElementType()!
                            : state.ParameterType;
                        stateSlots.Add(
                            (
                                method.Module.ModuleVersionId + ":" + method.MetadataToken,
                                patch.PatchMethod.DeclaringType!.FullName!,
                                kind,
                                patch.PatchMethod.Name,
                                type.AssemblyQualifiedName!,
                                state.ParameterType.IsByRef
                            )
                        );
                    }
                }
            }
            Add(info.Prefixes, "prefix");
            Add(info.Postfixes, "postfix");
            Add(info.Transpilers, "transpiler");
            Add(info.Finalizers, "finalizer");
        }
        IntegrationContract.ValidateHooks(
            hooks,
            gasBridge.Ready
                ? IntegrationManifest.RequiredCallbacks.Concat(IntegrationManifest.GasCallbacks)
                : IntegrationManifest.RequiredCallbacks
        );
        IntegrationContract.ValidateStateSlots(stateSlots);
        integration.Report.Targets = targets.Count;
        integration.Report.Callbacks = hooks.Count;
    }

    private void FinishIntegration()
    {
        integration.Complete(integration.Report.Targets, integration.Report.Callbacks);
        Instance = this; // No COMET callback may act while installation is incomplete.
        Logger.LogInfo(
            "I01: "
                + integration.Report.Message
                + " "
                + integration.Report.Targets
                + " targets, "
                + integration.Report.Callbacks
                + " hooks."
        );
        PublishIntegrationReport();
    }

    private void FailIntegration(Exception error)
    {
        Instance = null;
        integration.Fail(error, () => harmony?.UnpatchSelf());
        enabled = false;
        Logger.LogError(
            "I01 blocked at "
                + integration.Report.Phase
                + ": "
                + integration.Report.Message
                + "\n"
                + error
        );
        PublishIntegrationReport();
    }

    private async void PublishIntegrationReport()
    {
        // A single startup report, never a raid/frame polling loop. A failed report cannot disable combat.
        try
        {
            var response = await RequestHandler.PostJsonAsync(
                "/comet/integration",
                JsonConvert.SerializeObject(integration.Report)
            );
            if (Newtonsoft.Json.Linq.JObject.Parse(response).Value<bool?>("ok") != true)
                throw new InvalidDataException(
                    "Server rejected the startup report; check that the client and server COMET builds match."
                );
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                "I01 startup report unavailable in the server UI; see this log. " + ex.Message
            );
        }
    }

    private void OnDisable()
    {
        if (Instance != this)
            return;
        try
        {
            EndRaid("plugin-disabled");
        }
        finally
        {
            Instance = null;
        }
    }

    private void OnEnable()
    {
        if (integration.Ready)
            Instance = this;
    }
}
