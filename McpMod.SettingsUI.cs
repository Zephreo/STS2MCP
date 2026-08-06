using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2_MCP;

public static partial class McpMod
{
    private const string InstantModeConfigKey = "instant_mode";

    private static NFastModeTickbox? _instantModeTickbox;
    private static NFastModeTickbox? _originalFastModeTickbox;

    /// <summary>
    /// The player's Instant Mode preference. Persisted in STS2_MCP.conf rather than in the
    /// game's own prefs save, because the game deliberately downgrades a stored
    /// <see cref="FastModeType.Instant" /> back to <see cref="FastModeType.Fast" /> on every
    /// startup (NGame's init block, non-editor builds) — so the game save cannot hold it.
    /// </summary>
    private static bool _instantModeEnabled;

    /// <summary>Whether we currently have Instant forced into the game's prefs.</summary>
    private static bool _instantModeApplied;

    /// <summary>Latched once the game itself has loaded the prefs save. See <see cref="PrefsAreLoaded" />.</summary>
    private static bool _prefsReady;

    private static bool _instantModeErrorLogged;

    internal static void LoadInstantModePreference()
    {
        _instantModeEnabled = ReadConfigBool(InstantModeConfigKey, fallback: false);
        if (_instantModeEnabled)
            GD.Print("[STS2 MCP] Instant Mode is enabled (singleplayer only)");
    }

    private static void SetInstantModeEnabled(bool enabled)
    {
        _instantModeEnabled = enabled;
        WriteConfigValue(InstantModeConfigKey, enabled);
        ReconcileInstantMode();
    }

    /// <summary>
    /// NGame loads the prefs save during its init block, immediately before it launches the
    /// main menu, so seeing the main menu or a live run proves prefs exist. We must not touch
    /// SaveManager.Instance before that point: its getter lazily CONSTRUCTS the SaveManager,
    /// and one built before Steam finishes initializing writes to the local-only save store
    /// (no cloud sync) for the rest of the session.
    /// </summary>
    private static bool PrefsAreLoaded()
    {
        if (_prefsReady) return true;
        try
        {
            if (NGame.Instance?.MainMenu != null || RunManager.Instance.IsInProgress)
                _prefsReady = true;
        }
        catch { /* scene tree not up yet */ }
        return _prefsReady;
    }

    /// <summary>
    /// Pushes the Instant Mode preference into the game's prefs when it should be active and
    /// pulls it back out when it should not. Instant Mode is suppressed during multiplayer
    /// runs — animation timing is shared presentation state there — and is re-applied by
    /// itself once the multiplayer run ends. Called every process frame; cheap and idempotent.
    /// </summary>
    internal static void ReconcileInstantMode()
    {
        // Feature off and nothing of ours applied: leave the game's setting completely alone
        // (including the dev console's own `instant` command).
        if (!_instantModeEnabled && !_instantModeApplied) return;
        if (!PrefsAreLoaded()) return;

        try
        {
            var prefs = SaveManager.Instance.PrefsSave;
            if (prefs == null) return;

            if (_instantModeEnabled && !IsMultiplayerRun())
            {
                if (prefs.FastMode != FastModeType.Instant) prefs.FastMode = FastModeType.Instant;
                _instantModeApplied = true;
            }
            else
            {
                if (prefs.FastMode == FastModeType.Instant) prefs.FastMode = FastModeType.Fast;
                _instantModeApplied = false;
            }
        }
        catch (Exception ex)
        {
            // Runs every frame — only ever report the first failure.
            if (!_instantModeErrorLogged)
            {
                _instantModeErrorLogged = true;
                GD.PrintErr($"[STS2 MCP] Instant Mode reconcile failed: {ex}");
            }
        }
    }

    private static bool IsInInstantModeLine(Node node)
    {
        var current = node;
        while (current != null)
        {
            if (current.Name == "InstantMode") return true;
            current = current.GetParent();
        }
        return false;
    }

