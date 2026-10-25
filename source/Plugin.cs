using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using GameManager;
using GameManager.OnlineBase;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace WheelchairLocalCoop;

[BepInPlugin("local.wheelchair.couchcoop", "Wheelchair Local Co-op", "0.3.1")]
public sealed class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<int> PlayerCount;
    internal static ConfigEntry<bool> ShowHud;
    internal static bool TestMode;
    internal static string TestProfile;
    public override void Load()
    {
        Instance = this;
        TestMode = System.Environment.GetCommandLineArgs().Contains("-localcoop-test");
        Enabled = Config.Bind("LocalCoop", "Enabled", true, "Enable host-only local split-screen multiplayer. F8 toggles this.");
        PlayerCount = Config.Bind("LocalCoop", "Players", 4, new ConfigDescription("Total local players, including the host. F7 switches 3/4.", new AcceptableValueRange<int>(2,4)));
        ShowHud = Config.Bind("LocalCoop", "ShowHud", true, "Show local player/controller status.");
        var harmony = new Harmony("local.wheelchair.couchcoop");
        if (TestMode || System.Environment.GetCommandLineArgs().Contains("-localcoop-profile"))
        {
            TestProfile = Path.Combine(Paths.BepInExRootPath, TestMode ? "test-profile" : "local-profile");
            Directory.CreateDirectory(TestProfile);
            harmony.Patch(AccessTools.PropertyGetter(typeof(Application), "persistentDataPath"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(TestSavePath)));
        }
        LocalPlayers.Install(harmony, Info);
        InputRouting.Install(harmony, Info);
        WeaponOwnership.Install(harmony, Info);
        WeaponAiming.Install(harmony);
        LobbyNavigation.Install(harmony, Info);
        WheelchairMovement.Install(harmony, Info);
        if(TestMode)
        {
            // Desktop test orchestration must not disable synthetic controllers
            // when the user switches focus to report a result in Codex.
            Application.runInBackground=true;
            InputSystem.settings.backgroundBehavior=InputSettings.BackgroundBehavior.IgnoreFocus;
            WeaponTests.Install(harmony);
            C4Tests.Install(harmony,Info);
            MovementSoak.Install(harmony);
        }
        AddComponent<CoopRuntime>();
        Info("Local co-op loaded. F8 enable/disable; F7 3/4 players. TestMode=" + TestMode);
    }
    private static bool TestSavePath(ref string __result) { __result = TestProfile; return false; }
    internal static void Info(string message) => Instance.Log.LogInfo(message);
}

public sealed class CoopRuntime : MonoBehaviour
{
    private readonly SharedCamera cameraController = new();
    private readonly List<Transform> targets = new();
    private readonly Dictionary<int, (int deviceId, bool keyboard)> bound = new();
    private readonly Dictionary<int, (Behaviour controller, bool enabled)> originalControllers = new();
    private readonly int[] deviceSlots = { -1, -1, -1, -1 };
    private float nextStatus;
    private float nextError;
    private bool wasActive;
    private bool showPanel = true;
    private string message = "Create a private room, then start a level.";
    internal static CoopRuntime Instance;
    public CoopRuntime(IntPtr ptr) : base(ptr) { }
    public void Awake() { Instance = this; }

