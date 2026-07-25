using HarmonyLib;
using SpaceCraft;
using UnityEngine;

namespace ATLAS
{
    /// <summary>
    /// Quitting to the intro scene ends a world but not the process, so one LogOutput.log
    /// can span several save loads. Rolling the archive here makes each archived file map
    /// to exactly one world, which is what you want when reading a bug report.
    /// </summary>
    [HarmonyPatch(typeof(GameExitController))]
    internal static class SessionBoundaryPatch
    {
        private static float _lastRoll = -999f;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(GameExitController.QuitCurrentGame))]
        private static void QuitCurrentGame_Postfix()
        {
            // QuitCurrentGame early-returns when InProgress is already set, and a postfix
            // still runs on that path, so guard against a double roll.
            if (Time.realtimeSinceStartup - _lastRoll < 2f) return;
            _lastRoll = Time.realtimeSinceStartup;

            Plugin.Instance?.RollSession("returned to menu");
        }
    }
}
