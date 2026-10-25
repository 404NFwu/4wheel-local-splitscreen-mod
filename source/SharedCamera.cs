using System;
using System.Collections.Generic;
using Cinemachine;
using GameManager.OnlineBase;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace WheelchairLocalCoop;

/// <summary>Four independent third-person viewports; name retained for integration compatibility.</summary>
internal sealed class SharedCamera
{
    private sealed class View
    {
        internal Camera Camera;
        internal GameObject CreatedObject;
        internal Transform Target;
        internal OnlineBasePlayerRef Actor;
        internal OnlineBaseCameraController Controller;
        internal Camera OriginalActorCamera;
        internal float Yaw;
        internal float Pitch = 23f;
        internal float Distance = 5.2f;
        internal float RequestedDistance = 5.2f;
        internal float MinimumDistance = 3f;
        internal float MaximumDistance = 10f;
        internal Vector3 Pivot;
        internal bool Initialized;
    }

    private static readonly Rect[] Quadrants =
    {
        new(0f, 0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f, 0.5f),
        new(0f, 0f, 0.5f, 0.5f), new(0.5f, 0f, 0.5f, 0.5f)
    };
    private readonly View[] _views = new View[4];
    private static SharedCamera _active;
    private const float GamepadZoomSpeed = 3f;
    private const float ShoulderOffset = 0.45f;
    private Camera _camera;
    private Camera _savedSettings;
    private GameObject _savedObject;
    private CinemachineBrain _brain;
    private bool _brainWasEnabled;
    private bool _cameraWasEnabled;
    private Vector3 _originalPosition;
    private Quaternion _originalRotation;
    private UniversalAdditionalCameraData _mainData;
    private CameraRenderType _originalRenderType;
    private AntialiasingMode _originalAntialiasing;
    private bool _collisionAvailable = true;

    internal bool IsAttached => _camera;
    internal int ViewCount => _camera ? 4 : 0;
    internal float FollowDistance { get; set; } = 5.2f;

    // Requested zoom remains independent of the temporary camera-wall distance.
    // Exposed for synthetic input checks without moving the real controllers.
    internal static float GetZoomDistance(Transform target)
    {
        if (!target || _active == null) return float.NaN;
        int id = target.GetInstanceID();
        foreach (View view in _active._views)
            if (view != null && view.Target && view.Target.GetInstanceID() == id)
                return view.RequestedDistance;
        return float.NaN;
    }

    internal void Tick(IReadOnlyList<Transform> targets, Camera cam)
    {
        if (!cam || targets.Count < 2) { Reset(); return; }
        InputRouting.PruneDestroyedSeats();
        if (!_camera || _camera.GetInstanceID() != cam.GetInstanceID() || !ViewsExist()) Attach(cam);
        if (_brain) _brain.enabled = false;
        float delta = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        for (int i = 0; i < _views.Length; ++i)
        {
            View view = _views[i];
            Transform target = i < targets.Count ? targets[i] : null;
            if (!target || !target.gameObject.activeInHierarchy)
            {
                ReleaseTarget(view);
                ConfigureBlank(view.Camera, i);
                continue;
            }
            if (!view.Target || view.Target.GetInstanceID() != target.GetInstanceID()) SetTarget(view, target);
            ConfigureActive(view.Camera, i);
            if (view.Controller) view.Controller.mainCamera = view.Camera;
            UpdateView(view, delta);
        }
    }

    private bool ViewsExist()
    {
        for (int i = 0; i < _views.Length; ++i)
            if (_views[i] == null || !_views[i].Camera) return false;
        return _savedSettings;
    }

    private void Attach(Camera camera)
    {
        Reset();
        _active = this;
        _camera = camera;
        _cameraWasEnabled = camera.enabled;
        _originalPosition = camera.transform.position;
        _originalRotation = camera.transform.rotation;
        _brain = camera.GetComponent<CinemachineBrain>();
        if (_brain) { _brainWasEnabled = _brain.enabled; _brain.enabled = false; }
        // Backup never renders and contains no audio or game scripts.
        _savedObject = new GameObject("LocalCoop_CameraSettingsBackup");
        _savedObject.SetActive(false);
        _savedSettings = _savedObject.AddComponent<Camera>();
        _savedSettings.CopyFrom(camera);
        _savedSettings.enabled = false;
        _mainData = camera.GetComponent<UniversalAdditionalCameraData>();
        if (_mainData)
        {
            _originalRenderType = _mainData.renderType;
            _originalAntialiasing = _mainData.antialiasing;
            _mainData.renderType = CameraRenderType.Base;
            if (_mainData.antialiasing == AntialiasingMode.TemporalAntiAliasing)
                _mainData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        }
        _views[0] = new View { Camera = camera };
        for (int i = 1; i < 4; ++i)
        {
            var created = new GameObject("LocalCoop_Viewport_P" + (i + 1));
            created.SetActive(false);
            var extra = created.AddComponent<Camera>();
            extra.CopyFrom(_savedSettings);
            extra.enabled = false;
            CopyRenderPipelineSettings(created);
            _views[i] = new View { Camera = extra, CreatedObject = created };
            created.SetActive(true);
        }
    }

