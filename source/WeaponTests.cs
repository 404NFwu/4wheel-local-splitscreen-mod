using System;
using System.Collections.Generic;
using System.Linq;
using GameManager.OnlineBase;
using GameManager.OnlineDefault;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;

namespace WheelchairLocalCoop;

// Integration diagnostics only, enabled by the explicit test command-line flag.
internal static class WeaponTests
{
    internal sealed class ProjectileObservation
    {
        public uint netId { get; set; }
        public uint ownerNetId { get; set; }
        public bool ownerMatchesCommand { get; set; }
        public float[] creationPosition { get; set; }
        public float[] creationForward { get; set; }
        public bool velocityObserved { get; set; }
        public float[] velocity { get; set; }
        public string velocitySource { get; set; }
        public float velocitySampleDelay { get; set; }
        public float velocityCameraForwardDot { get; set; }
        public bool explosionSuppressed { get; set; }
        internal NetworkBehaviour Component;
        internal Rigidbody Body;
        internal Vector3 CameraForward;
        internal float CreatedAt;
    }

    internal sealed class FireObservation
    {
        public uint ownerNetId { get; set; }
        public string weapon { get; set; }
        public int frame { get; set; }
        public float time { get; set; }
        public bool centered { get; set; }
        public float pixelError { get; set; }
        public bool targetInsideOwnCollider { get; set; }
        public float cameraDepth { get; set; }
        public float targetDistanceFromSpawn { get; set; }
        public float targetDistanceFromActor { get; set; }
        public float targetDistanceFromCamera { get; set; }
        public float spawnToTargetCameraForwardDot { get; set; }
        public object geometry { get; set; }
        public object centerRay { get; set; }
        public string error { get; set; }
        public List<ProjectileObservation> projectiles { get; set; } = new();
        internal Vector3 CameraForward;
    }

    private sealed class CommandScope
    {
        internal FireObservation Previous;
        internal FireObservation Current;
    }

    private static readonly Dictionary<(uint, WeaponsCollectable),int> shots=new();
    private static readonly Dictionary<(uint, WeaponsCollectable),List<FireObservation>> commands=new();
    private static readonly Dictionary<IntPtr, ProjectileObservation> observedProjectiles=new();
    private static readonly Dictionary<IntPtr, NetworkBehaviour> fixtureProjectiles=new();
    [ThreadStatic] private static FireObservation currentCommand;
    private static int[] previousAmmo, previousShots, previousIndices, previousResets, previousCommands;
    private static bool fixtureActive;
    private static float fixtureExpires;
    private static bool installed;
    internal static WeaponsCollectable Selected;

