using System;
using HarmonyLib;
using SpaceCraft;

namespace ATLAS
{
    /// <summary>
    /// Content capture point (§6.1). Postfix on StaticDataHandler.LoadStaticData - the moment the
    /// group roster has just been registered. Verified against the decompiled source: the method
    /// is `public void LoadStaticData()` and runs on EVERY save load, not only at boot, so the
    /// capture must be idempotent - DriftState.CaptureContent guards that with a once-per-session
    /// flag. Read-only: the postfix takes a snapshot and never touches the groups.
    /// </summary>
    [HarmonyPatch(typeof(StaticDataHandler), nameof(StaticDataHandler.LoadStaticData))]
    internal static class StaticDataPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try { DriftState.CaptureContent(); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("ATLAS drift content hook failed, game unaffected: " + ex.Message);
            }
        }
    }
}
