using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace STS2_MCP;

/// <summary>
/// Diagnostics for the <c>NetService.IsGameLoading</c> flag. Observes only — no
/// game behaviour changes here.
/// </summary>
/// <remarks>
/// <para>
/// <c>IsGameLoading</c> is refcounted process-globally by <c>NetLoadingHandle</c>
/// (a static <c>Dictionary&lt;INetGameService, int&gt;</c>), and the counter has
/// no upper bound. One <c>using</c> scope in <c>RunManager</c> whose await never
/// completes leaves the count at 1 forever: every later Dispose then decrements
/// 2 -> 1 and never reaches the 1 that clears the flag. From that moment
/// <c>IsRunTransitionInFlight</c> is permanently true and choose_event_option
/// refuses for the rest of the session.
/// </para>
/// <para>
/// Observed 2026-08-08: an Ancient event (Tezcatara, floor 18) where the game's
/// own option buttons stayed enabled and clickable while the API refused, more
/// than ten minutes after the room-entered log line. These patches record where
/// each handle is opened so the scope that leaked names itself, instead of us
/// guessing at which await hung.
/// </para>
/// </remarks>
public static partial class McpMod
{
    private sealed class LoadingHandleInfo
    {
        public int Id;
        public ulong OpenedMs;
        public string Origin = "";
    }

    /// <summary>
    /// How long the flag must stay held before the open handles are dumped. A
    /// real room entry holds it for about a second (two 0.8s room fades under
    /// Instant Mode, which <c>NTransition.RoomFadeIn</c> never special-cases),
    /// so anything past this is the leak, not a slow transition.
    /// </summary>
    private const ulong LoadingStuckReportMs = 10000;

    // Reference-keyed: NetLoadingHandle does not override Equals/GetHashCode, so
    // each handle instance is its own key and Dispose matches its own ctor entry.
    private static readonly Dictionary<NetLoadingHandle, LoadingHandleInfo> _openLoadingHandles = new();
    private static int _loadingHandleSeq;
    private static ulong _loadingHeldSinceMs;
    private static bool _loadingStuckReported;

    private static string CaptureLoadingOrigin()
    {
        // No file info: this runs on every room transition and the type/method
        // names alone identify the scope (async bodies show up as their
        // state-machine type, e.g. RunManager+<EnterMapPointInternal>d__96).
        var trace = new StackTrace(2, fNeedFileInfo: false);
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < trace.FrameCount && shown < 6; i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            var declaring = method?.DeclaringType;
            if (declaring == null) continue;

            string name = $"{declaring.FullName}.{method!.Name}";
            if (name.Contains("HarmonyLib") || name.Contains("STS2_MCP")) continue;

            if (shown > 0) sb.Append(" <- ");
            sb.Append(name);
            shown++;
        }
        return sb.Length == 0 ? "<unknown>" : sb.ToString();
    }

    private static void NoteLoadingHandleOpened(NetLoadingHandle handle)
    {
        var info = new LoadingHandleInfo
        {
            Id = ++_loadingHandleSeq,
            OpenedMs = Time.GetTicksMsec(),
            Origin = CaptureLoadingOrigin()
        };
        _openLoadingHandles[handle] = info;

        if (_openLoadingHandles.Count == 1)
        {
            _loadingHeldSinceMs = info.OpenedMs;
            _loadingStuckReported = false;
        }

        GD.Print($"[STS2 MCP] loading+ #{info.Id} open={_openLoadingHandles.Count} {info.Origin}");
    }

    private static void NoteLoadingHandleClosed(NetLoadingHandle handle)
    {
        if (!_openLoadingHandles.TryGetValue(handle, out var info)) return;
        _openLoadingHandles.Remove(handle);

        ulong held = Time.GetTicksMsec() - info.OpenedMs;
        GD.Print($"[STS2 MCP] loading- #{info.Id} open={_openLoadingHandles.Count} held={held}ms");

        if (_openLoadingHandles.Count == 0)
        {
            _loadingHeldSinceMs = 0;
            _loadingStuckReported = false;
        }
    }

    /// <summary>
    /// Called every process frame. Reports once per continuous held period, so a
    /// leak produces exactly one report per session rather than a log flood.
    /// </summary>
    internal static void CheckLoadingHandleLeak()
    {
        if (_loadingStuckReported || _loadingHeldSinceMs == 0 || _openLoadingHandles.Count == 0) return;

        ulong now = Time.GetTicksMsec();
        if (now - _loadingHeldSinceMs < LoadingStuckReportMs) return;
        _loadingStuckReported = true;

        var sb = new StringBuilder();
        sb.Append($"[STS2 MCP] IsGameLoading has been held for {(now - _loadingHeldSinceMs) / 1000.0:F1}s by ");
        sb.Append($"{_openLoadingHandles.Count} open NetLoadingHandle(s). Every event choice is refused while it is ");
        sb.Append("set, and the refcount can no longer reach zero, so this is the leak:");
        foreach (var info in _openLoadingHandles.Values)
            sb.Append($"\n  #{info.Id} opened {(now - info.OpenedMs) / 1000.0:F1}s ago at {info.Origin}");
        GD.PrintErr(sb.ToString());
    }

    // --- Harmony patches (observe only) ---

    [HarmonyPatch(typeof(NetLoadingHandle), MethodType.Constructor, typeof(INetGameService))]
    static class NetLoadingHandleCtorPatch
    {
        static void Postfix(NetLoadingHandle __instance)
        {
            try { NoteLoadingHandleOpened(__instance); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] loading-handle diagnostics (open) failed: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(NetLoadingHandle), nameof(NetLoadingHandle.Dispose))]
    static class NetLoadingHandleDisposePatch
    {
        static void Postfix(NetLoadingHandle __instance)
        {
            try { NoteLoadingHandleClosed(__instance); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] loading-handle diagnostics (close) failed: {ex.Message}"); }
        }
    }
}
