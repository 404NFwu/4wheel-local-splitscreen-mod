using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using GameManager.OnlineBase;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;

namespace WheelchairLocalCoop;

// Test-only observation and synthetic state generation. TestDriver owns the
// virtual devices; this class never changes actor positions or game controls.
internal static class MovementSoak
{
    private sealed class Seat
    {
        public int player { get; set; }
        public uint netId { get; set; }
        public int actorGenerations { get; set; }
        public long successfulFixedUpdates { get; set; }
        public float eligibleDriveSeconds { get; set; }
        public int inputSamples { get; set; }
        public int matchingInputSamples { get; set; }
        public float distanceTravelled { get; set; }
        public float horizontalDistanceTravelled { get; set; }
        public int deaths { get; set; }
        public int revivals { get; set; }
        public int resetCalls { get; set; }
        public int teleportSamplesExcluded { get; set; }
        public int fixedUpdateStallEpisodes { get; set; }
        public int inputMismatchEpisodes { get; set; }
        public int observationErrors { get; set; }
        public int noProgressEpisodes { get; set; }
        public int recoveredProgressEpisodes { get; set; }
        public bool unresolvedNoProgress { get; set; }
        public string eligibility { get; set; } = "waiting";
        public float verticalInput { get; set; }
        public float horizontalInput { get; set; }
        public float expectedVertical { get; set; }
        public float expectedHorizontal { get; set; }
        public float[] position { get; set; }
        internal IntPtr ActorPointer;
        internal IntPtr ControllerPointer;
        internal long LastFixedCount;
        internal int LastResetCount;
        internal bool WasDead;
        internal Vector3 PreviousPosition;
        internal Vector3 ProgressAnchor;
        internal float ProgressSeconds;
        internal float NoFixedSeconds;
        internal float InputMismatchSeconds;
        internal float ExpectedSince;
        internal float RecoveryUntil;
        internal bool FixedFailureReported;
        internal bool InputFailureReported;
        internal GamepadState ExpectedState;
    }

    private static readonly Dictionary<IntPtr, long> fixedCounts = new();
    private static readonly List<object> events = new();
    private static readonly List<object> summaries = new();
    private static Seat[] seats = Array.Empty<Seat>();
    private static bool installed;
    private static float started;
    private static float duration;
    private static float lastSample;
    private static float nextSummary;
    private static DateTime startedUtc;
    private const float SeatPhaseSeconds = 16f;
    private const float InputSettleSeconds = 0.2f;
    internal static bool Running { get; private set; }
    internal static string Outcome { get; private set; } = "not-started";

    internal static void Install(Harmony harmony)
    {
        if (!Plugin.TestMode || installed) return;
        var original = AccessTools.Method(typeof(OnlineBaseWheelchairController), "FixedUpdate")
            ?? throw new MissingMethodException(nameof(OnlineBaseWheelchairController), "FixedUpdate");
        harmony.Patch(original, postfix: new HarmonyMethod(typeof(MovementSoak), nameof(AfterFixedUpdate)));
        installed = true;
    }

    private static void AfterFixedUpdate(OnlineBaseWheelchairController __instance)
    {
        if (!Running || __instance == null) return;
        var key = __instance.Pointer;
        fixedCounts.TryGetValue(key, out long count);
        fixedCounts[key] = count + 1;
    }

    internal static void Start(float durationSeconds = 360f)
    {
        if (!Plugin.TestMode || !installed) throw new InvalidOperationException("Movement soak requires test mode and installed observation patches.");
        if (Running) throw new InvalidOperationException("A movement soak is already running.");
        if (LocalPlayers.Players.Count != 4) throw new InvalidOperationException("Movement soak requires four ready actors and four virtual pads.");
        if (!float.IsFinite(durationSeconds) || durationSeconds < 20f)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Use at least 20 seconds; regression default is 360.");
        duration = durationSeconds;
        started = lastSample = Time.unscaledTime;
        nextSummary = started + 20f;
        startedUtc = DateTime.UtcNow;
        seats = Enumerable.Range(0, 4).Select(i => new Seat { player = i + 1, ExpectedSince = started }).ToArray();
        fixedCounts.Clear(); events.Clear(); summaries.Clear();
        Running = true;
        Outcome = "running";
        WriteReport(false);
        Plugin.Info("Movement soak started for " + duration.ToString("F0") + " seconds; virtual inputs only.");
    }