    private void CopyRenderPipelineSettings(GameObject destination)
    {
        if (!_mainData) return;
        var data = destination.AddComponent<UniversalAdditionalCameraData>();
        data.renderType = CameraRenderType.Base;
        data.SetRenderer(_mainData.m_RendererIndex);
        data.renderShadows = _mainData.renderShadows;
        data.requiresDepthOption = _mainData.requiresDepthOption;
        data.requiresColorOption = _mainData.requiresColorOption;
        data.volumeLayerMask = _mainData.volumeLayerMask;
        data.volumeTrigger = destination.transform;
        data.renderPostProcessing = _mainData.renderPostProcessing;
        data.antialiasing = _mainData.antialiasing;
        data.antialiasingQuality = _mainData.antialiasingQuality;
        data.stopNaN = _mainData.stopNaN;
        data.dithering = _mainData.dithering;
        data.allowXRRendering = false;
        // Each viewport is a Base camera. Overlay stacks belong to the original
        // player and are intentionally not copied to the other seats.
    }

    private void ConfigureActive(Camera camera, int index)
    {
        camera.enabled = true;
        camera.orthographic = false;
        camera.fieldOfView = 70f;
        camera.nearClipPlane = 0.08f;
        camera.rect = Quadrants[index];
        camera.aspect = Mathf.Max(0.5f, (float)Screen.width / Mathf.Max(1, Screen.height));
        camera.depth = _savedSettings.depth + index * 0.01f;
        camera.cullingMask = _savedSettings.cullingMask;
        camera.backgroundColor = _savedSettings.backgroundColor;
        camera.clearFlags = _savedSettings.clearFlags == CameraClearFlags.Nothing ||
            _savedSettings.clearFlags == CameraClearFlags.Depth ? CameraClearFlags.Skybox : _savedSettings.clearFlags;
        camera.targetTexture = null;
    }

    private void ConfigureBlank(Camera camera, int index)
    {
        ConfigureActive(camera, index);
        camera.cullingMask = 0;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
    }

    private void SetTarget(View view, Transform target)
    {
        ReleaseTarget(view);
        view.Target = target;
        view.Actor = target.GetComponent<OnlineBasePlayerRef>();
        view.Controller = view.Actor ? view.Actor.CamController : null;
        if (view.Controller)
        {
            view.OriginalActorCamera = view.Controller.mainCamera;
            view.Controller.mainCamera = view.Camera;
        }
        view.Yaw = target.eulerAngles.y;
        view.Pitch = 23f;
        // Read the shipped actor's serialized preset, rather than assuming the
        // CameraPreset constructor defaults (3..10) are used by every game mode.
        CameraPreset preset = view.Controller ? view.Controller.CurrentPreset : null;
        view.MinimumDistance = preset != null && float.IsFinite(preset.minDistance) && preset.minDistance > 0f
            ? preset.minDistance : 3f;
        view.MaximumDistance = preset != null && float.IsFinite(preset.maxDistance) && preset.maxDistance >= view.MinimumDistance
            ? preset.maxDistance : Mathf.Max(view.MinimumDistance, 10f);
        view.RequestedDistance = Mathf.Clamp(FollowDistance, view.MinimumDistance, view.MaximumDistance);
        view.Distance = view.RequestedDistance;
        view.Initialized = false;
    }

    private static void ReleaseTarget(View view)
    {
        if (view.Controller && view.Controller.mainCamera && view.Camera &&
            view.Controller.mainCamera.GetInstanceID() == view.Camera.GetInstanceID())
            view.Controller.mainCamera = view.OriginalActorCamera;
        view.Controller = null;
        view.OriginalActorCamera = null;
        view.Actor = null;
        view.Target = null;
        view.Initialized = false;
    }

