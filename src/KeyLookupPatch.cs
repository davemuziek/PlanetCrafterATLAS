using HarmonyLib;
using UnityEngine.InputSystem;

namespace ATLAS
{
    /// <summary>
    /// Backup capture path for key learning.
    ///
    /// ButtonControl.isPressed is a one-line property, and Mono may inline it straight into a
    /// mod's Update. Inlining copies the original IL, so Harmony's detour on the real method
    /// never runs and the press patch silently sees nothing. Keyboard's indexer is a separate
    /// call that has to resolve the KeyControl first, so it survives that case.
    ///
    /// It also records something the press path cannot: a key a mod watches but that was never
    /// actually pressed during the session. That still tells you the mod has claimed it.
    ///
    /// Cost is controlled inside KeyObserver, which caps stack walks per frame - this fires
    /// every frame for every key a mod polls, so it must not walk on every call.
    /// </summary>
    [HarmonyPatch(typeof(Keyboard), "get_Item", typeof(Key))]
    internal static class KeyLookupPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void GetItem_Postfix(Key key)
        {
            if (key == Key.None) return;
            KeyObserver.OnKeyLookup("/Keyboard/" + key.ToString().ToLowerInvariant());
        }
    }
}
