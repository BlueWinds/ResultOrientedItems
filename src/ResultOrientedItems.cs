using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json;
using BattleTech;
using BattleTech.UI;
using Harmony;
using IRBTModUtils.Logging;
using HBS;

namespace ResultOrientedItems
{
    public class ItemEvent {
        public string Item = null;
        public bool AllowInInventory = false;
        public RequirementDef[] Requirements;
        public SimGameEventResult[] Results;
    }

    public class ROI {
        internal static DeferringLogger modLog;
        public static Dictionary<string, ItemEvent> itemEvents = new Dictionary<string, ItemEvent>();

        public static void Init(string modDir, string settingsJSON) {
            modLog = new DeferringLogger(modDir, "ResultOrientedItems", "ROI", true, true);
            modLog.Debug?.Write($"Initializing itemEvents:");
            foreach (string path in Directory.GetFiles($"{modDir}/itemEvents")) {
                try {
                    using (StreamReader eventReader = new StreamReader(path)) {
                        ItemEvent itemEvent = JsonConvert.DeserializeObject<ItemEvent>(eventReader.ReadToEnd());
                        itemEvents[itemEvent.Item] = itemEvent;
                        modLog.Debug?.Write($"    {path}: {itemEvent.Item}");
                    }
                } catch (Exception e) {
                    modLog.Error?.Write($"Error processing {path}");
                    modLog.Error?.Write(e);
                }
            }

            var harmony = HarmonyInstance.Create("blue.winds.ResultOrientedItems");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    public static class ROI_Util {
        public const string RefreshShopsTag = "ROI_refresh_shops";
        public const string RefreshContractsTag = "ROI_refresh_contracts";

        public static List<ItemCollectionResult> PendingCollectionResults = new List<ItemCollectionResult>();

        public static void ProcessResult(ItemCollectionResult result)
        {
            ROI_Util.PendingCollectionResults.Add(result);
            ROI.modLog.Info?.Write($"Adding results from {result?.itemCollectionID} to pending collection. Count is now {PendingCollectionResults.Count}.");
        }
    }

    [HarmonyPatch(typeof(SimGameState), "ApplySimGameEventResult", new Type[] {typeof(SimGameEventResult), typeof(List<object>), typeof(SimGameEventTracker)})]
    public static class SimGameState_ApplySimGameEventResult {
        public static void Postfix(SimGameEventResult result) {
            try
            {
                SimGameState sim = UnityGameInstance.BattleTechGame.Simulation;
                if (result.Scope == EventScope.Company && result.AddedTags != null)
                {
                    foreach (string tag in result.AddedTags.ToList())
                    {
                        if (tag == ROI_Util.RefreshShopsTag)
                        {
                            sim.CurSystem.RefreshShops();
                            sim.CompanyTags.Remove(tag);
                        }
                        else if (tag == ROI_Util.RefreshContractsTag)
                        {
                            sim.CurSystem.ResetContracts();
                            sim.GeneratePotentialContracts(true, null, sim.CurSystem, false);
                            sim.CompanyTags.Remove(tag);
                        }
                    }
                }
            }
            catch (Exception e) {
                ROI.modLog.Error?.Write(e);
            }
        }
    }

    [HarmonyPatch(typeof(SimGameState), "AddItemStat", new Type[] {typeof(string), typeof(Type), typeof(bool)})]
    public static class SimGameState_AddItemStat_Patch {
        public static bool Prefix(SimGameState __instance, string id, Type type, bool damaged) {
            try {
                if (ROI.itemEvents.ContainsKey(id)) {
                    ItemEvent itemEvent = ROI.itemEvents[id];

                    ROI.modLog.Info?.Write($"Found itemEvent for {id}. AllowInInventory: {itemEvent.AllowInInventory}");
                    if (__instance.MeetsRequirements(itemEvent.Requirements)) {
                        ROI.modLog.Info?.Write($"Applying Results");
                        SimGameState.ApplySimGameEventResult(itemEvent.Results.ToList());
                    }
                    return itemEvent.AllowInInventory;
                }
            } catch (Exception e) {
                ROI.modLog.Error?.Write(e);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(SimGameState), "AddItemStat", new Type[] {typeof(string), typeof(string), typeof(bool)})]
    public static class SimGameState_AddItemStat2_Patch {
        public static bool Prefix(SimGameState __instance, string id, string type, bool damaged) {
            try {
                if (ROI.itemEvents.ContainsKey(id)) {
                    ItemEvent itemEvent = ROI.itemEvents[id];

                    ROI.modLog.Info?.Write($"Found itemEvent for {id}. AllowInInventory: {itemEvent.AllowInInventory}");
                    if (__instance.MeetsRequirements(itemEvent.Requirements)) {
                        ROI.modLog.Info?.Write($"Applying Results");
                        SimGameState.ApplySimGameEventResult(itemEvent.Results.ToList());
                    }
                    return itemEvent.AllowInInventory;
                }
            } catch (Exception e) {
                ROI.modLog.Error?.Write(e);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(SimGameState), "SetSimGameStat")]
    public static class SimGameState_SetSimGameStat_Patch {
        public static void Prefix(ref SimGameStat stat) {
            try {
                if (stat.name == "Reputation.Owner") {
                    SimGameState sim = SceneSingletonBehavior<UnityGameInstance>.Instance.Game.Simulation;
                    FactionValue owner = sim.CurSystem.OwnerValue;
                    if (!owner.DoesGainReputation) {
                        stat.value = "0";
                    }
                    stat.name = $"Reputation.{owner.Name}";
                    ROI.modLog.Info?.Write($"Changed Reputation.Owner to {stat.name} (DoesGainReputation: {owner.DoesGainReputation}, value: {stat.value})");
                }
            } catch (Exception e) {
                ROI.modLog.Error?.Write(e);
            }
        }
    }

    [HarmonyPatch(typeof(SimGameInterruptManager), "AddInterrupt")]
    public static class SimGameInterruptManager_Entry_AddInterrupt
    {
        public static bool Prefix(SimGameInterruptManager __instance, SimGameInterruptManager.Entry entry, List<SimGameInterruptManager.Entry> ___popups, SimGameInterruptManager.Entry ___curPopup, bool playImmediate = true)
        {
            try
            {
                if (entry is SimGameInterruptManager.RewardsPopupEntry rewardsEntry)
                {
                    if (___popups.All(x => x.type != SimGameInterruptManager.InterruptType.RewardsPopup) && ___curPopup.type != SimGameInterruptManager.InterruptType.RewardsPopup)
                    {
                        return true;
                    }

                    var collectionId = rewardsEntry.parameters[0] as string;
                    __instance.Sim.RequestItem<ItemCollectionDef>(collectionId, null,
                        BattleTechResourceType.ItemCollectionDef);
                    __instance.Sim.DataManager.ItemCollectionDefs.TryGet(collectionId, out var collection);
                    var result = __instance.Sim.ItemCollectionResultGen.GenerateItemCollection(collection, 0,
                        new Action<ItemCollectionResult>(ROI_Util.ProcessResult), null);
                    ROI.modLog.Info?.Write(
                        $"Created temporary result from {result?.itemCollectionID} and {result?.items.Count} items");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                ROI.modLog.Error?.Write(e);
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(RewardsPopup), "OnItemsCollected")]
    public static class RewardsPopup_OnItemsCollected
    {
        public static void Prefix(RewardsPopup __instance, ItemCollectionResult result)
        {
            try
            {
                foreach (var pendingCollection in ROI_Util.PendingCollectionResults)
                {
                    result.items.AddRange(pendingCollection.items);
                    ROI.modLog.Info?.Write(
                        $"Added result from state {pendingCollection.itemCollectionID}_{pendingCollection.GUID} to result from {result.itemCollectionID}_{result.GUID}");
                }

                ROI_Util.PendingCollectionResults = new List<ItemCollectionResult>();
            }
            catch (Exception e)
            {
                ROI.modLog.Error?.Write(e);
            }
        }
    }
}
