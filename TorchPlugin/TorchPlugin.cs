using System;
using System.Linq;
using System.Reflection;
using MultigridProjector.Logic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Torch;
using Torch.API;
using Torch.API.Session;

namespace MultigridProjectorTorch;

/// <summary>A stand-only Torch host for the unchanged Magnetar plugin and projection engine.</summary>
public sealed class TorchPlugin : TorchPluginBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    // Keep the original plugin's fail-closed result without changing its sources or skipping its guard.
    private static readonly FieldInfo StartupFailed = typeof(ServerPlugin.Plugin)
        .GetField("failed", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(typeof(ServerPlugin.Plugin).FullName, "failed");
    private ITorchSessionManager sessions;
    private ServerPlugin.Plugin plugin;
    private MultigridProjectorSession mgpSession;
    private bool startupAttempted;
    private bool enabled;

    public override void Init(ITorchBase torch)
    {
        base.Init(torch);
        IsReloadable = false; // Game patches and the original earlyStarted flag are process-wide.
        sessions = (ITorchSessionManager)torch.Managers.GetManager(typeof(ITorchSessionManager))
            ?? throw new InvalidOperationException("Torch session manager is unavailable.");
        if (sessions.CurrentSession != null &&
            (sessions.CurrentSession.State == TorchSessionState.Loading ||
             sessions.CurrentSession.State == TorchSessionState.Loaded))
            throw new InvalidOperationException("MGP must be installed before world loading; hot loading is unsafe.");
        sessions.SessionStateChanged += OnSessionStateChanged;
        Log.Info("MGP Torch loader armed; patches are deferred until session Loading, after serializer registration.");
    }

    private void OnSessionStateChanged(ITorchSession session, TorchSessionState state)
    {
        switch (state)
        {
            case TorchSessionState.Loading:
                if (startupAttempted)
                    break;
                startupAttempted = true;
                TorchPatchBackend.Manager = (Torch.Managers.PatchManager.PatchManager)Torch.Managers.GetManager(
                    typeof(Torch.Managers.PatchManager.PatchManager))
                    ?? throw new InvalidOperationException("Torch patch manager is unavailable.");
                plugin = new ServerPlugin.Plugin();
                // Original Init calls EarlyStartup + EnsureOriginal + both patch categories.
                // Never invoke it in raw Torch Init, and never install the Magnetar preloader.
                plugin.Init(null);
                enabled = !(bool)StartupFailed.GetValue(null);
                if (enabled)
                    Log.Info("MGP original server startup completed at Torch session Loading.");
                else
                    Log.Error("MGP disabled by original startup/compatibility guard; see game log. No session will be registered.");
                break;

            case TorchSessionState.Loaded:
                if (!enabled || mgpSession != null)
                    break;
                mgpSession = new MultigridProjectorSession();
                // The core listens to OnEntityCreate. At Loaded existing PBs have already fired
                // that event; replay it once so its own one-shot Update registers the PB API.
                var existingPb = MyEntities.GetEntities().FirstOrDefault(entity => entity is IMyProgrammableBlock);
                if (existingPb != null)
                {
                    var onEntityCreate = typeof(MultigridProjectorSession).GetMethod(
                        "OnEntityCreate", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException(typeof(MultigridProjectorSession).FullName, "OnEntityCreate");
                    onEntityCreate.Invoke(mgpSession, new object[] { existingPb });
                }
                Log.Info("MGP shared session created; Torch is its only updater.");
                break;

            case TorchSessionState.Unloading:
            case TorchSessionState.Unloaded:
                DisposeSession();
                break;
        }
    }

    public override void Update()
    {
        mgpSession?.Update();
    }

    private void DisposeSession()
    {
        if (mgpSession == null)
            return;
        try
        {
            mgpSession.Dispose();
        }
        finally
        {
            mgpSession = null;
        }
        Log.Info("MGP shared session disposed.");
    }

    public override void Dispose()
    {
        if (sessions != null)
            sessions.SessionStateChanged -= OnSessionStateChanged;
        DisposeSession();
        plugin?.Dispose();
        plugin = null;
        enabled = false;
        base.Dispose();
    }
}
