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
        public static List<ItemCollectionResult> pendingCollectionResults = new List<ItemCollectionResult>();

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

    [HarmonyPatch(typeof(SimGameState), "ApplySimGameEventResult", new Type[] {typeof(SimGameEventResult), typeof(List<object>), typeof(SimGameEventTracker)})]
    public static class SimGameState_ApplySimGameEventResult {
        public static void Postfix(SimGameEventResult result) {
            try {
                SimGameState sim = UnityGameInstance.BattleTechGame.Simulation;
                if (result.Scope == EventScope.Company && result.AddedTags != null) {
                    foreach (string tag in result.AddedTags.ToList()) {
                        if (tag == "ROI_refresh_shops") {
                            sim.CurSystem.RefreshShops();
                            sim.CompanyTags.Remove(tag);
                        } else if (tag == "ROI_refresh_contracts") {
                            sim.CurSystem.ResetContracts();
                            sim.GeneratePotentialContracts(true, null, sim.CurSystem, false);
                            sim.CompanyTags.Remove(tag);
                        }
                    }
                }
            } catch (Exception e) {
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
    public static class SimGameInterruptManager_Entry_AddInterrupt {
        public static bool Prefix(SimGameInterruptManager.Entry entry, List<SimGameInterruptManager.Entry> ___popups, SimGameInterruptManager.Entry ___curPopup) {
            try {
                if (entry is SimGameInterruptManager.RewardsPopupEntry rewardsEntry) {
                    // During career start, the SimGameInterruptManager isn't fully initialized.
                    if (___popups == null || ___curPopup == null) {
                        return true;
                    }

                    if (___popups.All(x => x.type != SimGameInterruptManager.InterruptType.RewardsPopup) && ___curPopup.type != SimGameInterruptManager.InterruptType.RewardsPopup) {
                        return true;
                    }

                    SimGameState sim = SceneSingletonBehavior<UnityGameInstance>.Instance.Game.Simulation;

                    string collectionId = rewardsEntry.parameters[0] as string;
                    sim.RequestItem<ItemCollectionDef>(collectionId, null, BattleTechResourceType.ItemCollectionDef);
                    sim.DataManager.ItemCollectionDefs.TryGet(collectionId, out var collection);

                    Action<ItemCollectionResult> action = new Action<ItemCollectionResult>(processResult);
                    ItemCollectionResult result = sim.ItemCollectionResultGen.GenerateItemCollection(collection, 0, action, null);
                    ROI.modLog.Info?.Write($"Created temp result from {result?.itemCollectionID} and {result?.items.Count} items");
                    return false;
                }
            } catch (Exception e) {
                ROI.modLog.Error?.Write(e);
            }
            return true;
        }

        public static void processResult(ItemCollectionResult result) {
            ROI.pendingCollectionResults.Add(result);
            ROI.modLog.Info?.Write($"Adding results from {result?.itemCollectionID} to pending collection. Count is now {ROI.pendingCollectionResults.Count}.");
        }
    }

    [HarmonyPatch(typeof(RewardsPopup), "OnItemsCollected")]
    public static class RewardsPopup_OnItemsCollected
    {
        public static void Prefix(RewardsPopup __instance, ItemCollectionResult result) {
            try {
                foreach (var pendingCollection in ROI.pendingCollectionResults) {
                    result.items.AddRange(pendingCollection.items);
                    ROI.modLog.Info?.Write(
                        $"Added result from state {pendingCollection.itemCollectionID}_{pendingCollection.GUID} to result from {result.itemCollectionID}_{result.GUID}");
                }

                ROI.pendingCollectionResults.Clear();
            } catch (Exception e) {
                ROI.modLog.Error?.Write(e);
            }
        }
    }
}
