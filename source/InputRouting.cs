using System;
using System.Collections.Generic;
using System.Reflection;
using GameManager.OnlineBase;
using GameManager.OnlineDefault;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace WheelchairLocalCoop;

/// <summary>
/// Keeps the game's global UI maps intact while selecting an independent input
/// asset only for the duration of a registered player's native update method.
/// </summary>
internal static class InputRouting
{
    private sealed class Seat
    {
        internal Transform Root = null!;
        internal Gamepad Pad = null!;
        internal EntradasGlobais Maps = null!;
        internal bool KeyboardOnly;
        internal OnlineBasePlayerRef Actor;
    }

    private sealed class SwapState
    {
        internal EntradasGlobais Previous = null!;
        internal bool Swapped;
    }

    private static readonly Dictionary<int, Seat> Seats = new();
    private static readonly List<int> SeatOrder = new();
    private static EntradasGlobais _globalMaps;
    private static Action<string> _log = _ => { };
    private static bool _installed;
    [ThreadStatic] private static int _scopeDepth;

    internal static int Count => Seats.Count;

    internal static Gamepad PrimaryPad
    {
        get
        {
            foreach (int id in SeatOrder)
                if (Seats.TryGetValue(id, out Seat seat) && seat.Root && seat.Pad != null && seat.Pad.added)
                    return seat.Pad;
            return null;
        }
    }

    internal static void Register(Transform root, Gamepad pad)
    {
        if (pad == null || !pad.added) throw new ArgumentException("Gamepad is not connected", nameof(pad));
        RegisterCore(root, pad, false);
    }

    internal static void RegisterUnbound(Transform root) => RegisterCore(root, null, false);

    // A keyboard host also needs isolation; leaving the host unregistered would
    // let every guest gamepad continue to control it through the global asset.
    internal static void RegisterKeyboard(Transform root) => RegisterCore(root, null, true);

    private static void RegisterCore(Transform root, Gamepad pad, bool keyboardOnly)
    {
        if (!root) throw new ArgumentNullException(nameof(root));
        if (_scopeDepth != 0) throw new InvalidOperationException("Cannot register a seat inside an input scope");
        PruneDestroyedSeats();
        _globalMaps = InputGetKeys.Maps ?? throw new InvalidOperationException("Game input maps are not initialized");
        int id = root.GetInstanceID();
        Unregister(root);
        EntradasGlobais maps = new();
        try
        {
            // Exact control paths avoid sharing any Gamepad.current/global state,
            // and also avoid nullable IL2CPP value-type marshaling for asset.devices.
            RestrictBindings(maps, pad, keyboardOnly);
            Seat seat = new() { Root = root, Pad = pad, Maps = maps, KeyboardOnly = keyboardOnly,
                Actor = root.GetComponent<OnlineBasePlayerRef>() };
            SyncEnabledMaps(seat);
            if (pad != null) ValidateControls(maps, pad);
            Seats.Add(id, seat);
            SeatOrder.Add(id);
            _log($"Input seat {Seats.Count}: root={root.name}, " +
                (pad != null ? $"deviceId={pad.deviceId}, path={pad.path}" : keyboardOnly ? "keyboard/mouse" : "unbound"));
        }
        catch
        {
            maps.Disable();
            maps.Dispose();
            throw;
        }
    }

    internal static void Unregister(Transform root)
    {
        if (!root) return;
        if (_scopeDepth != 0) throw new InvalidOperationException("Cannot remove a seat inside an input scope");
        int id = root.GetInstanceID();
        if (!Seats.Remove(id, out Seat seat)) return;
        SeatOrder.Remove(id);
        seat.Maps.Disable();
        seat.Maps.Dispose();
    }

    internal static void Clear()
    {
        if (_scopeDepth != 0) throw new InvalidOperationException("Cannot clear seats inside an input scope");
        foreach (Seat seat in Seats.Values)
        {
            seat.Maps.Disable();
            seat.Maps.Dispose();
        }
        Seats.Clear();
        SeatOrder.Clear();
        _globalMaps = null;
    }

    internal static bool IsRegistered(Component component) => FindSeat(component) != null;

    internal static Gamepad GetPad(Component component)
    {
        var seat = FindSeat(component);
        return seat != null && seat.Pad != null && seat.Pad.added ? seat.Pad : null;
    }

    internal static bool IsKeyboard(Component component) => FindSeat(component)?.KeyboardOnly == true;

    internal static void PruneDestroyedSeats()
    {
        if (_scopeDepth != 0) return;
        for (int i = SeatOrder.Count - 1; i >= 0; --i)
        {
            int id = SeatOrder[i];
            if (!Seats.TryGetValue(id, out var seat)) { SeatOrder.RemoveAt(i); continue; }
            if (seat.Root) continue;
            seat.Maps.Disable();
            seat.Maps.Dispose();
            Seats.Remove(id);
            SeatOrder.RemoveAt(i);
        }
    }

    private static Seat FindSeat(Component component)
    {
        if (!component || Seats.Count == 0) return null;
        for (Transform current = component.transform; current; current = current.parent)
            if (Seats.TryGetValue(current.GetInstanceID(), out Seat seat))
                return seat;
        return null;
    }