    public void Update()
    {
        try
        {
            if (Keyboard.current != null)
            {
                if (Keyboard.current.f8Key.wasPressedThisFrame) Plugin.Enabled.Value = !Plugin.Enabled.Value;
                if (Keyboard.current.f7Key.wasPressedThisFrame) Plugin.PlayerCount.Value = Plugin.PlayerCount.Value == 4 ? 3 : 4;
                if (Keyboard.current.f9Key.wasPressedThisFrame) showPanel = !showPanel;
            }
            if (Plugin.TestMode) TestDriver.Tick(this);
            bool external = NetworkServer.active && NetworkServer.connections != null && NetworkServer.connections.Count > 1;
            if (!Plugin.Enabled.Value || external)
            {
                ResetSession();
                message = external ? "Local co-op paused: another computer is connected." : "Local co-op OFF (F8 to enable).";
            }
            else
            {
                LocalPlayers.Tick(Plugin.PlayerCount.Value);
                if (LocalPlayers.Players.Count == 0)
                {
                    if (wasActive) ResetPresentation();
                    message = NetworkClient.active && !NetworkServer.active
                        ? "Local co-op requires this PC to host the room."
                        : "Create a private room, then start a level.";
                }
                else
                {
                    wasActive = true;
                    BindControllers();
                    message = "Split-screen players: " + LocalPlayers.Players.Count + "/" + Plugin.PlayerCount.Value;
                }
            }
            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 1f;
                if (Plugin.TestMode) WriteStatus();
            }
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= nextError) { nextError = Time.unscaledTime + 3; Plugin.Info("Runtime error: " + ex); }
        }
    }

    [HideFromIl2Cpp]
    private void BindControllers()
    {
        var liveRoots=new HashSet<int>();
        foreach(var actor in LocalPlayers.Players) if(actor) liveRoots.Add(actor.transform.GetInstanceID());
        foreach(var id in bound.Keys.Where(id => !liveRoots.Contains(id)).ToArray()) bound.Remove(id);
        foreach(var id in originalControllers.Keys.Where(id => !originalControllers[id].controller).ToArray())
            originalControllers.Remove(id);
        var pads = new List<Gamepad>();
        foreach (var pad in Gamepad.all) if (pad.added && pad.enabled) pads.Add(pad);
        if (Plugin.TestMode && TestDriver.UseVirtualOnly) pads.RemoveAll(p => !TestDriver.IsVirtual(p));
        // Preserve occupied slots when devices are unplugged, instead of reordering the survivors.
        for (int i = 0; i < deviceSlots.Length; i++)
            if (!pads.Any(p => p.deviceId == deviceSlots[i])) deviceSlots[i] = -1;
        foreach (var pad in pads)
        {
            if (Array.IndexOf(deviceSlots, pad.deviceId) >= 0) continue;
            int slot = Array.IndexOf(deviceSlots, -1);
            if (slot >= 0) deviceSlots[slot] = pad.deviceId;
        }
        for (int i = 0; i < LocalPlayers.Players.Count; i++)
        {
            var actor = LocalPlayers.Players[i];
            if (!actor) continue;
            int id = actor.transform.GetInstanceID();
            var pad = pads.FirstOrDefault(p => p.deviceId == deviceSlots[i]);
            int device = pad == null ? -1 : pad.deviceId;
            bool keyboard = i == 0 && pad == null;
            if (!bound.TryGetValue(id, out var previous) || previous != (device, keyboard))
            {
                if (pad != null) InputRouting.Register(actor.transform, pad);
                else if (keyboard) InputRouting.RegisterKeyboard(actor.transform);
                else InputRouting.RegisterUnbound(actor.transform);
                bound[id] = (device, keyboard);
                Plugin.Info("P" + (i+1) + " input: " + (pad == null ? (keyboard ? "keyboard" : "waiting for controller") : pad.displayName + " #" + device));
            }
        }
    }

    public void LateUpdate()
    {
        try
        {
            if (!LocalPlayers.Active || !Plugin.Enabled.Value) return;
            targets.Clear();
            foreach (var actor in LocalPlayers.Players)
            {
                if (!actor) { targets.Add(null); continue; }
                targets.Add(actor.transform);
                var controller = actor.CamController;
                if (controller)
                {
                    int id = controller.GetInstanceID();
                    if (!originalControllers.ContainsKey(id)) originalControllers[id] = (controller, controller.enabled);
                    controller.enabled = false;
                }
            }
            var lead = LocalPlayers.Players[0];
            var camera = lead.CamController ? lead.CamController.MainCamera : Camera.main;
            if (camera) cameraController.Tick(targets, camera);
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= nextError) { nextError = Time.unscaledTime+3; Plugin.Info("Camera error: " + ex); }
        }
    }

    [HideFromIl2Cpp]
    private void ResetPresentation()
    {
        cameraController.Reset();
        foreach (var entry in originalControllers.Values) if (entry.controller) entry.controller.enabled = entry.enabled;
        originalControllers.Clear();
        InputRouting.Clear();
        bound.Clear();
        wasActive = false;
    }
    [HideFromIl2Cpp]
    private void ResetSession() { ResetPresentation(); LocalPlayers.Clear(); }
    public void OnDestroy() { ResetSession(); }

    public void OnGUI()
    {
        if (!Plugin.ShowHud.Value || !showPanel)
        {
            if(LocalPlayers.Active)
                for(int i=0;i<LocalPlayers.Players.Count;i++)
                    if(LocalPlayers.Players[i]) WeaponHud.Draw(i,LocalPlayers.Players[i],false);
            return;
        }
        if (LocalPlayers.Active)
        {
            for (int i=0; i<4; i++)
            {
                float x=(i%2)*Screen.width*0.5f;
                float y=(i/2)*Screen.height*0.5f;
                string label=i<LocalPlayers.Players.Count
                    ? "P"+(i+1)+(InputRouting.GetPad(LocalPlayers.Players[i].transform)==null ? " - connect controller" : " - Xbox / Gamepad")
                    : "Unused - F7: 3/4 players";
                if(i<LocalPlayers.Players.Count)
                {
                    float progress=LocalPlayers.GetRespawnHoldProgress(LocalPlayers.Players[i]);
                    if(progress>0) label="P"+(i+1)+" Respawn: "+Mathf.RoundToInt(progress*100)+"%";
                }
                GUI.Box(new Rect(x+12,y+12,250,28),label);
                if(i<LocalPlayers.Players.Count && LocalPlayers.Players[i])
                    WeaponHud.Draw(i,LocalPlayers.Players[i]);
            }
            GUI.Box(new Rect(Screen.width*0.5f-1,0,2,Screen.height), "");
            GUI.Box(new Rect(0,Screen.height*0.5f-1,Screen.width,2), "");
        }
        if(!LocalPlayers.Active)
        {
            GUI.Box(new Rect(12,Screen.height-60,480,50), "Local Co-op 0.3 | F7: 3/4 players | F8: on/off | F9: hide");
            GUI.Label(new Rect(24,Screen.height-35,460,24), message+" | Connected pads: "+Gamepad.all.Count);
        }
    }

    [HideFromIl2Cpp]
    internal void WriteStatus()
    {
        var pads = new List<object>();
        foreach (var pad in Gamepad.all) pads.Add(new { id=pad.deviceId, name=pad.displayName, path=pad.path, layout=pad.layout, virtualPad=TestDriver.IsVirtual(pad) });
        var actors = new List<object>();
        foreach (var actor in LocalPlayers.Players)
        {
            if (!actor) continue;
            var p=actor.transform.position;
            actors.Add(new { name=actor.name, netId=actor.netId, local=actor.isLocalPlayer, owned=actor.isOwned,
                dead=actor.IsDead, x=p.x,y=p.y,z=p.z, vertical=actor.WcController ? actor.WcController.VerticalInput : 0,
                horizontal=actor.WcController ? actor.WcController.HorizontalInput : 0,
                braking=actor.WcController && actor.WcController.IsBrakingInput,
                controlsEnabled=actor.WcController && actor.WcController.controlsEnabled,
                paused=actor.GameMode && actor.GameMode.IsPaused,
                raceStarted=actor.GameMode && actor.GameMode.RaceStarted,
                resets=LocalPlayers.GetResetCount(actor),
                device=InputRouting.GetPad(actor.transform)?.deviceId ?? -1,
                weapons=WeaponTests.Inspect(actor) });
        }
        var cameras = new List<object>();
        foreach (var cam in Camera.allCameras)
        {
            var rect=cam.rect;
            var p=cam.transform.position;
            cameras.Add(new {name=cam.name,enabled=cam.enabled,rect=new{x=rect.x,y=rect.y,w=rect.width,h=rect.height},
                position=new{x=p.x,y=p.y,z=p.z},yaw=cam.transform.eulerAngles.y,depth=cam.depth});
        }
        File.WriteAllText(Path.Combine(Paths.BepInExRootPath,"localcoop-status.json"), JsonSerializer.Serialize(new {
            time=Time.unscaledTime, scene=SceneManager.GetActiveScene().name, enabled=Plugin.Enabled.Value,
            desired=Plugin.PlayerCount.Value, server=NetworkServer.active, client=NetworkClient.active,
            connectionCount=NetworkServer.connections?.Count ?? 0, message, pads, actors, cameras,
            persistentDataPath=Application.persistentDataPath, test=TestDriver.Status
        }, new JsonSerializerOptions { WriteIndented=true }));
    }
}