    private static T? FindChildOfType<T>(Node parent) where T : class
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is T typed) return typed;
            if (child is Node childNode)
            {
                var found = FindChildOfType<T>(childNode);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static void InjectInstantModeCheckbox(NSettingsScreen settingsScreen)
    {
        _instantModeTickbox = null;
        _originalFastModeTickbox = null;

        var content = settingsScreen.GetNode<NSettingsPanel>("%GeneralSettings").Content;
        var fastModeLine = content.GetNodeOrNull("FastMode");
        if (fastModeLine == null)
        {
            GD.PrintErr("[STS2 MCP] Could not find FastMode settings line");
            return;
        }

        _originalFastModeTickbox = FindChildOfType<NFastModeTickbox>(fastModeLine);

        // Duplicate the entire FastMode line and rename
        var instantLine = (Node)fastModeLine.Duplicate();
        instantLine.Name = "InstantMode";

        // Insert right after FastMode in the VBoxContainer
        int idx = fastModeLine.GetIndex();
        content.AddChild(instantLine);
        content.MoveChild(instantLine, idx + 1);

        // Update label text
        var label = instantLine.GetNodeOrNull<MegaRichTextLabel>("Label");
        if (label != null) label.Text = "Instant Mode";

        // Store reference and correct initial tick state
        // (NFastModeTickbox._Ready() already ran via AddChild, but used original SetFromSettings logic)
        _instantModeTickbox = FindChildOfType<NFastModeTickbox>(instantLine);
        if (_instantModeTickbox != null)
            _instantModeTickbox.IsTicked = _instantModeEnabled;

        GD.Print("[STS2 MCP] Instant Mode checkbox injected into settings");
    }

    // --- Harmony Patches ---

    [HarmonyPatch(typeof(NSettingsScreen), "_Ready")]
    static class SettingsScreenReadyPatch
    {
        static void Postfix(NSettingsScreen __instance)
        {
            try { InjectInstantModeCheckbox(__instance); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Settings UI injection failed: {ex}"); }
        }
    }

    [HarmonyPatch(typeof(NFastModeTickbox), nameof(NFastModeTickbox.SetFromSettings))]
    static class FastModeSetFromSettingsPatch
    {
        static bool Prefix(NFastModeTickbox __instance)
        {
            if (!IsInInstantModeLine(__instance)) return true;

            // "Reset gameplay settings" writes FastMode = Normal and then refreshes every
            // resettable node. Nothing else produces Normal while Instant Mode is on (the
            // reconciler holds Instant in singleplayer and Fast in multiplayer), so treat it
            // as the reset it is and drop the preference too.
            if (_instantModeEnabled && SaveManager.Instance.PrefsSave?.FastMode == FastModeType.Normal)
                SetInstantModeEnabled(false);

            // Reflects the stored preference, not the live game setting: in a multiplayer run
            // the setting is suppressed while the preference itself stays on.
            __instance.IsTicked = _instantModeEnabled;
            return false;
        }
    }

    [HarmonyPatch(typeof(NFastModeTickbox), "OnTick")]
    static class FastModeOnTickPatch
    {
        static bool Prefix(NFastModeTickbox __instance)
        {
            if (!IsInInstantModeLine(__instance)) return true;
            // Instant Mode turned on. It implies Fast Mode — which is also what the player
            // gets during a multiplayer run, where Instant itself is suppressed.
            if (SaveManager.Instance.PrefsSave.FastMode == FastModeType.Normal)
                SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
            SetInstantModeEnabled(true);
            // Ensure the original Fast Mode checkbox also shows ticked
            if (_originalFastModeTickbox != null && !_originalFastModeTickbox.IsTicked)
                _originalFastModeTickbox.IsTicked = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(NFastModeTickbox), "OnUntick")]
    static class FastModeOnUntickPatch
    {
        static bool Prefix(NFastModeTickbox __instance)
        {
            if (!IsInInstantModeLine(__instance)) return true;
            // Instant Mode turned off - fall back to Fast
            SetInstantModeEnabled(false);
            return false;
        }

        static void Postfix(NFastModeTickbox __instance)
        {
            // Only for the ORIGINAL Fast Mode tickbox being unticked (→ Normal).
            // Turning Fast Mode off turns Instant Mode off with it, otherwise the reconciler
            // would immediately drag the setting back up to Instant.
            if (IsInInstantModeLine(__instance)) return;
            if (_instantModeTickbox != null && _instantModeTickbox.IsTicked)
                _instantModeTickbox.IsTicked = false;
            if (_instantModeEnabled)
                SetInstantModeEnabled(false);
        }
    }

    [HarmonyPatch(typeof(NFastModeHoverTip), "_Ready")]
    static class InstantModeHoverTipPatch
    {
        static void Postfix(NFastModeHoverTip __instance)
        {
            if (!IsInInstantModeLine(__instance)) return;

            // Build a HoverTip with custom strings via reflection
            // (HoverTip is a record struct; Title/Description have private setters)
            object boxed = (object)default(HoverTip);
            var ht = typeof(HoverTip);
            ht.GetProperty("Id")!.SetValue(boxed, "sts2mcp_instant_mode");
            ht.GetProperty("Title")!.GetSetMethod(true)!
                .Invoke(boxed, ["Instant Mode"]);
            ht.GetProperty("Description")!.GetSetMethod(true)!
                .Invoke(boxed, [
                    "This option is added by the STS2MCP mod.\n" +
                    "When enabled, most in-game animations will become instant.\n" +
                    "Can reduce token usage as get-state calls will no longer receive an intermediate state.\n" +
                    "Applies to singleplayer only — it is suspended during multiplayer runs.\n" +
                    "The setting is remembered between sessions."
                ]);

            // Replace the _hoverTip field set by the original _Ready
            typeof(NFastModeHoverTip)
                .GetField("_hoverTip", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(__instance, (IHoverTip)boxed);
        }
    }
}
