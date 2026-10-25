using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using GameManager;
using GameManager.OnlineTenLevels;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace WheelchairLocalCoop;

/// <summary>Enabled only by -localcoop-test. No synthetic devices in normal play.</summary>
internal static class TestDriver
{
    private static readonly List<Gamepad> virtualPads = new();
    private static float nextCommand;
    private static float checkStarted = -1;
    private static int previousPhase = -1;
    private static bool checkingCamera;
    private static bool checkingZoom;
    private static bool zoomIn;
    private static float[] previousZoom=new float[4];
    private static bool checkingRespawn;
    private static bool checkingWeapon;
    private static int[] previousResets=new int[4];
    private static float[] previousYaw = new float[4];
    private static readonly List<object> results = new();
    private static int tapPlayer=-1;
    private static GamepadButton tapButton;
    private static float tapUntil;
    internal static bool UseVirtualOnly { get; private set; }
    internal static string Status { get; private set; } = "idle";
    internal static bool IsVirtual(Gamepad pad) => virtualPads.Any(p => p.deviceId == pad.deviceId);

    internal static void Tick(CoopRuntime runtime)
    {
        C4Tests.Tick();
        if (Time.unscaledTime >= nextCommand)
        {
            nextCommand = Time.unscaledTime + 0.25f;
            var path = Path.Combine(Paths.BepInExRootPath, "localcoop-command.txt");
            if (File.Exists(path))
            {
                string command=File.ReadAllText(path).Trim();
                File.Delete(path);
                try { Run(command); }
                catch (Exception ex) { Status="command failed: "+ex; Plugin.Info(Status); }
            }
        }
        if (MovementSoak.Tick())
        {
            RemoveVirtual();
            Status="movement endurance check completed; physical controllers restored";
            return;
        }
        if (virtualPads.Count == 0) return;
        if(MovementSoak.Running)
        {
            for(int i=0;i<virtualPads.Count;i++) InputSystem.QueueStateEvent(virtualPads[i],MovementSoak.StateFor(i));
            Status="movement endurance check running";
            return;
        }
        int active = -1;
        if (checkStarted >= 0)
        {
            float elapsed=Time.unscaledTime-checkStarted;
            float phaseLength=checkingWeapon ? 6f : checkingRespawn ? 1.8f : 1.2f;
            int phase=(int)(elapsed/phaseLength);
            if (phase != previousPhase && previousPhase >= 0 && previousPhase < virtualPads.Count)
                Capture(previousPhase);
            previousPhase=phase;
            if (phase >= virtualPads.Count)
            {
                checkStarted=-1;
                string file=checkingWeapon ? "localcoop-"+WeaponTests.Selected+"-results.json" : checkingZoom ? "localcoop-zoom-results.json" : checkingCamera ? "localcoop-camera-results.json" : checkingRespawn ? "localcoop-respawn-results.json" : "localcoop-input-results.json";
                Status="check completed; see "+file;
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath,file),
                    JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
                Plugin.Info(Status);
                // Give control back immediately after automated input checks.
                // Keeping idle virtual devices assigned makes real controllers
                // appear broken while global menu bindings still work.
                RemoveVirtual();
                Status += "; physical controllers restored";
                return;
            }
            else { active=phase; Status="testing input P"+(phase+1); }
        }
        for (int i=0;i<virtualPads.Count;i++)
        {
            var state=new GamepadState();
            if (i==active)
            {
                if (checkingWeapon) state=WeaponTests.Drive(i,(Time.unscaledTime-checkStarted)%6f);
                else if (checkingZoom && (Time.unscaledTime-checkStarted)%1.2f<1.05f)
                    state.buttons=1u<<(int)(zoomIn?GamepadButton.DpadUp:GamepadButton.DpadDown);
                else if (checkingRespawn)
                {
                    if((Time.unscaledTime-checkStarted)%1.8f<1.45f) state.buttons=1u<<(int)GamepadButton.North;
                }
                else if (checkingCamera) state.rightStick=new Vector2(0.7f,0);
                else { state.rightTrigger=0.75f; state.leftStick=new Vector2(0.6f,0); }
            }
            if(i==tapPlayer && Time.unscaledTime<tapUntil) state.buttons |= 1u<<(int)tapButton;
            InputSystem.QueueStateEvent(virtualPads[i],state);
        }
    }

    private static void Capture(int selected)
    {
        if (checkingWeapon) { results.Add(WeaponTests.Capture(selected)); return; }
        if (checkingZoom) { CaptureZoom(selected); return; }
        if (checkingCamera) { CaptureCamera(selected); return; }
        if (checkingRespawn) { CaptureRespawn(selected); return; }
        var values=new List<object>();
        bool pass=LocalPlayers.Players.Count==virtualPads.Count;
        for(int i=0;i<LocalPlayers.Players.Count;i++)
        {
            var actor=LocalPlayers.Players[i];
            float vertical=actor.WcController.VerticalInput;
            float horizontal=actor.WcController.HorizontalInput;
            bool correct=i==selected ? vertical>0.2f && horizontal>0.2f : Mathf.Abs(vertical)<0.05f && Mathf.Abs(horizontal)<0.05f;
            pass &= correct;
            var p=actor.transform.position;
            values.Add(new{player=i+1,vertical,horizontal,correct,position=new{x=p.x,y=p.y,z=p.z}});
        }
        results.Add(new{drivenPlayer=selected+1,pass,values});
        Plugin.Info("Input isolation P"+(selected+1)+": "+(pass?"PASS":"FAIL"));
    }

    private static float[] CameraYaw()
    {
        var yaw=new float[]{float.NaN,float.NaN,float.NaN,float.NaN};
        foreach(var cam in Camera.allCameras)
        {
            var r=cam.rect;
            if(!cam.enabled || Mathf.Abs(r.width-0.5f)>0.01f || Mathf.Abs(r.height-0.5f)>0.01f) continue;
            int index=(r.y>0.25f?0:2)+(r.x>0.25f?1:0);
            yaw[index]=cam.transform.eulerAngles.y;
        }
        return yaw;
    }
    private static void CaptureZoom(int selected)
    {
        bool pass=true;var values=new List<object>();
        for(int i=0;i<LocalPlayers.Players.Count;i++)
        {
            float distance=SharedCamera.GetZoomDistance(LocalPlayers.Players[i].transform);
            float delta=distance-previousZoom[i];
            bool correct=!float.IsNaN(delta) && (i==selected ? (zoomIn?-delta:delta)>0.5f : Mathf.Abs(delta)<0.01f);
            pass &= correct;
            values.Add(new{player=i+1,distance,delta,correct});
            previousZoom[i]=distance;
        }
        results.Add(new{drivenPlayer=selected+1,zoomIn,pass,values});
        Plugin.Info("Zoom isolation P"+(selected+1)+": "+(pass?"PASS":"FAIL"));
    }
    private static void CaptureRespawn(int selected)
    {
        bool pass=true;
        var values=new List<object>();
        for(int i=0;i<LocalPlayers.Players.Count;i++)
        {
            var actor=LocalPlayers.Players[i];
            int resets=LocalPlayers.GetResetCount(actor);
            int delta=resets-previousResets[i];
            bool correct=delta==(i==selected?1:0) && !actor.IsDead && actor.WcController.controlsEnabled;
            pass &= correct;
            values.Add(new{player=i+1,resets,delta,dead=actor.IsDead,controls=actor.WcController.controlsEnabled,correct});
            previousResets[i]=resets;
        }
        results.Add(new{drivenPlayer=selected+1,pass,values});
        Plugin.Info("Respawn isolation P"+(selected+1)+": "+(pass?"PASS":"FAIL"));
    }
    private static void CaptureCamera(int selected)
    {
        var yaw=CameraYaw();
        var values=new List<object>();
        bool pass=true;
        for(int i=0;i<virtualPads.Count;i++)
        {
            float delta=Mathf.Abs(Mathf.DeltaAngle(previousYaw[i],yaw[i]));
            bool correct=!float.IsNaN(delta) && (i==selected ? delta>10f : delta<2f);
            pass &= correct;
            values.Add(new{player=i+1,yaw=yaw[i],delta,correct});
        }
        previousYaw=yaw;
        results.Add(new{drivenPlayer=selected+1,pass,values});
        Plugin.Info("Camera isolation P"+(selected+1)+": "+(pass?"PASS":"FAIL"));
    }

    private static void Run(string command)
    {
        Plugin.Info("Test command: "+command);
        string[] words=command.Split(':');
        switch(words[0].ToLowerInvariant())
        {
            case "host":
                var lobby=UnityEngine.Object.FindObjectOfType<MySteamLobbyAPI>();
                if(!lobby) throw new InvalidOperationException("Steam lobby component not ready.");
                ModularGameManager.instance.SetGameModeEnum(EGameMode.OnlineTenLevels);
                lobby.CreateLobby("LOCAL"+DateTime.UtcNow.Ticks%100000, EGameMode.OnlineTenLevels,
                    ELobbyType.k_ELobbyTypePrivate,"Local controller test");
                Status="creating private lobby";
                break;
            case "start":
            case "level":
                var game=UnityEngine.Object.FindObjectOfType<TLOnlineGameMode>();
                if(!game || !NetworkServer.active) throw new InvalidOperationException("Host not ready.");
                int level=words.Length>1?int.Parse(words[1]):0;
                // Test scenes must retain their native pickup objects. Setup
                // destroys excluded pickups when the level starts, so set both
                // the lobby UI and network struct before changing scene.
                var settingsUi=game.LobbySync.onlineLobby.LobbySettingsUI;
                var settings=settingsUi.GetCurrentSettings();
                settings.spawnWeaponsType=NewUIManager.OnlineBase.Lobby.ESpawnWeaponsState.AllLevel;
                settingsUi.SetSettings(settings);
                game.LobbySync.NetworkcurrentLobbySettings=settings;
                game.PlayGameWithLevel(game.AvailableLevels[level]);
                Status="starting level "+level;
                break;
            case "virtualpads":
                int count=words.Length>1?Math.Clamp(int.Parse(words[1]),1,4):4;
                RemoveVirtual();
                for(int i=0;i<count;i++) virtualPads.Add(InputSystem.AddDevice<Gamepad>("LocalCoopTest"+(i+1)));
                UseVirtualOnly=true;
                Plugin.PlayerCount.Value=count;
                Status="virtual pads: "+count;
                break;
            case "realpads": RemoveVirtual(); Status="physical devices"; break;
            case "players": Plugin.PlayerCount.Value=int.Parse(words[1]); break;
            case "checkinput":
            case "checkcamera":
            case "checkzoom":
            case "checkrespawn":
            case "checkweapon":
                if(LocalPlayers.Players.Count!=virtualPads.Count || virtualPads.Count==0) throw new InvalidOperationException("Actors and test pads must be ready.");
                checkingCamera=words[0].Equals("checkcamera",StringComparison.OrdinalIgnoreCase);
                checkingZoom=words[0].Equals("checkzoom",StringComparison.OrdinalIgnoreCase);
                zoomIn=words.Length>1 && words[1].Equals("in",StringComparison.OrdinalIgnoreCase);
                checkingRespawn=words[0].Equals("checkrespawn",StringComparison.OrdinalIgnoreCase);
                checkingWeapon=words[0].Equals("checkweapon",StringComparison.OrdinalIgnoreCase);
                if(checkingWeapon) WeaponTests.Prepare(words[1]);
                if(checkingCamera) previousYaw=CameraYaw();
                if(checkingZoom) for(int i=0;i<LocalPlayers.Players.Count;i++) previousZoom[i]=SharedCamera.GetZoomDistance(LocalPlayers.Players[i].transform);
                if(checkingRespawn) for(int i=0;i<LocalPlayers.Players.Count;i++) previousResets[i]=LocalPlayers.GetResetCount(LocalPlayers.Players[i]);
                checkStarted=Time.unscaledTime; previousPhase=-1; results.Clear(); break;
            case "kill":
                LocalPlayers.Players[int.Parse(words[1])-1].PlayerDeadInvoke(GameManager.OnlineBase.ETypesOfDead.Fall); break;
            case "status": CoopRuntime.Instance.WriteStatus(); break;
            case "lobbydiag":
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath,"localcoop-lobby-status.json"),
                    JsonSerializer.Serialize(LobbyNavigation.Snapshot(),new JsonSerializerOptions{WriteIndented=true}));
                break;
            case "checkmovement":
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath,"localcoop-movement-results.json"),
                    JsonSerializer.Serialize(MovementTests.Run(),new JsonSerializerOptions{WriteIndented=true}));
                Status="movement platform fixture checks completed";
                break;
            case "soakmovement":
                if(virtualPads.Count==0 || virtualPads.Count!=LocalPlayers.Players.Count)
                    throw new InvalidOperationException("Actors and virtual controllers must be ready");
                checkStarted=-1;
                MovementSoak.Start(words.Length>1?float.Parse(words[1],System.Globalization.CultureInfo.InvariantCulture):360f);
                break;
            case "tap":
                tapPlayer=int.Parse(words[1])-1;
                if(tapPlayer<0 || tapPlayer>=virtualPads.Count) throw new ArgumentException("No test pad in that slot");
                tapButton=Enum.Parse<GamepadButton>(words[2],true);
                tapUntil=Time.unscaledTime+0.2f;
                Status="test button pulse: P"+(tapPlayer+1)+" "+tapButton;
                break;
            case "pickup": WeaponTests.Pickup(int.Parse(words[1]),words[2]); break;
            case "checkaim":
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath,"localcoop-aim-results.json"),
                    JsonSerializer.Serialize(AimTests.Run(),new JsonSerializerOptions{WriteIndented=true}));
                Status="aim geometry check completed";
                break;
            case "checkc4": C4Tests.Start(words.Length>1?words[1]:"other"); break;
            case "inspectc4":
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath,"localcoop-c4-inspection.json"),
                    JsonSerializer.Serialize(C4Tests.Inspect(),new JsonSerializerOptions{WriteIndented=true}));
                break;
            case "menu": ModularGameManager.instance.ReturnMainMenu(); break;
            case "off": Plugin.Enabled.Value=false; break;
            case "on": Plugin.Enabled.Value=true; break;
            case "quit": Application.Quit(); break;
            default: throw new ArgumentException("Unknown test command.");
        }
    }
    private static void RemoveVirtual()
    {
        WeaponTests.Cleanup();
        MovementSoak.Cancel("virtual controllers released");
        tapPlayer=-1;
        checkStarted=-1;
        foreach(var pad in virtualPads) if(pad.added) InputSystem.RemoveDevice(pad);
        virtualPads.Clear(); UseVirtualOnly=false;
    }
}
