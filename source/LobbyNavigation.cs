using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using NewUIManager;
using NewUIManager.OnlyUp;
using NewUIManager.TenLevels;
using NewUIManager.UINavigation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace WheelchairLocalCoop;

/// <summary>Recover a missing gamepad focus only inside this PC's host lobby.</summary>
internal static class LobbyNavigation
{
    private static bool installed;
    private static Action<string> log = _ => { };
    private static float nextError;
    private static int focusRepairs;

    internal static void Install(Harmony harmony, Action<string> logger)
    {
        log = logger;
        if (installed) return;
        // This distinct, parameterless native method owns mouse/keys switching.
        // Record the initial state before native code restores its old header
        // selection, then repair after its mouse/keys transition has finished.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(UINavigationModeController), "Update"),
            prefix: new HarmonyMethod(typeof(LobbyNavigation), nameof(BeforeNavigationUpdate)),
            postfix: new HarmonyMethod(typeof(LobbyNavigation), nameof(AfterNavigationUpdate)));
        installed = true;
        log("Host lobby gamepad focus recovery installed.");
    }

    private static void BeforeNavigationUpdate(UINavigationModeController __instance, out int __state)
    {
        __state = 0;
        try
        {
            if (!__instance || __instance.CurrentMode != ENavigationMode.Mouse ||
                !HasGamepadIntent() || __instance.IsEditingText) return;
            var events = EventSystem.current;
            if (!events || events.alreadySelecting || IsUsableSelection(events.currentSelectedGameObject)) return;
            var ui = UIManagerNew.instance;
            if (!TryGetHostLobby(ui, out var screen, out _, out _) || !ui.NavigationModeController ||
                __instance.Pointer != ui.NavigationModeController.Pointer) return;
            // This is the first controller intent after mouse use, with no
            // previous usable selection. A header selected by this native
            // Update is a restoration default, not a choice made by the user.
            __state = screen.GetInstanceID();
        }
        catch (Exception error) { ReportError(error); }
    }

    private static void AfterNavigationUpdate(UINavigationModeController __instance, int __state)
    {
        try
        {
            if (!HasGamepadIntent()) return;
            var ui = UIManagerNew.instance;
            if (!TryGetHostLobby(ui, out var screen, out var play, out _)) return;
            if (!__instance || !ui.NavigationModeController ||
                __instance.Pointer != ui.NavigationModeController.Pointer) return;
            var events = EventSystem.current;
            if (!events || __instance.IsEditingText || events.alreadySelecting) return;

            // Preserve an already usable selection. The one exception is the
            // native header restored during this initial mouse-to-pad Update;
            // give that otherwise empty starting point to the Play button.
            bool initialControllerFocus = __state != 0 && __state == screen.GetInstanceID();
            if (!initialControllerFocus && IsUsableSelection(events.currentSelectedGameObject)) return;
            if (screen.navigationBlocked || ui.HasVisibleOverlay() ||
                !ui.CanReceiveShortcut(play.gameObject)) return;

            // Preserve native navigation, selection visuals and A/Submit. This
            // does not invoke a button or change the chosen map/settings.
            __instance.RegisterFirstSelected(play.gameObject, 0);
            __instance.SetMode(ENavigationMode.Keys);
            // SetMode can be a no-op when already Keys. RegisterFirstSelected
            // has selected the target in that case; this fallback also covers
            // a stale EventSystem reference inside the original controller.
            if (!events.currentSelectedGameObject)
                events.SetSelectedGameObject(play.gameObject);
            focusRepairs++;
            log("Recovered host lobby gamepad focus: " + play.gameObject.name + ".");
        }
        catch (Exception error) { ReportError(error); }
    }

    private static void ReportError(Exception error)
    {
        if (Time.unscaledTime < nextError) return;
        nextError = Time.unscaledTime + 5f;
        log("Lobby gamepad focus recovery failed: " + error.Message);
    }

    private static bool HasGamepadIntent()
    {
        foreach (var pad in Gamepad.all)
        {
            if (pad == null || !pad.added || !pad.enabled) continue;
            if (pad.buttonSouth.wasPressedThisFrame ||
                pad.dpad.ReadValue().sqrMagnitude > 0.25f ||
                pad.leftStick.ReadValue().sqrMagnitude > 0.36f) return true;
        }
        return false;
    }

    private static bool IsUsableSelection(GameObject selected)
    {
        if (!selected || !selected.activeInHierarchy) return false;
        var selectable = selected.GetComponent<Selectable>();
        return selectable && selectable.isActiveAndEnabled && selectable.IsInteractable();
    }

    private static bool TryGetHostLobby(UIManagerNew ui, out BaseUIScreen screen,
        out Button play, out string reason)
    {
        screen = null;
        play = null;
        reason = "not local host lobby";
        if (Plugin.Enabled == null || !Plugin.Enabled.Value || !NetworkServer.active ||
            !NetworkClient.active || (NetworkServer.connections != null && NetworkServer.connections.Count > 1))
            return false;
        if (!ui || !ui.CurrentScreen) { reason = "UI not ready"; return false; }
        screen = ui.CurrentScreen;
        if (!screen.IsVisible || !screen.Root || !screen.Root.activeInHierarchy)
        { reason = "screen hidden"; return false; }
        var tenLevels = screen.TryCast<TenLevelsLobbyUIScreen>();
        if (tenLevels && !tenLevels.isOfflineMode && tenLevels.onlineLobby &&
            tenLevels.onlineLobby.gameObject.activeInHierarchy && tenLevels.onlineLobby.playButton)
            play = tenLevels.onlineLobby.playButton.getButton;
        else
        {
            var onlyUp = screen.TryCast<OnlyUpLobbyManagerUIScreen>();
            if (onlyUp && onlyUp.playButton) play = onlyUp.playButton.getButton;
        }
        if (!play || !play.isActiveAndEnabled || !play.gameObject.activeInHierarchy || !play.IsInteractable())
        { reason = "host play button unavailable"; return false; }
        if (screen.navigationBlocked || ui.HasVisibleOverlay() || !ui.CanReceiveShortcut(play.gameObject))
        { reason = "overlay or editing blocks lobby"; return false; }
        reason = "ready";
        return true;
    }

    /// <summary>Read-only diagnostics for the controller navigation test.</summary>
    internal static object Snapshot()
    {
        try
        {
            var ui = UIManagerNew.instance;
            bool eligible = TryGetHostLobby(ui, out var screen, out var play, out string reason);
            var events = EventSystem.current;
            var nav = ui ? ui.NavigationModeController : null;
            var selected = events ? events.currentSelectedGameObject : null;
            var module = events && events.currentInputModule
                ? events.currentInputModule.TryCast<InputSystemUIInputModule>() : null;
            var pads = new List<object>();
            foreach (var pad in Gamepad.all)
                pads.Add(new { pad.deviceId, pad.displayName, pad.added, pad.enabled,
                    south = pad.buttonSouth.isPressed, dpad = pad.dpad.ReadValue().ToString(),
                    leftStick = pad.leftStick.ReadValue().ToString() });
            return new
            {
                eligible, reason, focusRepairs,
                host = NetworkServer.active, client = NetworkClient.active,
                screen = screen ? screen.GetIl2CppType().FullName : null,
                overlay = ui && ui.HasVisibleOverlay(),
                navigationBlocked = screen && screen.navigationBlocked,
                mode = nav ? nav.CurrentMode.ToString() : null,
                lastSelected = nav && nav.lastSelected ? nav.lastSelected.name : null,
                editingText = nav && nav.IsEditingText,
                selected = selected ? selected.name : null,
                selectedUsable = IsUsableSelection(selected),
                playButton = play ? play.gameObject.name : null,
                playSelected = play && selected && play.gameObject.Pointer == selected.Pointer,
                playNavigation = play ? play.navigation.mode.ToString() : null,
                eventSystem = events ? events.gameObject.name : null,
                sendNavigationEvents = events && events.sendNavigationEvents,
                inputModule = events && events.currentInputModule ? events.currentInputModule.GetIl2CppType().FullName : null,
                move = module ? ActionInfo(module.move) : null,
                submit = module ? ActionInfo(module.submit) : null,
                leftClick = module ? ActionInfo(module.leftClick) : null,
                pads
            };
        }
        catch (Exception error) { return new { error = error.ToString() }; }
    }

    private static object ActionInfo(InputActionReference reference)
    {
        var action = reference ? reference.action : null;
        if (action == null) return null;
        var bindings = new List<string>();
        foreach (var binding in action.bindings) bindings.Add(binding.effectivePath);
        var controls = new List<string>();
        foreach (var control in action.controls) controls.Add(control.path);
        return new { action.name, action.enabled, bindings, controls };
    }
}
