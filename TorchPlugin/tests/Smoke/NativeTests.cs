using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using Torch.Managers.PatchManager.Transpile;
using VRageMath;

internal static class NativeTests
{
    private static readonly List<string> Trace = new List<string>();
    private static bool allow;
    private static long received;
    public static bool Trust(ref long builtBy) { Trace.Add("trust"); builtBy = 73; return allow; }
    public static void Suffix(bool __prefixSkipped) { Trace.Add(__prefixSkipped ? "suffix:skipped" : "suffix:ran"); }
    public static void Replacement(MyProjectorBase instance, Vector3I position, long owner, long builder, bool instant, long builtBy)
    { Trace.Add("mgp-body"); received = builtBy; }
    public static IEnumerable<MsilInstruction> ObserveBody(IEnumerable<MsilInstruction> source)
    {
        foreach (var i in source)
            yield return i.Operand is MsilOperandInline<MethodBase> op && op.Value.Name == "BuildInternal" && op.Value.DeclaringType.FullName == "MultigridProjectorClient.Patches.MyProjectorBase_BuildInternal"
                ? i.CopyWith(OpCodes.Call).InlineValue((MethodBase)typeof(NativeTests).GetMethod("Replacement")) : i;
    }

    private static object Decorated(MethodInfo method) => typeof(PatchManager).GetMethod("GetPatternInternal", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { method });
    private static DynamicMethod Compose(MethodInfo method)
    {
        var decorated = Decorated(method);
        // Run the installed Torch compiler, including AllocatePatchMethod, EmitPatched,
        // MethodTranspiler.EmitMethod and PatchUtilities.Compile; never commit a native detour.
        return (DynamicMethod)decorated.GetType().GetMethod("ComposePatchedMethod").Invoke(decorated, null);
    }

    public static bool TopTrust() => true;

    private static void CheckOnBuildSuccess(Assembly assembly, MethodInfo method, Action<bool, string> check)
    {
        var transform = assembly.GetType("MultigridProjectorTorch.TorchPatchBackend").GetMethod("OnBuildSuccessTranspiler");
        var original = PatchUtilities.ReadInstructions(method).ToList();
        var branchIndex = original.FindIndex(i => i.OpCode == OpCodes.Brfalse_S || i.OpCode == OpCodes.Brfalse);
        var target = ((MsilOperandBrTarget)original[branchIndex].Operand).Target;
        var changed = ((IEnumerable<MsilInstruction>)transform.Invoke(null, new object[] { original })).ToList();
        check(changed.Count == original.Count + 3 &&
              original.Take(branchIndex + 1).SequenceEqual(changed.Take(branchIndex + 1)) &&
              original.Skip(branchIndex + 1).SequenceEqual(changed.Skip(branchIndex + 4)),
            "OnBuildSuccess native transform preserves every vanilla instruction/label and inserts exactly three instructions");
        check(changed[branchIndex + 1].OpCode == OpCodes.Ldarg_0 &&
              changed[branchIndex + 2].OpCode == OpCodes.Call &&
              changed[branchIndex + 2].Operand is MsilOperandInline<MethodBase> predicate &&
              predicate.Value.DeclaringType == assembly.GetType("MultigridProjector.Logic.MultigridProjection") &&
              predicate.Value.Name == "ShouldAllowBuildingDefaultTopBlock" &&
              changed[branchIndex + 3].OpCode == OpCodes.Brfalse &&
              ((MsilOperandBrTarget)changed[branchIndex + 3].Operand).Target == target,
            "OnBuildSuccess native predicate/false branch targets the exact vanilla skip-top label");
        original[branchIndex] = original[branchIndex].CopyWith(OpCodes.Brfalse).InlineTarget(target);
        check(((IEnumerable<MsilInstruction>)transform.Invoke(null, new object[] { original })).Count() == original.Count + 3,
            "OnBuildSuccess accepts prior Torch branch normalization");
        try
        {
            transform.Invoke(null, new object[] { new[] { new MsilInstruction(OpCodes.Ret) } });
            check(false, "OnBuildSuccess missing anchor must fail closed");
        }
        catch (TargetInvocationException e)
        {
            check(e.InnerException is NotSupportedException, "OnBuildSuccess missing anchor fails closed, not silently suppressed");
        }
        var pattern = (MethodRewritePattern)Decorated(method);
        pattern.Prefixes.Add(typeof(NativeTests).GetMethod("TopTrust"));
        pattern.Suffixes.Add(typeof(NativeTests).GetMethod("Suffix"));
        Compose(method);
        check(true, "OnBuildSuccess real Torch compiler + CLR JIT accepts original body, MGP predicate, prefix and suffix together");
    }

    public static void Run(Assembly assembly, Action<bool, string> check, IDictionary patterns)
    {
        // Mirror Torch startup's reflected bindings without initializing the server/game.
        foreach (var type in new[] { typeof(LoggingIlGenerator), typeof(PatchUtilities),
                     typeof(PatchManager).Assembly.GetType("Torch.Managers.PatchManager.DecoratedMethod", true) })
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                Torch.Utils.ReflectedManager.Process(field);
        foreach (DictionaryEntry item in patterns)
        {
            var method = (MethodInfo)item.Key;
            Compose(method);
            check(true, "real Torch emitter + CLR JIT accepts " + method.DeclaringType.Name + "." + method.Name);
        }
        CheckOnBuildSuccess(assembly, patterns.Keys.Cast<MethodInfo>().Single(m => m.Name == "OnBuildSuccess"), check);
        var build = patterns.Keys.Cast<MethodInfo>().Single(m => m.Name == "BuildInternal");
        var pattern = (MethodRewritePattern)Decorated(build);
        pattern.Prefixes.Add(typeof(NativeTests).GetMethod("Trust"));
        pattern.Suffixes.Add(typeof(NativeTests).GetMethod("Suffix"));
        pattern.Transpilers.Add(typeof(NativeTests).GetMethod("ObserveBody"));
        var composed = Compose(build);
        allow = true; Trace.Clear(); received = -1;
        composed.Invoke(null, new object[] { null, new Vector3I(1, 2, 3), 10L, 20L, false, 30L });
        check(Trace.SequenceEqual(new[] { "trust", "mgp-body", "suffix:ran" }) && received == 73, "BuildInternal Torch trust prefix runs before replacement, ref args reach body, suffix runs once");
        allow = false; Trace.Clear(); received = -1;
        composed.Invoke(null, new object[] { null, new Vector3I(), 10L, 20L, false, 30L });
        check(Trace.SequenceEqual(new[] { "trust", "suffix:skipped" }) && received == -1, "BuildInternal rejected by Torch trust prefix skips replacement and preserves Torch suffix semantics");
    }
}
