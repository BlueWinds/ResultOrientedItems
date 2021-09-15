using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json;
using BattleTech;
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
}
