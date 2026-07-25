using HarmonyLib;
using UnityEngine.InputSystem.Controls;

namespace ATLAS
{
    /// <summary>
    /// The single hot-path patch in ATLAS, applied only when Scanner.LearnHardcodedKeys is on.
    ///
    /// A postfix here runs for every button any code asks about, so it is deliberately shaped
    /// to do nothing at all in the common case: one bool test on __result, then return. All
    /// real work - stack walking, dictionary writes - happens only when a button is actually
    /// down, which is rare relative to how often buttons are queried.
    /// </summary>
    [HarmonyPatch(typeof(ButtonControl), nameof(ButtonControl.isPressed), MethodType.Getter)]
    internal static class ButtonReadPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void IsPressed_Postfix(ButtonControl __instance, bool __result)
        {
            if (!__result) return;
            KeyObserver.OnButtonRead(__instance, true);
        }
    }
}
