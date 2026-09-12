using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Settings;

namespace STS2_MCP;

public static partial class McpMod
{
    /// <summary>
    /// Stands in for the Punch Off event's ambient punching animation while
    /// <see cref="FastModeType.Instant" /> is active, because under Instant the game's own
    /// loop never yields and hangs the process.
    ///
    /// <para><c>PunchOff.PunchEachOther</c> is an endless decorative loop — two constructs
    /// trade punches for as long as the event screen is up — and every await in its body is
    /// a no-op under Instant: <c>Cmd.Wait</c> guards its whole body on
    /// <c>FastMode != Instant</c>, and <c>CreatureCmd.TriggerAnim(.., 0f)</c> delegates to
    /// <c>Cmd.CustomScaledWait</c>, which has the same guard. With nothing left to suspend
    /// on, the <c>while</c> runs to completion inside the state machine's first
    /// <c>AsyncMethodBuilderCore.Start</c> and never returns to the engine. The main thread
    /// stops servicing frames the instant the event opens — so our own HTTP reads time out —
    /// while each iteration instantiates an <c>NHitSparkVfx</c> (three <c>GpuParticles2D</c>)
    /// into the VFX container that can never be freed, since <c>FlashAndFree</c> waits on a
    /// particle signal that needs a frame. Within seconds the renderer's particle RID pool is
    /// exhausted (<c>ERROR: Element limit reached.</c>) and every later instantiate emits ~20
    /// engine errors with a full C# backtrace apiece: 1.88 GB of godot.log in three and a half
    /// minutes, observed live, with node and memory use climbing until the process is killed.
    /// </para>
    ///
    /// <para>Skipping the loop is what Instant Mode means everywhere else — it is an
    /// animation, and Instant's whole contract is that animations do not play; the game
    /// simply never gave this one an Instant path. Nothing in the loop touches run state:
    /// it mirrors one sprite's scale and spawns hit sparks, and the event's options, curse
    /// and rewards all live elsewhere. <c>_punchCts</c> is created by the caller and both
    /// <c>TakeThem</c> and <c>OnRoomExited</c> cancel it null-safely, so leaving it
    /// uncancelled is the same state the running loop would have been in.</para>
    ///
    /// <para>The patch is inert at Normal and Fast speed, where a human is watching and the
    /// loop behaves, and in multiplayer, where <see cref="ReconcileInstantMode" /> keeps
    /// Instant out of the prefs anyway.</para>
    /// </summary>
    [HarmonyPatch]
    private static class PunchOffInstantHangPatch
    {
        // Resolved by name so a rename upstream leaves this one patch unapplied rather than
        // throwing out of PatchAll, which would take every other patch in the mod with it.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var method = AccessTools.DeclaredMethod(typeof(PunchOff), "PunchEachOther");
            if (method == null)
            {
                GD.Print("[STS2 MCP] PunchOff.PunchEachOther not found - Instant Mode hang guard inactive");
                yield break;
            }

            yield return method;
        }

        private static bool Prefix(ref Task __result)
        {
            if (!InstantModeIsActive()) return true;

            __result = Task.CompletedTask;
            return false;
        }
    }
}