    // TestDriver queues this state to the matching virtual device once per frame.
    // Sixteen-second turns contain RT steering, two jump taps, and a native
    // hold-Y reset. No scene teleports or direct reset calls are used here.
    internal static GamepadState StateFor(int seat)
    {
        var state = new GamepadState();
        if (!Running || seat < 0 || seat >= seats.Length) return state;
        float elapsed = Time.unscaledTime - started;
        int turn = (int)(elapsed / SeatPhaseSeconds);
        if (seat == turn % seats.Length)
        {
            float phase = elapsed % SeatPhaseSeconds;
            if (phase >= 0.4f && phase < 12.5f)
            {
                state.rightTrigger = 0.8f;
                // Alternate directions between turns and during the same turn.
                float direction = (((int)((phase - 0.4f) / 3f) + turn / seats.Length) & 1) == 0 ? 1f : -1f;
                // Native MoveWASD reads stick directions as Button actions;
                // exceed the button press threshold, not just stick deadzone.
                state.leftStick = new Vector2(direction * 0.7f, 0f);
                if ((phase >= 3f && phase < 3.2f) || (phase >= 8f && phase < 8.2f))
                    state.buttons |= 1u << (int)GamepadButton.South;
            }
            if (phase >= 13.3f && phase < 15.3f)
                state.buttons |= 1u << (int)GamepadButton.North;
        }
        var row = seats[seat];
        if (row.ExpectedState.rightTrigger != state.rightTrigger || row.ExpectedState.leftStick.x != state.leftStick.x ||
            row.ExpectedState.buttons != state.buttons)
            row.ExpectedSince = Time.unscaledTime;
        row.ExpectedState = state;
        row.expectedVertical = state.rightTrigger;
        row.expectedHorizontal = state.leftStick.x;
        return state;
    }

    // Returns true only on the completion frame. Caller must then remove the
    // virtual devices so real controllers regain their normal assignments.
    internal static bool Tick()
    {
        if (!Running) return false;
        float now = Time.unscaledTime;
        if (now - lastSample >= 0.25f)
        {
            float delta = Mathf.Min(now - lastSample, 0.6f);
            lastSample = now;
            for (int i = 0; i < seats.Length; i++) Sample(seats[i], i, delta, now);
        }
        if (now >= nextSummary)
        {
            nextSummary = now + 20f;
            RecordSummary();
            WriteReport(false);
            Plugin.Info("Movement soak " + Mathf.FloorToInt(now - started) + "/" + duration.ToString("F0") + "s: " +
                string.Join("; ", seats.Select(s => "P" + s.player + " fixed=" + s.successfulFixedUpdates +
                    " drive=" + s.eligibleDriveSeconds.ToString("F1") + "s travel=" + s.horizontalDistanceTravelled.ToString("F1") +
                    "m reset=" + s.resetCalls + " " + s.eligibility)));
        }
        if (now - started < duration) return false;
        Running = false;
        bool confirmedFailure = seats.Any(s => s.fixedUpdateStallEpisodes > 0 || s.inputMismatchEpisodes > 0);
        bool covered = seats.All(s => s.eligibleDriveSeconds >= 10f && s.successfulFixedUpdates >= 50 && s.horizontalDistanceTravelled > 0.25f);
        Outcome = confirmedFailure ? "failed" : covered && seats.All(s => !s.unresolvedNoProgress && s.observationErrors == 0) ? "passed" : "inconclusive";
        RecordSummary();
        WriteReport(true);
        Plugin.Info("Movement soak completed: " + Outcome + "; see localcoop-movement-soak.json; clear virtual controllers now.");
        return true;
    }

