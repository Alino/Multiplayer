using HarmonyLib;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using System;
using Verse;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// Harmony patches for async time desync fixes - cleanup and safety only
    /// Main fixes are now integrated directly into AsyncTimeComp
    /// </summary>
    [HarmonyPatch]
    public static class AsyncTimeDesyncFixes
    {

        /// <summary>
        /// Clean up map-specific state when maps are destroyed to prevent memory leaks
        /// </summary>
        [HarmonyPatch(typeof(Map), "FinalizeDestroy")]
        [HarmonyPostfix]
        public static void CleanupMapState_OnDestroy(Map __instance)
        {
            try
            {
                if (__instance?.uniqueID != null)
                {
                    // Clean up map pause states and execution contexts
                    AsyncTimeComp.CleanupMapState(__instance.uniqueID);
                    AsyncTimeComp.CleanupExecutionContext(__instance.uniqueID);
                    
                    MpLog.Debug($"Cleaned up async time state for destroyed map {__instance.uniqueID}");
                }
            }
            catch (Exception e)
            {
                MpLog.Error($"Error cleaning up map state for map {__instance?.uniqueID}: {e}");
            }
        }

        /// <summary>
        /// Clean up all async time state when game ends to prevent memory leaks
        /// </summary>
        [HarmonyPatch(typeof(GenScene), "GoToMainMenu")]
        [HarmonyPrefix]
        public static void CleanupAllState_OnGameEnd()
        {
            try
            {
                // Clean up all async time state when returning to main menu
                AsyncTimeComp.CleanupAllState();
                
                MpLog.Debug("Cleaned up all async time state on game end");
            }
            catch (Exception e)
            {
                MpLog.Error($"Error cleaning up all async time state on game end: {e}");
            }
        }

    }
}