    internal static void Install(Harmony harmony)
    {
        if(!Plugin.TestMode || installed) return;
        harmony.Patch(AccessTools.Method(typeof(RpgProjectile),"ServerInitialize"),
            postfix:new HarmonyMethod(typeof(WeaponTests),nameof(RocketCreated)));
        harmony.Patch(AccessTools.Method(typeof(StickyBombProjectile),"ServerInitialize"),
            postfix:new HarmonyMethod(typeof(WeaponTests),nameof(BombCreated)));
        var prefix=new HarmonyMethod(typeof(WeaponTests),nameof(BeforeSpawnCommand));
        var finalizer=new HarmonyMethod(typeof(WeaponTests),nameof(AfterSpawnCommand));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(RpgWeapon),"UserCode_CmdSpawnRocket__Vector3__Vector3__Quaternion"),
            prefix:prefix,finalizer:finalizer);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(StickyBombWeapon),"UserCode_CmdSpawnBomb__Vector3__Vector3__Quaternion"),
            prefix:prefix,finalizer:finalizer);
        foreach(var type in new[]{typeof(RpgProjectile),typeof(StickyBombProjectile)})
        {
            harmony.Patch(AccessTools.DeclaredMethod(type,"Explode"),
                prefix:new HarmonyMethod(typeof(WeaponTests),nameof(BeforeFixtureExplosion)));
            harmony.Patch(AccessTools.DeclaredMethod(type,"OnCollisionEnter"),
                prefix:new HarmonyMethod(typeof(WeaponTests),nameof(BeforeProjectileCollision)));
        }
        harmony.Patch(AccessTools.DeclaredMethod(typeof(OnDefaultWeaponsManager),"Update"),
            postfix:new HarmonyMethod(typeof(WeaponTests),nameof(ObserveProjectiles)));
        installed=true;
    }
    private static void RocketCreated(RpgProjectile __instance,NetworkIdentity ownerIdentity)
    {
        CountShot(ownerIdentity,WeaponsCollectable.Rpg);
        RegisterProjectile(__instance,__instance.Rigidbody,ownerIdentity);
    }
    private static void BombCreated(StickyBombProjectile __instance,NetworkIdentity ownerIdentity)
    {
        CountShot(ownerIdentity,WeaponsCollectable.StickyBomb);
        RegisterProjectile(__instance,__instance.Rigidbody,ownerIdentity);
    }

    private static float[] V(Vector3 value) => new[]{value.x,value.y,value.z};

    private static void BeforeSpawnCommand(WeaponBase __instance,Vector3 targetPoint,Vector3 spawnPos,
        Quaternion spawnRot,out CommandScope __state)
    {
        __state=new CommandScope{Previous=currentCommand};
        var actor=__instance.playerRef;
        if(!Plugin.TestMode || !actor || !LocalPlayers.Players.Any(p=>p && p.Pointer==actor.Pointer)) return;
        var sample=new FireObservation {ownerNetId=actor.netId,weapon=__instance.WeaponType.ToString(),
            frame=Time.frameCount,time=Time.unscaledTime};
        __state.Current=sample;
        currentCommand=sample;
        var key=(actor.netId,__instance.WeaponType);
        if(!commands.TryGetValue(key,out var list)) commands[key]=list=new List<FireObservation>();
        list.Add(sample);
        try
        {
            var camera=actor.CamController.mainCamera;
            if(!camera) {sample.error="actor has no camera";return;}
            Vector3 viewport=camera.WorldToViewportPoint(targetPoint);
            // WorldToViewportPoint is camera-local; temporary aim-scope rect
            // translation cannot make a global-screen target appear centered.
            sample.pixelError=new Vector2((viewport.x-0.5f)*camera.pixelWidth,
                (viewport.y-0.5f)*camera.pixelHeight).magnitude;
            sample.cameraDepth=viewport.z;
            sample.centered=viewport.z>0 && sample.pixelError<3f;
            sample.targetDistanceFromSpawn=Vector3.Distance(targetPoint,spawnPos);
            sample.targetDistanceFromActor=Vector3.Distance(targetPoint,actor.transform.position);
            sample.targetDistanceFromCamera=Vector3.Distance(targetPoint,camera.transform.position);
            sample.CameraForward=camera.transform.forward;
            sample.spawnToTargetCameraForwardDot=Vector3.Dot((targetPoint-spawnPos).normalized,sample.CameraForward);
            var selfColliders=new List<string>();
            foreach(var collider in actor.GetComponentsInChildren<Collider>(true))
            {
                if(!collider || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
                // WheelCollider does not implement the generic ClosestPoint
                // query and can return the input even 100 metres away. Reject
                // points outside the collider bounds before any shape query.
                var bounds=collider.bounds;
                var offset=targetPoint-bounds.center;
                var extents=bounds.extents;
                if(Mathf.Abs(offset.x)>extents.x+0.025f || Mathf.Abs(offset.y)>extents.y+0.025f || Mathf.Abs(offset.z)>extents.z+0.025f) continue;
                if(collider.TryCast<WheelCollider>()!=null || Vector3.Distance(collider.ClosestPoint(targetPoint),targetPoint)<0.025f)
                    selfColliders.Add(TransformPath(collider.transform));
            }
            sample.targetInsideOwnCollider=selfColliders.Count>0;
            sample.geometry=new {targetWorld=V(targetPoint),spawnWorld=V(spawnPos),
                spawnRotation=new[]{spawnRot.x,spawnRot.y,spawnRot.z,spawnRot.w},
                actorWorld=V(actor.transform.position),cameraWorld=V(camera.transform.position),
                cameraForward=V(sample.CameraForward),viewport=V(viewport),
                pixelWidth=camera.pixelWidth,pixelHeight=camera.pixelHeight,
                activeAimScopes=WeaponAiming.ActiveScopes,selfColliders};
            var ray=camera.ViewportPointToRay(new Vector3(0.5f,0.5f,0f));
            var hits=Physics.RaycastAll(ray.origin,ray.direction,100f,-5,QueryTriggerInteraction.UseGlobal)
                .OrderBy(h=>h.distance).Take(12);
            var hitDetails=new List<object>();
            foreach(var hit in hits)
            {
                var collider=hit.collider;
                var hitActor=collider?collider.GetComponentInParent<OnlineBasePlayerRef>():null;
                hitDetails.Add(new {distance=hit.distance,point=V(hit.point),
                    distanceFromActualTarget=Vector3.Distance(hit.point,targetPoint),
                    collider=collider?TransformPath(collider.transform):"missing",
                    layer=collider?collider.gameObject.layer:-1,isTrigger=collider && collider.isTrigger,
                    actorNetId=hitActor?hitActor.netId:0,isSelf=hitActor && hitActor.netId==actor.netId});
            }
            sample.centerRay=new {origin=V(ray.origin),direction=V(ray.direction),mask=-5,hits=hitDetails};
        }
        catch(Exception error) {sample.error=error.GetType().Name+": "+error.Message;}
    }

    private static Exception AfterSpawnCommand(Exception __exception,CommandScope __state)
    {
        if(__state!=null)
        {
            if(__state.Current!=null && __exception!=null) __state.Current.error=__exception.ToString();
            if(__state.Current!=null)
                foreach(var projectile in __state.Current.projectiles) SampleProjectile(projectile,"command-return");
            currentCommand=__state.Previous;
        }
        return __exception;
    }

    private static string TransformPath(Transform value)
    {
        var names=new List<string>();
        for(int i=0;value && i<12;i++,value=value.parent) names.Add(value.name);
        names.Reverse();
        return string.Join("/",names);
    }

    private static void RegisterProjectile(NetworkBehaviour projectile,Rigidbody body,NetworkIdentity owner)
    {
        if(!Plugin.TestMode || !projectile) return;
        if(currentCommand!=null)
        {
            var sample=new ProjectileObservation{Component=projectile,Body=body,CreatedAt=Time.unscaledTime,
                ownerNetId=owner?owner.netId:0,ownerMatchesCommand=owner && owner.netId==currentCommand.ownerNetId,
                creationPosition=V(projectile.transform.position),creationForward=V(projectile.transform.forward),
                CameraForward=currentCommand.CameraForward};
            currentCommand.projectiles.Add(sample);
            observedProjectiles[projectile.Pointer]=sample;
        }
        if(fixtureActive && owner && LocalPlayers.Players.Any(p=>p && p.netId==owner.netId))
            fixtureProjectiles[projectile.Pointer]=projectile;
    }

    private static void SampleProjectile(ProjectileObservation sample,string source)
    {
        if(!sample.Component) return;
        sample.netId=sample.Component.netId;
        if(sample.velocityObserved || !sample.Body) return;
        Vector3 velocity=sample.Body.linearVelocity;
        if(velocity.sqrMagnitude<0.01f) return;
        sample.velocityObserved=true;
        sample.velocity=V(velocity);
        sample.velocitySource=source+"; sampled physics velocity, may include gravity/contact effects";
        sample.velocitySampleDelay=Time.unscaledTime-sample.CreatedAt;
        sample.velocityCameraForwardDot=Vector3.Dot(velocity.normalized,sample.CameraForward);
    }

    private static void BeforeProjectileCollision(NetworkBehaviour __instance)
    {
        if(observedProjectiles.TryGetValue(__instance.Pointer,out var sample))
            SampleProjectile(sample,"before-native-collision-handler");
    }

    private static void ObserveProjectiles()
    {
        foreach(var item in observedProjectiles.ToArray())
        {
            if(!item.Value.Component) {observedProjectiles.Remove(item.Key);continue;}
            SampleProjectile(item.Value,"player-update-after-physics");
        }
        if(fixtureActive && (Time.unscaledTime>fixtureExpires || !LocalPlayers.Active)) Cleanup();
    }

    private static bool BeforeFixtureExplosion(NetworkBehaviour __instance)
    {
        if(!Plugin.TestMode || !fixtureProjectiles.Remove(__instance.Pointer)) return true;
        if(observedProjectiles.TryGetValue(__instance.Pointer,out var sample))
        {
            SampleProjectile(sample,"before-fixture-explosion");
            sample.explosionSuppressed=true;
        }
        // Tests cover native input/equip/ammo/creation/aim. Blast damage is
        // intentionally excluded so a valid shot cannot kill the next subject.
        if(NetworkServer.active && __instance.isServer) NetworkServer.Destroy(__instance.gameObject);
        else UnityEngine.Object.Destroy(__instance.gameObject);
        return false;
    }

    private static void CleanupProjectiles()
    {
        foreach(var projectile in fixtureProjectiles.Values.ToArray())
        {
            if(!projectile) continue;
            if(NetworkServer.active && projectile.isServer) NetworkServer.Destroy(projectile.gameObject);
            else UnityEngine.Object.Destroy(projectile.gameObject);
        }
        fixtureProjectiles.Clear();
    }

    internal static void Cleanup()
    {
        CleanupProjectiles();
        fixtureActive=false;
    }
    private static void CountShot(NetworkIdentity owner, WeaponsCollectable type)
    {
        if(owner==null) return;
        shots.TryGetValue((owner.netId,type),out int count);
        shots[(owner.netId,type)]=count+1;
        Plugin.Info("Weapon projectile: "+type+", owner="+owner.netId+", count="+(count+1));
    }
    internal static OnDefaultWeaponsManager Manager(OnlineBasePlayerRef actor) => actor.GetComponentInChildren<OnDefaultWeaponsManager>(true);
    private static WeaponBase Weapon(OnDefaultWeaponsManager manager, WeaponsCollectable type)
    {
        foreach(var weapon in manager.GetWeaponsList()) if(weapon.WeaponType==type) return weapon;
        throw new InvalidOperationException("Weapon missing: "+type);
    }
    internal static object Inspect(OnlineBasePlayerRef actor)
    {
        var manager=Manager(actor);
        if(!manager) return null;
        var inventory=new List<object>();
        foreach(var weapon in manager.GetWeaponsList())
            inventory.Add(new{type=weapon.WeaponType.ToString(),ammo=weapon.CurrentAmmo,
                rootActive=weapon.root && weapon.root.activeInHierarchy,
                local=weapon.isLocalPlayer,owned=weapon.isOwned,owner=weapon.playerRef?weapon.playerRef.netId:0});
        return new {initialized=manager.initialized,index=manager.CurrentWeaponIndex,
            selected=manager.CurrentWeapon?manager.CurrentWeapon.WeaponType.ToString():"None",
            aiming=manager.CameraController && manager.CameraController.IsAiming,inventory};
    }
    internal static void Prepare(string type)
    {
        if(!Plugin.TestMode) throw new InvalidOperationException("Weapon tests require explicit test mode.");
        Cleanup();
        Selected=Enum.Parse<WeaponsCollectable>(type,true);
        if(Selected==WeaponsCollectable.Punch) throw new ArgumentException("Choose Rpg or StickyBomb");
        int count=LocalPlayers.Players.Count;
        previousAmmo=new int[count];previousShots=new int[count];previousIndices=new int[count];previousResets=new int[count];previousCommands=new int[count];
        for(int i=0;i<count;i++)
        {
            var actor=LocalPlayers.Players[i];var manager=Manager(actor);
            manager.Collect(WeaponsCollectable.Rpg,3);
            manager.Collect(WeaponsCollectable.StickyBomb,3);
            manager.SetDefaultWeapon();
            previousAmmo[i]=Weapon(manager,Selected).CurrentAmmo;
            shots.TryGetValue((actor.netId,Selected),out previousShots[i]);
            previousIndices[i]=manager.CurrentWeaponIndex;
            previousResets[i]=LocalPlayers.GetResetCount(actor);
            previousCommands[i]=commands.TryGetValue((actor.netId,Selected),out var samples)?samples.Count:0;
        }
        fixtureActive=true;
        fixtureExpires=Time.unscaledTime+count*6f+10f;
    }
    internal static GamepadState Drive(int player,float elapsed)
    {
        var state=new GamepadState();
        // Keep the existing six-second driver contract. The first three seconds
        // allow maps/respawn/cameras to settle; test blasts cannot kill subjects.
        elapsed-=3f;
        if(elapsed<0) return state;
        if(elapsed<0.9f) state.rightStick=new Vector2(0f,1f);
        var manager=Manager(LocalPlayers.Players[player]);
        // Exercise the actual per-player d-pad switching route before aiming.
        if(elapsed<1.1f && (!manager.CurrentWeapon || manager.CurrentWeapon.WeaponType!=Selected) && elapsed%0.35f<0.10f)
            state.buttons |= 1u<<(int)GamepadButton.DpadRight;
        if(elapsed>=1.1f) state.buttons |= 1u<<(int)GamepadButton.LeftShoulder;
        if(elapsed>=1.6f && elapsed<1.8f) state.buttons |= 1u<<(int)GamepadButton.RightShoulder;
        return state;
    }
    internal static object Capture(int selected)
    {
        bool pass=true;var values=new List<object>();
        for(int i=0;i<LocalPlayers.Players.Count;i++)
        {
            var actor=LocalPlayers.Players[i];var manager=Manager(actor);
            var weapon=Weapon(manager,Selected);
            int ammo=weapon.CurrentAmmo;
            shots.TryGetValue((actor.netId,Selected),out int fired);
            int ammoUsed=previousAmmo[i]-ammo, spawned=fired-previousShots[i];
            bool equipped=manager.CurrentWeapon && manager.CurrentWeapon.WeaponType==Selected;
            bool model=weapon.root && weapon.root.activeInHierarchy;
            var key=(actor.netId,Selected);
            var newCommands=commands.TryGetValue(key,out var samples)?samples.Skip(previousCommands[i]).ToArray():Array.Empty<FireObservation>();
            foreach(var command in newCommands)
                foreach(var projectile in command.projectiles) SampleProjectile(projectile,"capture");
            var actualFire=newCommands.Length==1?newCommands[0]:null;
            bool aimValid=actualFire!=null && actualFire.error==null && actualFire.centered &&
                !actualFire.targetInsideOwnCollider && actualFire.cameraDepth>0 &&
                actualFire.targetDistanceFromSpawn>0.5f && actualFire.spawnToTargetCameraForwardDot>0f;
            bool actualCreation=actualFire!=null && actualFire.projectiles.Count==1 &&
                actualFire.projectiles[0].ownerMatchesCommand;
            int resets=LocalPlayers.GetResetCount(actor);
            bool respawned=resets!=previousResets[i];
            bool correct=!actor.IsDead && !respawned && (i==selected ? equipped && model && ammoUsed==1 && spawned==1 &&
                newCommands.Length==1 && aimValid && actualCreation :
                ammoUsed==0 && spawned==0 && newCommands.Length==0 && manager.CurrentWeaponIndex==previousIndices[i]);
            pass &= correct;
            values.Add(new{player=i+1,equipped,model,ammo,ammoUsed,spawned,correct,
                dead=actor.IsDead,respawned,aimValid,actualCreation,actualServerCommands=newCommands,details=Inspect(actor)});
            previousAmmo[i]=ammo;previousShots[i]=fired;previousIndices[i]=manager.CurrentWeaponIndex;
            previousResets[i]=resets;
            previousCommands[i]=samples?.Count??0;
        }
        CleanupProjectiles();
        if(selected==LocalPlayers.Players.Count-1) Cleanup();
        Plugin.Info(Selected+" equip/fire isolation P"+(selected+1)+": "+(pass?"PASS":"FAIL"));
        return new{drivenPlayer=selected+1,weapon=Selected.ToString(),pass,
            fixture="Only this automated test's native-created projectiles have blast damage suppressed and are cleaned after each phase; no damage/death acceptance claimed.",values};
    }
    internal static void Pickup(int player,string type)
    {
        var target=Enum.Parse<WeaponsCollectable>(type,true);
        var actor=LocalPlayers.Players[player-1];
        foreach(var pickup in UnityEngine.Object.FindObjectsOfType<AmmoPickup>())
        {
            if(pickup.weaponType!=target || pickup.collected || !pickup.isServer) continue;
            pickup.OnTriggerEnter(actor.PCollider);
            Plugin.Info("Native pickup trigger: P"+player+" "+target+", collected="+pickup.collected);
            return;
        }
        throw new InvalidOperationException("No available scene pickup: "+type);
    }
}