    internal static void Cancel(string reason = "cancelled")
    {
        if (!Running) return;
        Running = false;
        Outcome = "cancelled";
        events.Add(new { time = Time.unscaledTime - started, type = "cancelled", reason });
        WriteReport(true);
    }

    private static void Sample(Seat row, int index, float delta, float now)
    {
        try
        {
            var actor = index < LocalPlayers.Players.Count ? LocalPlayers.Players[index] : null;
            if (!actor || !actor.WcController)
            {
                row.eligibility = "actor-unavailable";
                ResetWindow(row);
                return;
            }
            var wc = actor.WcController;
            Vector3 position = actor.transform.position;
            int resets = LocalPlayers.GetResetCount(actor);
            bool newlyBound = row.ActorPointer != actor.Pointer || row.ControllerPointer != wc.Pointer;
            if (newlyBound)
            {
                row.ActorPointer = actor.Pointer;
                row.ControllerPointer = wc.Pointer;
                row.netId = actor.netId;
                row.actorGenerations++;
                row.LastFixedCount = 0;
                row.LastResetCount = resets;
                row.WasDead = actor.IsDead;
                row.PreviousPosition = row.ProgressAnchor = position;
                row.RecoveryUntil = now + 1f;
                ResetWindow(row);
            }
            fixedCounts.TryGetValue(wc.Pointer, out long fixedCount);
            long completed = Math.Max(0, fixedCount - row.LastFixedCount);
            row.successfulFixedUpdates += completed;
            row.LastFixedCount = fixedCount;
            bool reset = resets != row.LastResetCount;
            if (reset)
            {
                row.resetCalls += Math.Max(0, resets - row.LastResetCount);
                row.LastResetCount = resets;
                row.RecoveryUntil = now + 1f;
                ResetWindow(row);
            }
            bool dead = actor.IsDead;
            if (dead && !row.WasDead) row.deaths++;
            if (!dead && row.WasDead)
            {
                row.revivals++;
                row.RecoveryUntil = now + 1f;
                ResetWindow(row);
            }
            row.WasDead = dead;
            Vector3 step = position - row.PreviousPosition;
            float distance = step.magnitude;
            if (!newlyBound && !reset && now >= row.RecoveryUntil && float.IsFinite(distance) && distance < 8f)
            {
                row.distanceTravelled += distance;
                row.horizontalDistanceTravelled += new Vector2(step.x, step.z).magnitude;
            }
            else if (!newlyBound && (reset || distance >= 8f)) row.teleportSamplesExcluded++;
            row.PreviousPosition = position;
            row.position = new[] { position.x, position.y, position.z };
            row.verticalInput = wc.VerticalInput;
            row.horizontalInput = wc.HorizontalInput;
            row.eligibility = IneligibleReason(actor, now < row.RecoveryUntil);
            bool driving = row.ExpectedState.rightTrigger > 0.2f;
            if (row.eligibility != "eligible" || !driving)
            {
                ResetWindow(row);
                row.ProgressAnchor = position;
                return;
            }
            if (now - row.ExpectedSince < InputSettleSeconds) return;
            row.eligibleDriveSeconds += delta;
            row.inputSamples++;
            bool inputCorrect = row.verticalInput > 0.2f && Math.Sign(row.horizontalInput) == Math.Sign(row.expectedHorizontal) &&
                Mathf.Abs(row.horizontalInput) > 0.1f;
            if (inputCorrect)
            {
                row.matchingInputSamples++;
                row.InputMismatchSeconds = 0f;
                row.InputFailureReported = false;
            }
            else
            {
                row.InputMismatchSeconds += delta;
                if (row.InputMismatchSeconds >= 1.5f && !row.InputFailureReported)
                {
                    row.inputMismatchEpisodes++;
                    row.InputFailureReported = true;
                    Observe(row, "input-mismatch", completed);
                }
            }
            if (completed > 0)
            {
                row.NoFixedSeconds = 0f;
                row.FixedFailureReported = false;
            }
            else
            {
                row.NoFixedSeconds += delta;
                if (row.NoFixedSeconds >= 1.5f && !row.FixedFailureReported)
                {
                    row.fixedUpdateStallEpisodes++;
                    row.FixedFailureReported = true;
                    Observe(row, "fixed-update-stalled", completed);
                }
            }
            Vector3 progress = position - row.ProgressAnchor;
            if (new Vector2(progress.x, progress.z).magnitude > 0.15f)
            {
                if (row.unresolvedNoProgress) row.recoveredProgressEpisodes++;
                row.unresolvedNoProgress = false;
                row.ProgressSeconds = 0f;
                row.ProgressAnchor = position;
            }
            else
            {
                row.ProgressSeconds += delta;
                if (row.ProgressSeconds >= 6f && !row.unresolvedNoProgress)
                {
                    row.noProgressEpisodes++;
                    row.unresolvedNoProgress = true;
                    // A wall can legitimately stop translation while physics
                    // and input work. Keep this as an unresolved observation,
                    // not an asserted engine failure; a subsequent drive can clear it.
                    Observe(row, "no-progress-check-obstacle", completed);
                }
            }
        }
        catch (Exception error)
        {
            row.observationErrors++;
            row.eligibility = "observation-error";
            ResetWindow(row);
            if (!events.Any(e => e is string text && text == error.Message))
                events.Add(error.Message);
        }
    }

