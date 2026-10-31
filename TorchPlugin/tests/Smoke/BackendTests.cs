using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using System.Reflection;

internal static class BackendTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Vanilla(int n) => n + 1;
    public static IEnumerable<CodeInstruction> ChangeBody(IEnumerable<CodeInstruction> source) => new[]
    { new CodeInstruction(OpCodes.Ldc_I4, 42), new CodeInstruction(OpCodes.Ret) };

    public static void Run(Assembly assembly, Action<bool, string> check)
    {
        var backend = assembly.GetType("MultigridProjectorTorch.TorchPatchBackend");
        check(backend != null, "Torch backend exists (not Harmony detours on the six shared targets)");
        var context = (PatchContext)Activator.CreateInstance(typeof(PatchContext), true);
        backend.GetMethod("Register").Invoke(null, new object[] { context });
        var patterns = (System.Collections.IDictionary)typeof(PatchContext).GetField("_rewritePatterns", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
        check(patterns.Count == 6, "exactly six Torch-owned targets registered");
        foreach (System.Collections.DictionaryEntry item in patterns)
        {
            var target = (MethodInfo)item.Key;
            var pattern = (MethodRewritePattern)item.Value;
            check(pattern.Prefixes.Count + pattern.Transpilers.Count == 1, target.DeclaringType.Name + "." + target.Name + " has one MGP hook");
        }
        var harmonyTypes = backend.GetMethod("GetHarmonyPatchTypes");
        check(harmonyTypes != null, "Harmony installer has explicit backend selection");
        var selected = ((IEnumerable<Type>)harmonyTypes.Invoke(null, new object[] { null })).ToArray();
        var owned = (Func<Type, bool>)Delegate.CreateDelegate(typeof(Func<Type, bool>), backend.GetMethod("OwnsPatch"));
        var all = assembly.GetTypes().Where(t => t.GetCustomAttributes<HarmonyPatch>().Any()).ToArray();
        check(all.Count(owned) == 6 && selected.Length == all.Length - 6 && selected.All(t => !owned(t)), "Harmony scan preserves every unrelated class and excludes all six Torch classes");
        var hash = assembly.GetType("MultigridProjector.Utilities.EnsureOriginal").GetMethod("HashMethodBody", BindingFlags.Static | BindingFlags.NonPublic);
        var fixture = typeof(BackendTests).GetMethod("Vanilla");
        var vanilla = hash.Invoke(null, new object[] { fixture });
        var harmony = new Harmony("mgp.offline.vanilla-guard");
        try
        {
            harmony.Patch(fixture, transpiler: new HarmonyMethod(typeof(BackendTests).GetMethod("ChangeBody")));
            check(Vanilla(0) == 42, "guard regression fixture actually has a rewritten Harmony body");
            check(vanilla.Equals(hash.Invoke(null, new object[] { fixture })), "Torch guard hashes vanilla IL, not other installed Harmony transpilers");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        var verify = assembly.GetType("MultigridProjector.Utilities.EnsureOriginal").GetMethod("VerifyAssembly", BindingFlags.Static | BindingFlags.NonPublic);
        verify.Invoke(null, new object[] { assembly });
        check(true, "all unchanged EnsureOriginal hashes pass against installed vanilla game IL");
        // A positive compatibility test alone could miss an accidentally disabled guard.
        // Define a patch carrying a deliberately wrong hash and demand a refusal.
        var invalidAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("MgpInvalidHashFixture"), AssemblyBuilderAccess.Run);
        var invalidType = invalidAssembly.DefineDynamicModule("fixture").DefineType("InvalidHashPatch");
        invalidType.SetCustomAttribute(new CustomAttributeBuilder(typeof(HarmonyPatch).GetConstructor(new[] { typeof(Type) }), new object[] { typeof(BackendTests) }));
        invalidType.SetCustomAttribute(new CustomAttributeBuilder(typeof(HarmonyPatch).GetConstructor(new[] { typeof(string) }), new object[] { nameof(Vanilla) }));
        invalidType.SetCustomAttribute(new CustomAttributeBuilder(assembly.GetType("MultigridProjector.Utilities.EnsureOriginal").GetConstructor(new[] { typeof(string[]) }), new object[] { new[] { "00000000" } }));
        invalidType.CreateType();
        try { verify.Invoke(null, new object[] { invalidAssembly }); check(false, "wrong hash must be refused"); }
        catch (TargetInvocationException e)
        {
            check(e.InnerException is NotSupportedException && e.InnerException.Message.Contains("expected 00000000"), "EnsureOriginal rejects a deliberately wrong hash (guard not bypassed)");
        }
        NativeTests.Run(assembly, check, patterns);
    }
}
