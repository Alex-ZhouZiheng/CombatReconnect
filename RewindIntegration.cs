using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Interop;
using STS2RitsuLib.RuntimeInput;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace CombatReconnect;

public sealed class RewindSettings
{
    public string RewindHotkey { get; set; } = "F4";
    public int TurnHistoryLimit { get; set; } = 10;
    public float RewindButtonX { get; set; } = 0.05f;
    public float RewindButtonY { get; set; } = 0.70f;
    public int RewindButtonLayoutVersion { get; set; }
}

internal static class RitsuCompatibility
{
    internal static void Verify()
    {
        Assembly assembly = typeof(RitsuLibFramework).Assembly;
        bool HasType(string name) => assembly.GetType(name) != null;
        bool hasLifecycle = typeof(RitsuLibFramework).GetMethods()
            .Any(method => method.Name == nameof(RitsuLibFramework.SubscribeLifecycle));

        ReconnectLog.Write(
            "RitsuLib compatibility: " +
            $"runtimeInput={HasType("STS2RitsuLib.RuntimeInput.RuntimeHotkeyService")}, " +
            $"rightClick={HasType("STS2RitsuLib.Interactions.RightClick.ModRightClickRegistry")}, " +
            $"settings={HasType("STS2RitsuLib.Settings.ModSettingsPageBuilder")}, " +
            $"dataStore={HasType("STS2RitsuLib.Data.ModDataStore")}, " +
            $"lifecycle={hasLifecycle}, customControl={HasType("STS2RitsuLib.Settings.ModSettingsSectionBuilder")}. " +
            "Deterministic replay and network state remain owned by CombatReconnect.");
    }
}

internal static class RewindSettingsStore
{
    private const string SettingsKey = "rewind-settings";
    internal const int CurrentButtonLayoutVersion = 1;
    private static ModDataStore? _store;
    private static IRuntimeHotkeyHandle? _rewindHotkey;
    private static IRuntimeHotkeyHandle? _selfTestHotkey;

    internal static int TurnHistoryLimit => Math.Max(1, _store?.Get<RewindSettings>(SettingsKey).TurnHistoryLimit ?? 10);

    internal static Vector2 RewindButtonPosition
    {
        get
        {
            RewindSettings? settings = _store?.Get<RewindSettings>(SettingsKey);
            if (settings == null) return new Vector2(0.05f, 0.70f);
            return new Vector2(settings.RewindButtonX, settings.RewindButtonY);
        }
    }

    internal static bool RequiresButtonLayoutMigration
        => (_store?.Get<RewindSettings>(SettingsKey).RewindButtonLayoutVersion ?? 0) < CurrentButtonLayoutVersion;

    internal static void Initialize()
    {
        ModTypeDiscoveryHub.RegisterModAssembly("CombatReconnect", Assembly.GetExecutingAssembly());
        using (RitsuLibFramework.BeginModDataRegistration("CombatReconnect"))
        {
            _store = RitsuLibFramework.GetDataStore("CombatReconnect");
            _store.Register(
                SettingsKey,
                "rewind-settings.json",
                SaveScope.Global,
                () => new RewindSettings(),
                autoCreateIfMissing: true);
        }

        RitsuLibFramework.RegisterModSettings("CombatReconnect", page => page
            .WithTitle(ModSettingsText.Literal("Combat Reconnect"))
            .WithModDisplayName(ModSettingsText.Literal("Combat Reconnect"))
            .AddSection("rewind", section => section
                .WithTitle(ModSettingsText.Literal("Rewind"))
                .AddKeyBinding(
                    "rewind-hotkey",
                    ModSettingsText.Literal("Rewind current turn"),
                    new ModSettingsValueBinding<RewindSettings, string>(
                        "CombatReconnect", SettingsKey, SaveScope.Global,
                        settings => settings.RewindHotkey,
                        SetRewindHotkey))
                .AddIntSlider(
                    "turn-history-limit",
                    ModSettingsText.Literal("Turn history limit"),
                    new ModSettingsValueBinding<RewindSettings, int>(
                        "CombatReconnect", SettingsKey, SaveScope.Global,
                        settings => settings.TurnHistoryLimit,
                        (settings, value) => settings.TurnHistoryLimit = Math.Max(1, value)),
                    minValue: 1,
                    maxValue: 20)));

        RewindSettings settings = _store.Get<RewindSettings>(SettingsKey);
        RuntimeHotkeyService.Initialize();
        _rewindHotkey = RuntimeHotkeyService.Register(settings.RewindHotkey, RewindCoordinator.RequestCurrentTurn, new RuntimeHotkeyOptions
        {
            Id = "CombatReconnect.rewind-current-turn",
            DisplayName = "Rewind current turn",
            Description = "Restores the current player-turn anchor after pending actions finish.",
            Category = "Combat Reconnect",
            MarkInputHandled = true,
            SuppressWhenTextInputFocused = true,
            SuppressWhenDevConsoleVisible = true
        });
        _selfTestHotkey = RuntimeHotkeyService.Register("F6", RewindSelfTest.Request, new RuntimeHotkeyOptions
        {
            Id = "CombatReconnect.rewind-self-test",
            DisplayName = "Run local rewind self-test",
            Description = "Simulates multiplayer rewind transport, replay validation, and safety recovery in a single-player combat.",
            Category = "Combat Reconnect",
            MarkInputHandled = true,
            SuppressWhenTextInputFocused = true,
            SuppressWhenDevConsoleVisible = true
        });
        ReconnectLog.Write($"Registered rewind hotkey '{settings.RewindHotkey}'.");
        ReconnectLog.Write("Registered local rewind self-test hotkey 'F6'.");
    }

