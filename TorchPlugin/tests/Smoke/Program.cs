using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Xml.Serialization;
using Mono.Cecil;

internal sealed class Proxy : RealProxy
{
    private readonly Func<IMethodCallMessage, object> run;
    public Proxy(Type type, Func<IMethodCallMessage, object> run) : base(type) { this.run = run; }
    public override IMessage Invoke(IMessage message)
    {
        var call = (IMethodCallMessage)message;
        try { return new ReturnMessage(run(call), null, 0, call.LogicalCallContext, call); }
        catch (Exception error) { return new ReturnMessage(error, call); }
    }
}

internal static class Program
{
    static int checks;
    static string output;
    static void Check(bool value, string text)
    {
        if (!value) throw new Exception("FAIL: " + text);
        checks++; Console.WriteLine("PASS " + text);
    }
    static object MakeProxy(Type t, Func<IMethodCallMessage, object> f) => new Proxy(t, f).GetTransparentProxy();
    static PropertyInfo Property(Type t, string n) => t.GetProperty(n) ?? t.GetInterfaces().Select(x => x.GetProperty(n)).First(x => x != null);
    static string Instructions(MethodDefinition method) => !method.HasBody ? "" : string.Join("\n", method.Body.Instructions.Select(i => i.ToString()));

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
    static string Body(MethodDefinition m) => !m.HasBody ? "" :
        m.Body.InitLocals + "|" + string.Join(",", m.Body.Variables.Select(v => v.VariableType.FullName)) + "|" +
        Instructions(m) + "|" + string.Join(";", m.Body.ExceptionHandlers.Select(h =>
            h.HandlerType + ":" + h.TryStart + ":" + h.TryEnd + ":" + h.HandlerStart + ":" + h.HandlerEnd + ":" + h.FilterStart + ":" + h.CatchType));
    static string GuardValue(CustomAttributeArgument arg) => arg.Value is CustomAttributeArgument[] array
        ? string.Join(",", array.Select(GuardValue)) : Convert.ToString(arg.Value);
    static string[] Guards(ModuleDefinition module) => AllTypes(module.Types)
        .SelectMany(t => t.CustomAttributes.Where(a => a.AttributeType.Name == "EnsureOriginal")
            .Select(a => t.FullName + ":" + string.Join(";", a.ConstructorArguments.Select(GuardValue))))
        .OrderBy(x => x).ToArray();
    static void CompareMagnetar(string baseline, string current)
    {
        using (var before = ModuleDefinition.ReadModule(baseline))
        using (var after = ModuleDefinition.ReadModule(current))
        {
            var oldMethods = AllTypes(before.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Body);
            var newMethods = AllTypes(after.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Body);
            var changed = oldMethods.Keys.Union(newMethods.Keys).Where(k =>
                !oldMethods.ContainsKey(k) || !newMethods.ContainsKey(k) || oldMethods[k] != newMethods[k]).ToArray();
            Check(changed.Length == 0, "Magnetar all " + oldMethods.Count + " methods (including nested types/locals/EH) match pre-backend IL: " + string.Join(", ", changed));
            Check(Guards(before).SequenceEqual(Guards(after)), "Magnetar EnsureOriginal allowlists unchanged");
        }
    }

