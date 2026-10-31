using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MultigridProjector.Logic;
using MultigridProjector.Patches;
using MultigridProjector.Utilities;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using BuildInternalPatch = MultigridProjectorClient.Patches.MyProjectorBase_BuildInternal;

namespace MultigridProjectorTorch;

/// <summary>
/// One detour owner for the six game methods shared with Torch plugins. The patch bodies remain
/// those of the core; only the two IL transforms have native Torch representations. Not a PatchShim:
/// registration must wait for Loading, serializers and the original startup/hash guard.
/// </summary>
public static class TorchPatchBackend
{
    private static readonly Type[] PatchTypes =
    {
        typeof(BuildInternalPatch), typeof(MyGridPhysicalHierarchy_BreakLink),
        typeof(MyMechanicalConnectionBlockBase_CreateTopPartAndAttach), typeof(MyProjectorBase_Build),
        typeof(MyMechanicalConnectionBlockBase_OnBuildSuccess), typeof(MyShipToolBase_UpdateAfterSimulation10)
    };

    public static PatchManager Manager { private get; set; }
    private static PatchContext installedContext;

    public static void Install()
    {
        if (installedContext != null)
            return;
        var manager = Manager ?? throw new InvalidOperationException("Torch backend was not armed at Loading.");
        var context = manager.AcquireContext();
        try
        {
            Register(context);
            manager.Commit();
            installedContext = context;
            PluginLog.Info("Torch backend installed: 6 shared targets; no MGP Harmony detours on these methods.");
        }
        catch
        {
            manager.FreeContext(context);
            manager.Commit();
            throw;
        }
    }

    public static bool OwnsPatch(Type type) => PatchTypes.Contains(type);

    public static IEnumerable<Type> GetHarmonyPatchTypes(string category) => typeof(TorchPatchBackend).Assembly.GetTypes()
        .Where(t => !OwnsPatch(t) && t.GetCustomAttributes<HarmonyPatch>().Any())
        .Where(t => t.GetCustomAttributes<HarmonyPatchCategory>().Select(a => a.info.category).FirstOrDefault() == category);

    public static void PatchHarmony(Harmony harmony, string category)
    {
        foreach (var type in GetHarmonyPatchTypes(category))
            harmony.CreateClassProcessor(type).Patch();
    }

    public static void Register(PatchContext context)
    {
        AddPrefix(context, typeof(MyGridPhysicalHierarchy), "BreakLink", PatchTypes[1], "Prefix");
        AddPrefix(context, typeof(MyMechanicalConnectionBlockBase), "CreateTopPartAndAttach", PatchTypes[2], "Prefix");
        AddPrefix(context, typeof(MyProjectorBase), "Build", PatchTypes[3], "Prefix");
        AddPrefix(context, typeof(MyShipToolBase), "UpdateAfterSimulation10", PatchTypes[5], "UpdateAfterSimulation10Prefix");
        context.GetPattern(Target(typeof(MyProjectorBase), "BuildInternal")).Transpilers.Add(
            AccessTools.Method(typeof(TorchPatchBackend), nameof(BuildInternalTranspiler)));
        context.GetPattern(Target(typeof(MyMechanicalConnectionBlockBase), "OnBuildSuccess")).Transpilers.Add(
            AccessTools.Method(typeof(TorchPatchBackend), nameof(OnBuildSuccessTranspiler)));
    }

    private static MethodInfo Target(Type type, string name) => AccessTools.DeclaredMethod(type, name)
        ?? throw new MissingMethodException(type.FullName, name);

    private static void AddPrefix(PatchContext context, Type type, string name, Type patchType, string prefix)
    {
        context.GetPattern(Target(type, name)).Prefixes.Add(Target(patchType, prefix));
    }

    public static IEnumerable<MsilInstruction> BuildInternalTranspiler(IEnumerable<MsilInstruction> instructions)
    {
        // Torch transpilers receive ONLY the original body, not the injected prefix/suffix envelope.
        // Ret is rewritten to Torch's common return label, so trust checks and suffixes still run.
        return new[]
        {
            new MsilInstruction(OpCodes.Ldarg_0), new MsilInstruction(OpCodes.Ldarg_1),
            new MsilInstruction(OpCodes.Ldarg_2), new MsilInstruction(OpCodes.Ldarg_3),
            new MsilInstruction(OpCodes.Ldarg).InlineValue(new MsilArgument(4)),
            new MsilInstruction(OpCodes.Ldarg).InlineValue(new MsilArgument(5)),
            new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)AccessTools.Method(typeof(BuildInternalPatch), "BuildInternal")),
            new MsilInstruction(OpCodes.Ret)
        };
    }

    public static IEnumerable<MsilInstruction> OnBuildSuccessTranspiler(IEnumerable<MsilInstruction> instructions)
    {
        var il = instructions.ToList();
        // Anchor on vanilla's first conditional branch, exactly as the original core transform.
        // Accept long form too, since a preceding Torch transform can normalize branches.
        var j = il.FindIndex(i => i.OpCode == OpCodes.Brfalse_S || i.OpCode == OpCodes.Brfalse);
        if (j <= 0 || !(il[j].Operand is MsilOperandBrTarget branch))
            throw new NotSupportedException("MGP OnBuildSuccess: vanilla top-creation branch is missing.");
        il.InsertRange(j + 1, new[]
        {
            new MsilInstruction(OpCodes.Ldarg_0),
            new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)AccessTools.Method(typeof(MultigridProjection), nameof(MultigridProjection.ShouldAllowBuildingDefaultTopBlock))),
            new MsilInstruction(OpCodes.Brfalse).InlineTarget(branch.Target)
        });
        return il;
    }
}