    private static void SetRewindHotkey(RewindSettings settings, string value)
    {
        settings.RewindHotkey = value;
        if (_rewindHotkey != null && !_rewindHotkey.TryRebind(value, out string error))
            ReconnectLog.Write($"Could not update rewind hotkey '{value}': {error}");
    }

    internal static void SaveRewindButtonPosition(Vector2 normalizedPosition)
    {
        if (_store == null) return;
        _store.Modify<RewindSettings>(SettingsKey, settings =>
        {
            settings.RewindButtonX = Mathf.Clamp(normalizedPosition.X, 0f, 1f);
            settings.RewindButtonY = Mathf.Clamp(normalizedPosition.Y, 0f, 1f);
            settings.RewindButtonLayoutVersion = CurrentButtonLayoutVersion;
        });
        _store.Save(SettingsKey);
    }
}

internal static class RewindCoordinator
{
    private static bool _pending;
    internal static bool IsRewinding { get; private set; }

    internal static void RequestCurrentTurn() => RequestTurn(0);

    internal static void RequestTurn(ulong epoch)
    {
        if (_pending || IsRewinding)
        {
            ReconnectLog.Write("Rewind request ignored: rewind is already in progress.");
            return;
        }
        if (!CombatManager.Instance.IsInProgress)
        {
            ReconnectLog.Write("Rewind request ignored: no combat is active.");
            return;
        }
        if (RunManager.Instance.DebugOnlyGetState()?.Players.Count > 1)
        {
            MultiplayerRewindCoordinator.RequestTurn(epoch);
            return;
        }
        if ((epoch == 0 ? TurnReplayLedger.Anchor : TurnReplayLedger.FindAnchor(epoch)) == null)
        {
            ReconnectLog.Write("Rewind request ignored: no player-turn anchor is available yet.");
            return;
        }

        _pending = true;
        _ = RestoreAtSafeBoundary(epoch);
    }

    private static async Task RestoreAtSafeBoundary(ulong epoch)
    {
        RewindSafetySnapshot? safety = null;
        bool mutationStarted = false;
        bool keepLocked = false;
        try
        {
            TurnReplayAnchor? anchor = epoch == 0 ? TurnReplayLedger.Anchor : TurnReplayLedger.FindAnchor(epoch);
            if (anchor == null || !CombatManager.Instance.IsInProgress) return;
            TurnReplayAnchor target = anchor;
            RewindReplayPlan plan = TurnReplayLedger.BuildRewindPlan(target);

            IsRewinding = true;
            RewindTransactionGate.LockInput("正在等待战斗进入安全回滚边界……");
            using var boundaryCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RewindSafety.AwaitStableBoundary("single-player pre-rewind boundary", boundaryCancellation.Token);
            safety = RewindSafety.Capture();
            ReconnectLog.Write($"Captured single-player pre-rewind safety snapshot: hash={safety.Hash}.");
            RewindTransactionGate.BeginIsolation();
            mutationStarted = true;
            RewindSafety.ClearTransientState();
            string hash = await DeterministicReplayEngine.RestoreAndReplay(
                plan.Root.CombatState,
                plan.Root.ExtendedState,
                plan.Events,
                target.ExtendedState.nextActionId,
                target.ExtendedState.nextHookId,
                target.ExtendedState.nextChecksumId,
                showReplayVisuals: true,
                expectedFinalRoundNumber: target.RoundNumber,
                expectedFinalSide: target.ExtendedState.currentSide,
                expectedPlayerTurns: target.CombatState.Players
                    .ToDictionary(player => player.playerId, player => player.turnNumber));
            if (hash != target.PayloadHash)
                throw new InvalidOperationException($"Anchor restore hash mismatch: expected {target.PayloadHash}, got {hash}.");
            RewindTransactionGate.EnsureLocked();
            if (RewindTransactionGate.ViolationReason != null)
                throw new InvalidOperationException(RewindTransactionGate.ViolationReason);

            TurnReplayLedger.TruncateAfter(target);
            ReconnectLog.Write($"Rewound to epoch={target.Epoch}, round={target.RoundNumber}.");
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Rewind failed: {ex}");
            if (mutationStarted && safety != null)
            {
                RewindUiController.ShowTransactionStatus("回滚失败，正在恢复回滚前状态……");
                var recovery = await MultiplayerRewindCoordinator.RecoverSafety(safety);
                if (recovery.Success && recovery.Hash == safety.Hash)
                {
                    ReconnectLog.Write($"Single-player rewind safety recovery passed: hash={recovery.Hash}.");
                }
                else
                {
                    keepLocked = true;
                    RewindTransactionGate.EnsureLocked();
                    RewindUiController.ShowTransactionStatus("无法恢复回滚前安全状态，战斗保持暂停。");
                    ReconnectLog.Write(
                        $"Single-player rewind safety recovery failed: success={recovery.Success}, " +
                        $"hash={recovery.Hash}, expected={safety.Hash}, detail={recovery.Detail}.");
                }
            }
        }
        finally
        {
            if (keepLocked) RewindTransactionGate.EnsureLocked();
            else RewindTransactionGate.Unlock();
            IsRewinding = false;
            _pending = false;
        }
    }
}