    private static void RestrictBindings(EntradasGlobais maps, Gamepad pad, bool keyboardOnly)
    {
        var actionMaps = maps.asset.actionMaps;
        for (int m = 0; m < actionMaps.Count; m++)
        {
            InputActionMap map = actionMaps[m];
            var bindings = map.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding binding = bindings[i];
                if (binding.isComposite) continue;
                string path = binding.path ?? "";
                string replacement = pad != null && path.StartsWith("<Gamepad>/", StringComparison.OrdinalIgnoreCase)
                    ? pad.path + path.Substring("<Gamepad>".Length)
                    : "";
                if (keyboardOnly && (path.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("<Mouse>/", StringComparison.OrdinalIgnoreCase))) replacement = path;
                InputBinding inputOverride = new();
                inputOverride.overridePath = replacement;
                InputActionRebindingExtensions.ApplyBindingOverride(map, i, inputOverride);
            }
        }
    }

    private static void ValidateControls(EntradasGlobais maps, Gamepad pad)
    {
        int controls = 0;
        var actionMaps = maps.asset.actionMaps;
        for (int m = 0; m < actionMaps.Count; m++)
        {
            var actions = actionMaps[m].actions;
            for (int a = 0; a < actions.Count; a++)
            {
                var resolved = actions[a].controls;
                for (int c = 0; c < resolved.Count; c++)
                {
                    controls++;
                    if (resolved[c].device.deviceId != pad.deviceId)
                        throw new InvalidOperationException("Input isolation failed: a binding resolved to another device");
                }
            }
        }
        if (controls == 0) throw new InvalidOperationException($"No game controls resolved for gamepad {pad.path}");
    }

    private static void SyncEnabledMaps(Seat seat)
    {
        if (_globalMaps == null) return;
        var maps = seat.Maps.asset.actionMaps;
        for (int i = 0; i < maps.Count; i++)
        {
            InputActionMap map = maps[i];
            InputActionMap original = _globalMaps.asset.FindActionMap(map.name, false);
            bool enabled = original != null && original.enabled;
            // The original UI owns one player's global map. A death screen can
            // leave it disabled after a per-player reset. Follow the local actor
            // and match state so one death does not lock every controller.
            if (seat.Actor && seat.Actor.GameMode != null &&
                (map.name == "Global" || map.name == "WheelchairFreeMove" ||
                 map.name == "Action" || map.name == "Pickable"))
            {
                var mode = seat.Actor.GameMode;
                var finishActor = seat.Actor.TryCast<OnDefaultFinishLinePlayerRef>();
                enabled = mode.IsGameplayScene() && mode.GameInProgress && !mode.IsPaused &&
                    !seat.Actor.IsDead && (finishActor == null || !finishActor.LevelCompleted) &&
                    (!mode.EnableInitialCountdown || mode.RaceStarted);
            }
            enabled &= seat.KeyboardOnly || (seat.Pad != null && seat.Pad.added);
            if (enabled && !map.enabled) map.Enable();
            else if (!enabled && map.enabled) map.Disable();
        }
    }

    // Only distinct, nontrivial native methods are patched. Small generated
    // accessors and empty IL2CPP methods share machine code with unrelated types.
    internal static void Install(Harmony harmony, Action<string> log)
    {
        _log = log;
        if (_installed) return;
        var targets = new (Type Type, string Method)[]
        {
            (typeof(OnlineBaseWheelchairController), "Update"),
            (typeof(OnlineBaseWheelchairController), "FixedUpdate"),
            (typeof(OnlineBaseWheelchairController), "SkiddingMove"),
            (typeof(OnlineBaseJumpFeature), "Update"),
            (typeof(OnlineBaseJumpFeature), "JumpSetup"),
            (typeof(OnDefaultWeaponsManager), "Update"),
            (typeof(PunchSystemWeapon), "OnUpdate"),
            (typeof(RpgWeapon), "OnUpdate"),
            (typeof(StickyBombWeapon), "OnUpdate"),
        };
        var prefix = new HarmonyMethod(typeof(InputRouting), nameof(BeforePlayerMethod));
        var finalizer = new HarmonyMethod(typeof(InputRouting), nameof(AfterPlayerMethod));
        foreach (var target in targets)
        {
            MethodInfo method = AccessTools.DeclaredMethod(target.Type, target.Method)
                ?? throw new MissingMethodException(target.Type.FullName, target.Method);
            harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            _log($"Input scope patched: {target.Type.Name}.{target.Method}");
        }
        _installed = true;
    }

    private static void BeforePlayerMethod(Component __instance, out SwapState __state)
    {
        __state = null;
        Seat seat = FindSeat(__instance);
        if (seat == null) return;
        // UI map changes remain owned by the original game asset. Capture a new
        // asset after a scene reload, but never capture a nested player's maps.
        if (_scopeDepth == 0) _globalMaps = InputGetKeys.Maps;
        SyncEnabledMaps(seat);
        SwapState state = new() { Previous = InputGetKeys.Maps };
        __state = state;
        InputGetKeys.Maps = seat.Maps;
        _scopeDepth++;
        state.Swapped = true;
    }

    private static Exception AfterPlayerMethod(Exception __exception, SwapState __state)
    {
        if (__state?.Swapped == true)
        {
            InputGetKeys.Maps = __state.Previous;
            _scopeDepth--;
            __state.Swapped = false;
        }
        return __exception;
    }
}