    private static string IneligibleReason(OnlineBasePlayerRef actor, bool recovery)
    {
        if (!actor.gameObject.activeInHierarchy || !actor.WcController.enabled) return "inactive";
        if (actor.IsDead) return "dead";
        var finish = actor.TryCast<OnDefaultFinishLinePlayerRef>();
        if (finish != null && finish.LevelCompleted) return "finished";
        var mode = actor.GameMode;
        if (mode == null || !mode.IsGameplayScene()) return "not-in-level";
        if (Time.timeScale <= 0f || mode.IsPaused) return "paused";
        if (!mode.GameInProgress || (mode.EnableInitialCountdown && !mode.RaceStarted)) return "countdown-or-ended";
        if (!actor.WcController.controlsEnabled) return "controls-disabled";
        return recovery ? "respawn-settling" : "eligible";
    }

    private static void ResetWindow(Seat row)
    {
        row.ProgressSeconds = row.NoFixedSeconds = row.InputMismatchSeconds = 0f;
        row.FixedFailureReported = row.InputFailureReported = false;
    }

    private static void Observe(Seat row, string type, long completed)
    {
        events.Add(new { time = Time.unscaledTime - started, player = row.player, type, fixedUpdatesSinceSample = completed,
            vertical = row.verticalInput, horizontal = row.horizontalInput,
            expectedVertical = row.expectedVertical, expectedHorizontal = row.expectedHorizontal, position = row.position });
        Plugin.Info("Movement soak P" + row.player + ": " + type);
    }

    private static void RecordSummary()
    {
        summaries.Add(new { time = Time.unscaledTime - started, players = seats.Select(s => new { s.player, s.netId,
            s.successfulFixedUpdates, s.eligibleDriveSeconds, s.inputSamples, s.matchingInputSamples,
            s.horizontalDistanceTravelled, s.deaths, s.revivals, s.resetCalls, s.eligibility,
            s.fixedUpdateStallEpisodes, s.inputMismatchEpisodes, s.unresolvedNoProgress }).ToArray() });
    }

    private static void WriteReport(bool completed)
    {
        var report = new { test = "four-player-native-input-movement-soak", startedUtc, requestedDurationSeconds = duration,
            elapsedSeconds = Time.unscaledTime - started, completed, outcome = Outcome,
            successRule = "All four seats: >=10 eligible driving seconds, >=50 successful FixedUpdates, >0.25m horizontal travel; no persistent input/FixedUpdate failure or unresolved no-progress observation.",
            noProgressMeaning = "Six seconds without 0.15m horizontal progress is an observation; collisions can legitimately block travel. It is not alone proof of a movement engine failure.",
            players = seats, events, summaries };
        File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "localcoop-movement-soak.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
