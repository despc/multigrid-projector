using System;
using System.Reflection;
using System.Runtime.Serialization;
using Sandbox.Game.Entities.Blocks;

internal static class PistonTests
{
    internal static void Run(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var piston = (MyPistonBase)FormatterServices.GetUninitializedObject(typeof(MyPistonBase));
        var head = (MyPistonTop)FormatterServices.GetUninitializedObject(typeof(MyPistonTop));
        var otherHead = (MyPistonTop)FormatterServices.GetUninitializedObject(typeof(MyPistonTop));
        var top = typeof(MyMechanicalConnectionBlockBase).GetField("m_topBlock", instance);
        var stator = typeof(MyAttachableTopBlockBase).GetField("m_parentBlock", instance);
        var baseType = assembly.GetType("MultigridProjector.Logic.BaseConnection", true);
        var topType = assembly.GetType("MultigridProjector.Logic.TopConnection", true);
        var baseConnection = FormatterServices.GetUninitializedObject(baseType);
        var topConnection = FormatterServices.GetUninitializedObject(topType);
        baseType.GetField("Block").SetValue(baseConnection, piston);
        topType.GetField("Block").SetValue(topConnection, head);
        var projection = assembly.GetType("MultigridProjector.Logic.MultigridProjection", true);
        var align = projection.GetMethod("TryAlignMechanicalPart", BindingFlags.Static | BindingFlags.NonPublic);
        bool Waits(bool alignTop) => !(bool)align.Invoke(null, new[] { baseConnection, topConnection, (object)alignTop });

        foreach (var alignTop in new[] { false, true })
        {
            var side = alignTop ? "head" : "base";
            top.SetValue(piston, null);
            stator.SetValue(head, null);
            check(Waits(alignTop), side + " alignment waits for the piston head");
            top.SetValue(piston, otherHead);
            check(Waits(alignTop), side + " alignment does not move an unrelated head");
            top.SetValue(piston, head);
            check(Waits(alignTop), side + " alignment waits for the reciprocal attachment");
            stator.SetValue(head, piston);
            check(Waits(alignTop), side + " alignment waits for the piston model after attachment");
        }
    }
}