    private void UpdateView(View view, float delta)
    {
        bool acceptLook = Time.timeScale > 0f && (!view.Actor || view.Actor.GameMode == null || !view.Actor.GameMode.IsPaused);
        if (acceptLook)
        {
            Gamepad pad = InputRouting.GetPad(view.Target);
            if (pad != null)
            {
                Vector2 look = pad.rightStick.ReadValue();
                if (Mathf.Abs(look.x) > 0.12f) view.Yaw += look.x * 105f * delta;
                if (Mathf.Abs(look.y) > 0.12f) view.Pitch = Mathf.Clamp(view.Pitch - look.y * 80f * delta, -12f, 72f);
                if (pad.rightStickButton.wasPressedThisFrame) view.Yaw = view.Target.eulerAngles.y;
                // Native Global.MouseScroll maps Up to positive and Down to
                // negative, then subtracts it from targetCamDistance. Read only
                // this seat's pad; D-pad Left/Right remain weapon selection.
                float zoom = (pad.dpad.up.isPressed ? 1f : 0f) - (pad.dpad.down.isPressed ? 1f : 0f);
                view.RequestedDistance = Mathf.Clamp(view.RequestedDistance - zoom * GamepadZoomSpeed * delta,
                    view.MinimumDistance, view.MaximumDistance);
            }
            else if (InputRouting.IsKeyboard(view.Target) && Mouse.current != null)
            {
                Vector2 look = Mouse.current.delta.ReadValue();
                view.Yaw += look.x * 0.12f;
                view.Pitch = Mathf.Clamp(view.Pitch - look.y * 0.12f, -12f, 72f);
                float wheel = Mouse.current.scroll.ReadValue().y;
                view.RequestedDistance = Mathf.Clamp(view.RequestedDistance - wheel * 0.005f,
                    view.MinimumDistance, view.MaximumDistance);
            }
        }
        Vector3 targetPivot = view.Target.position + Vector3.up * 1.35f;
        bool teleported = !view.Initialized || (view.Pivot - targetPivot).sqrMagnitude > 144f;
        float follow = 1f - Mathf.Exp(-16f * delta);
        view.Pivot = teleported ? targetPivot : Vector3.Lerp(view.Pivot, targetPivot, follow);
        Quaternion rotation = Quaternion.Euler(view.Pitch, view.Yaw, 0f);
        Vector3 backward = -(rotation * Vector3.forward);
        // Shift the entire viewing axis to the right shoulder. Keeping the same
        // rotation moves the head away from the reticle without bending aim rays.
        Vector3 cameraPivot = view.Pivot + Quaternion.Euler(0f, view.Yaw, 0f) * Vector3.right * ShoulderOffset;
        float distance = view.RequestedDistance;
        if (_collisionAvailable)
        {
            try
            {
                // This exact RaycastAll overload exists in the shipped native
                // image (RVA 0x4C6EE10); SphereCastAll was stripped by Unity.
                var hits = Physics.RaycastAll(cameraPivot, backward, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                foreach (var hit in hits)
                {
                    if (!hit.collider || hit.distance <= 0f) continue;
                    if (hit.collider.GetComponentInParent<OnlineBasePlayerRef>()) continue;
                    distance = Mathf.Min(distance, Mathf.Max(0.45f, hit.distance - 0.22f));
                }
            }
            catch (Exception ex)
            {
                // Camera collision is optional. An unavailable stripped API must
                // never stop the remaining players' viewports from rendering.
                _collisionAvailable = false;
                Debug.LogWarning("LocalCoop: camera collision disabled; all four viewports remain active. " + ex.Message);
            }
        }
        view.Distance = teleported || distance < view.Distance ? distance :
            Mathf.Lerp(view.Distance, distance, 1f - Mathf.Exp(-7f * delta));
        view.Camera.transform.SetPositionAndRotation(cameraPivot + backward * view.Distance, rotation);
        view.Initialized = true;
    }

    internal void Reset()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        for (int i = 0; i < _views.Length; ++i)
        {
            View view = _views[i];
            if (view == null) continue;
            ReleaseTarget(view);
            if (view.CreatedObject)
            {
                view.CreatedObject.SetActive(false);
                UnityEngine.Object.Destroy(view.CreatedObject);
            }
            _views[i] = null;
        }
        if (_camera && _savedSettings)
        {
            _camera.CopyFrom(_savedSettings);
            _camera.enabled = _cameraWasEnabled;
            _camera.transform.SetPositionAndRotation(_originalPosition, _originalRotation);
        }
        if (_mainData)
        {
            _mainData.renderType = _originalRenderType;
            _mainData.antialiasing = _originalAntialiasing;
        }
        if (_brain) _brain.enabled = _brainWasEnabled;
        if (_savedObject) UnityEngine.Object.Destroy(_savedObject);
        _camera = null;
        _savedSettings = null;
        _savedObject = null;
        _mainData = null;
        _brain = null;
    }
}