    public static int Main(string[] args)
    {
        try { Run(args); Console.WriteLine("ALL " + checks + " CHECKS PASSED"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Run(string[] args)
    {
        output = Path.GetFullPath(args[0]);
        if (args.Length > 2) CompareMagnetar(args[1], args[2]);
        Assembly[] pluginAssemblies = null;
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            var requested = new AssemblyName(e.Name);
            var bundled = pluginAssemblies?.FirstOrDefault(a => a.GetName().Name == requested.Name &&
                a.GetName().Version.Major == requested.Version.Major && a.GetName().Version.Minor == requested.Version.Minor);
            if (bundled != null) return bundled;
            var name = requested.Name + ".dll";
            foreach (var dir in new[] { output, "C:/SE", "C:/SE/DedicatedServer64" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        var dll = Path.Combine(output, "MultigridProjector.dll");
        using (var module = ModuleDefinition.ReadModule(dll))
        using (var original = ModuleDefinition.ReadModule(args[1]))
        {
            var attrs = module.Assembly.CustomAttributes.Where(a => a.AttributeType.Name == "IgnoresAccessChecksToAttribute")
                .Select(a => (string)a.ConstructorArguments[0].Value).ToArray();
            Check(attrs.Contains("Sandbox.Game") && attrs.Contains("SpaceEngineers.Game"), "runtime access-check bypass attributes match publicized game assemblies");
            Check(!module.Types.Any(t => t.Name == "PluginSession" || t.Name == "Preloader"), "no automatic game session or Magnetar preloader in Torch DLL");
            Check(module.Types.Single(t => t.FullName == "MultigridProjectorTorch.TorchPlugin").BaseType.FullName == "Torch.TorchPluginBase", "entry point is TorchPluginBase");
            var originalPatches = original.Types.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")).Select(t => t.FullName).OrderBy(x => x).ToArray();
            var patches = module.Types.Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")).Select(t => t.FullName).OrderBy(x => x).ToArray();
            Check(originalPatches.SequenceEqual(patches), "all " + patches.Length + " original Harmony patch classes preserved");
            Check(Guards(original).SequenceEqual(Guards(module)), "Torch EnsureOriginal allowlists identical to pre-backend Magnetar");
            var changed = original.Types.Where(t => t.Name != "PluginSession" && t.Name != "Preloader" && t.Name != "<Module>")
                .SelectMany(t => t.Methods.Where(m => m.HasBody).Select(m => new { Type = t, Method = m }))
                .Where(x => module.Types.Any(t => t.FullName == x.Type.FullName))
                .Where(x => Instructions(x.Method) != Instructions(module.Types.Single(t => t.FullName == x.Type.FullName).Methods.Single(m => m.FullName == x.Method.FullName)))
                .Select(x => x.Method.FullName).ToArray();
            var expectedBackendDifferences = new[]
            {
                "System.Int32 MultigridProjector.Utilities.EnsureOriginal::HashMethodBody(System.Reflection.MethodInfo)",
                "System.Void ServerPlugin.Plugin::EarlyStartup()"
            };
            Check(changed.OrderBy(x => x).SequenceEqual(expectedBackendDifferences.OrderBy(x => x)), "only explicit Torch backend selection/vanilla-hash method IL differs: " + string.Join(", ", changed));
            Console.WriteLine("REFERENCES " + string.Join("; ", module.AssemblyReferences.Select(r => r.FullName)));
            foreach (var harmony in Directory.GetFiles("C:/SE/Plugins", "0Harmony.dll", SearchOption.AllDirectories))
                Console.WriteLine("EXISTING HARMONY " + harmony + " => " + AssemblyName.GetAssemblyName(harmony).FullName);
        }
        // Mirror installed Torch's byte-array assembly loading, with its existing Harmony 2.1 present.
        Assembly.Load(File.ReadAllBytes("C:/SE/Plugins/SentisOptimisations/0Harmony.dll"));
        pluginAssemblies = Directory.GetFiles(output, "*.dll").Select(path => Assembly.Load(File.ReadAllBytes(path))).ToArray();
        var assembly = pluginAssemblies.Single(a => a.GetName().Name == "MultigridProjector");
        BackendTests.Run(assembly, Check);
        var api = Assembly.LoadFrom("C:/SE/Torch.API.dll");
        var torchBase = api.GetType("Torch.API.ITorchBase", true);
        var pluginInterface = api.GetType("Torch.API.Plugins.ITorchPlugin", true);
        var entries = pluginAssemblies.SelectMany(a => a.GetExportedTypes()).Where(t => !t.IsAbstract && pluginInterface.IsAssignableFrom(t)).ToArray();
        Check(entries.Length == 1 && entries[0].FullName == "MultigridProjectorTorch.TorchPlugin", "Torch-style discovery of all bundled DLLs finds exactly one plugin entry");
        var manager = api.GetType("Torch.API.Session.ITorchSessionManager", true);
        var sessionInterface = api.GetType("Torch.API.Session.ITorchSession", true);
        var stateType = api.GetType("Torch.API.Session.TorchSessionState", true);
        var entry = assembly.GetType("MultigridProjectorTorch.TorchPlugin", true);
        var plugin = assembly.GetType("ServerPlugin.Plugin", true);
        var originalHarmony = plugin.GetProperty("Harmony", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Check(originalHarmony.GetType().Assembly.GetName().Version == new Version(2, 4, 2, 0), "original plugin resolves Harmony 2.4.2 with existing Harmony 2.1.1 loaded");
        var host = Activator.CreateInstance(entry);
        Delegate subscribed = null;
        object current = null;
        int adds = 0, removes = 0;
        var sessionManager = MakeProxy(manager, call =>
        {
            if (call.MethodName == "get_CurrentSession") return current;
            if (call.MethodName == "add_SessionStateChanged") { adds++; subscribed = (Delegate)call.Args[0]; return null; }
            if (call.MethodName == "remove_SessionStateChanged") { removes++; subscribed = null; return null; }
            throw new NotSupportedException(call.MethodName);
        });
        var dependencies = MakeProxy(Property(torchBase, "Managers").PropertyType, call =>
        {
            if (call.MethodName == "GetManager") return sessionManager;
            throw new NotSupportedException(call.MethodName);
        });
        var torch = MakeProxy(torchBase, call =>
        {
            if (call.MethodName == "get_Managers") return dependencies;
            throw new NotSupportedException(call.MethodName);
        });
        entry.GetMethod("Init").Invoke(host, new[] { torch });
        Check(adds == 1 && subscribed != null, "raw Init subscribes once");
        Check(!(bool)plugin.GetField("earlyStarted", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), "raw Init does NOT run original startup/patches");
        Check(!(bool)entry.GetProperty("IsReloadable").GetValue(host), "hot reload disabled");
        entry.GetMethod("Update").Invoke(host, null);
        subscribed.DynamicInvoke(null, Enum.Parse(stateType, "Unloading"));
        subscribed.DynamicInvoke(null, Enum.Parse(stateType, "Unloaded"));
        entry.GetMethod("Dispose").Invoke(host, null);
        Check(removes == 1 && subscribed == null, "unload/no-session and Dispose are safe; state event removed");
        current = MakeProxy(sessionInterface, call => call.MethodName == "get_State" ? Enum.Parse(stateType, "Loaded") : null);
        try { entry.GetMethod("Init").Invoke(Activator.CreateInstance(entry), new[] { torch }); throw new Exception("late Init unexpectedly accepted"); }
        catch (TargetInvocationException e) { Check(e.InnerException is InvalidOperationException && adds == 1, "late installation rejected before subscription or patching"); }

        var configType = assembly.GetType("ServerPlugin.PluginConfig", true);
        var config = Activator.CreateInstance(configType);
        configType.GetProperty("ProjectionPcuLimitPercent").SetValue(config, 234);
        var sdk = pluginAssemblies.Single(a => a.GetName().Name == "PluginSdk");
        var storage = sdk.GetType("PluginSdk.Config.ConfigStorage", true);
        var pathConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-plugin.cfg");
        storage.GetMethod("SaveXml").MakeGenericMethod(configType).Invoke(null, new[] { config, pathConfig });
        var loaded = storage.GetMethod("LoadXml").MakeGenericMethod(configType).Invoke(null, new object[] { pathConfig });
        Check((int)configType.GetProperty("ProjectionPcuLimitPercent").GetValue(loaded) == 234, "real PluginSdk XML save/load round trip including JSON transitive dependencies");
        using (var reader = File.OpenRead(Path.Combine(output, "manifest.xml")))
        {
            var manifest = new XmlSerializer(entry.GetProperty("Manifest").PropertyType).Deserialize(reader);
            Check(manifest != null, "manifest accepted by installed Torch PluginManifest serializer");
        }
    }
}
