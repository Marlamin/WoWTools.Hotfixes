using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WoWTools.WDBUpdater
{
    class Program
    {
        public struct wdbCache
        {
            public string identifier;
            public uint clientBuild;
            public string clientLocale;
            public uint recordSize;
            public uint recordVersion;
            public uint formatVersion;
            public Build buildInfo;
            public Dictionary<uint, Dictionary<string, string>> entries;
        }

        public struct Build
        {
            public string version;
            public short expansion;
            public short major;
            public short minor;
            public int build;
        }

        static void Main(string[] args)
        {
            Build targetBuild = new Build
            {
                version = "10.2.0.51521",
                expansion = 10,
                major = 2,
                minor = 0,
                build = 51521
            };

            if (args.Length == 0)
            {
                Console.WriteLine("Arguments: <wdbpath> (optional build in x.x.x.xxxxx format, uses " + targetBuild.version + " by default)");
                return;
            }

            if (args.Length == 2)
            {
                var splitBuild = args[1].Trim().Split('.');
                if (splitBuild.Length == 4)
                {
                    targetBuild.version = args[1].Trim();
                    targetBuild.expansion = short.Parse(splitBuild[0]);
                    targetBuild.major = short.Parse(splitBuild[1]);
                    targetBuild.minor = short.Parse(splitBuild[2]);
                    targetBuild.build = int.Parse(splitBuild[3]);
                }
                else
                {
                    throw new Exception("Build must be in x.x.x.xxxxx format");
                }
            }

            var wdb = new wdbCache();

            using (var ms = new MemoryStream(File.ReadAllBytes(args[0])))
            using (var bin = new BinaryReader(ms))
            {
                wdb.identifier = Encoding.ASCII.GetString(bin.ReadBytes(4).Reverse().ToArray());
                wdb.clientBuild = bin.ReadUInt32();
                wdb.clientLocale = Encoding.ASCII.GetString(bin.ReadBytes(4).Reverse().ToArray());
                wdb.recordSize = bin.ReadUInt32();
                wdb.recordVersion = bin.ReadUInt32();
                wdb.formatVersion = bin.ReadUInt32();
                wdb.buildInfo = targetBuild;

                var humanReadableJsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };

                if (wdb.clientLocale != "enUS" && wdb.clientLocale != "enGB")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new Dictionary<uint, Dictionary<string, string>>(), humanReadableJsonOptions));
                    return;
                }

                // All WDB structures below were originally based on Simca's excellent 010 template.
                switch (wdb.identifier)
                {
                    case "WMOB": // Creature
                        wdb.entries = ReadCreatureEntries(bin, wdb);
                        break;
                    case "WGOB": // Gameobject
                        wdb.entries = ReadGameObjectEntries(bin, wdb);
                        break;
                    case "WPTX": // PageText
                        wdb.entries = ReadPageTextEntries(bin);
                        break;
                    case "WQST": // Quest
                        wdb.entries = ReadQuestEntries(bin, wdb);
                        break;
                    case "WNPC": // NPC
                    case "WPTN": // Petition
                        break;
                    default:
                        Console.WriteLine("Unknown cache file: " + wdb.identifier);
                        break;
                }

                var storageJsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                };

                Console.WriteLine(JsonSerializer.Serialize(wdb.entries.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value), humanReadableJsonOptions));
            }
        }

        private static Dictionary<uint, Dictionary<string, string>> ReadQuestEntries(BinaryReader bin, wdbCache wdb)
        {
            var entries = new Dictionary<uint, Dictionary<string, string>>();

            if (wdb.buildInfo.expansion == 1 || wdb.buildInfo.expansion == 2 || wdb.buildInfo.expansion == 3)
            {
                return entries;
            }

            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                var posPreread = bin.BaseStream.Position;

                entries.Add(id, new Dictionary<string, string>());
                entries[id].Add("QuestID", bin.ReadUInt32().ToString());
                entries[id].Add("QuestType", bin.ReadUInt32().ToString());

                if (wdb.recordVersion <= 12 && wdb.buildInfo.expansion < 9)
                {
                    // Removed in 9.0.1.33978 - without a RecordVersion change
                    entries[id].Add("QuestLevel", bin.ReadUInt32().ToString());
                }

                if (wdb.recordVersion <= 12 && wdb.buildInfo.expansion < 9)
                {
                    // Removed in 9.0.1.33978 - without a RecordVersion change
                    entries[id].Add("QuestMaxScalingLevel", bin.ReadUInt32().ToString());
                }

                entries[id].Add("QuestPackageID", bin.ReadUInt32().ToString());

                if (wdb.recordVersion >= 11)
                {
                    entries[id].Add("ContentTuningID", bin.ReadUInt32().ToString());
                }

                if (wdb.recordVersion <= 12 && wdb.buildInfo.expansion < 9)
                {
                    // Removed in 9.0.1.33978 - without a RecordVersion change
                    entries[id].Add("QuestMinLevel", bin.ReadUInt32().ToString());
                }

                // If negative, index into QuestSortID, if positive index into AreaTable
                var questSortOrAreaTableID = bin.ReadInt32();
                if (questSortOrAreaTableID < 0)
                {
                    entries[id].Add("QuestSortID", Math.Abs(questSortOrAreaTableID).ToString());
                }
                else
                {
                    entries[id].Add("AreaTableID", questSortOrAreaTableID.ToString());
                }

                entries[id].Add("QuestInfoID", bin.ReadUInt32().ToString());
                entries[id].Add("SuggestedGroupNum", bin.ReadUInt32().ToString());
                entries[id].Add("RewardNextQuest", bin.ReadUInt32().ToString());
                entries[id].Add("RewardXPDifficulty", bin.ReadUInt32().ToString());
                entries[id].Add("RewardXPMultiplier", bin.ReadSingle().ToString());
                entries[id].Add("RewardMoney", bin.ReadUInt32().ToString());
                entries[id].Add("RewardMoneyDifficulty", bin.ReadUInt32().ToString());
                entries[id].Add("RewardMoneyMultiplier", bin.ReadSingle().ToString());
                entries[id].Add("RewardBonusMoney", bin.ReadUInt32().ToString());

                uint rewardDisplaySpellCount = 0;
                if (wdb.clientBuild >= 35078 && wdb.buildInfo.expansion >= 9)
                {
                    rewardDisplaySpellCount = bin.ReadUInt32();
                    entries[id].Add("RewardDisplaySpellCount", rewardDisplaySpellCount.ToString());
                }
                else
                {
                    for (var i = 0; i < 3; i++)
                    {
                        entries[id].Add("RewardDisplaySpell[" + i + "]", bin.ReadUInt32().ToString());
                    }
                }

                entries[id].Add("RewardSpell", bin.ReadUInt32().ToString());
                entries[id].Add("RewardHonorAddition", bin.ReadUInt32().ToString());
                entries[id].Add("RewardHonorMultiplier", bin.ReadSingle().ToString());
                entries[id].Add("RewardArtifactXPDifficulty", bin.ReadUInt32().ToString());
                entries[id].Add("RewardArtifactXPMultiplier", bin.ReadSingle().ToString());
                entries[id].Add("RewardArtifactCategoryID", bin.ReadUInt32().ToString());
                entries[id].Add("ProvidedItem", bin.ReadUInt32().ToString());

                for (var i = 0; i < 3; i++)
                {
                    entries[id].Add("Flags[" + i + "]", bin.ReadUInt32().ToString());
                }

                for (var i = 0; i < 4; i++)
                {
                    entries[id].Add("RewardFixedItemID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("RewardFixedItemQuantity[" + i + "]", bin.ReadUInt32().ToString());
                }

                for (var i = 0; i < 4; i++)
                {
                    entries[id].Add("ItemDropItemID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ItemDropItemQuantity[" + i + "]", bin.ReadUInt32().ToString());
                }

                for (var i = 0; i < 6; i++)
                {
                    entries[id].Add("RewardChoiceItemItemID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("RewardChoiceItemItemQuantity[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("RewardChoiceItemItemDisplayID[" + i + "]", bin.ReadUInt32().ToString());
                }

                entries[id].Add("POIContinent", bin.ReadUInt32().ToString());
                entries[id].Add("POIx", bin.ReadSingle().ToString());
                entries[id].Add("POIy", bin.ReadSingle().ToString());
                entries[id].Add("POIPriority", bin.ReadUInt32().ToString());
                entries[id].Add("RewardTitle", bin.ReadUInt32().ToString());
                entries[id].Add("RewardArenaPoints", bin.ReadUInt32().ToString());
                entries[id].Add("RewardSkillLineID", bin.ReadUInt32().ToString());
                entries[id].Add("RewardNumSkillUps", bin.ReadUInt32().ToString());
                entries[id].Add("PortraitGiverDisplayID", bin.ReadUInt32().ToString());
                entries[id].Add("PortraitGiverMountDisplayID", bin.ReadUInt32().ToString());
                entries[id].Add("PortraitTurnInDisplayID", bin.ReadUInt32().ToString());

                if ((wdb.buildInfo.expansion >= 9 && wdb.buildInfo.major >= 1) || wdb.buildInfo.expansion >= 10)
                {
                    entries[id].Add("PortraitModelSceneID", bin.ReadUInt32().ToString());
                }

                for (var i = 0; i < 5; i++)
                {
                    entries[id].Add("FactionID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("FactionValue[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("FactionOverride[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("FactionGainMaxRank[" + i + "]", bin.ReadUInt32().ToString());
                }

                entries[id].Add("RewardFactionFlags", bin.ReadUInt32().ToString());

                for (var i = 0; i < 4; i++)
                {
                    entries[id].Add("RewardCurrencyID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("RewardCurrencyQuantity[" + i + "]", bin.ReadUInt32().ToString());
                }

                entries[id].Add("AcceptedSoundKitID", bin.ReadUInt32().ToString());
                entries[id].Add("CompleteSoundKitID", bin.ReadUInt32().ToString());
                entries[id].Add("AreaGroupID", bin.ReadUInt32().ToString());

                if (wdb.buildInfo.expansion >= 10 && wdb.clientBuild >= 49516)
                    entries[id].Add("TimeAllowed", bin.ReadUInt64().ToString());
                else
                    entries[id].Add("TimeAllowed", bin.ReadUInt32().ToString());

                var numObjectives = bin.ReadUInt32();
                entries[id].Add("NumObjectives", numObjectives.ToString());

                entries[id].Add("RaceFlags", bin.ReadUInt64().ToString());
                entries[id].Add("QuestRewardID", bin.ReadInt32().ToString());
                entries[id].Add("ExpansionID", bin.ReadUInt32().ToString());

                if (wdb.recordVersion > 11)
                {
                    entries[id].Add("ManagedWorldStateID", bin.ReadUInt32().ToString());
                    entries[id].Add("QuestSessionBonus", bin.ReadUInt32().ToString());
                }

                uint numConditionalQuestDescription = 0;
                uint numConditionalQuestCompletion = 0;
                if (wdb.buildInfo.expansion >= 10)
                {
                    entries[id].Add("QuestGiverCreatureID", bin.ReadUInt32().ToString());
                    numConditionalQuestDescription = bin.ReadUInt32();
                    numConditionalQuestCompletion = bin.ReadUInt32();
                }

                if (wdb.clientBuild >= 35078 && wdb.buildInfo.expansion >= 9)
                {
                    for (var i = 0; i < rewardDisplaySpellCount; i++)
                    {
                        entries[id].Add("RewardDisplaySpellID[" + i + "]", bin.ReadUInt32().ToString());
                        entries[id].Add("RewardDisplayPlayerConditionID[" + i + "]", bin.ReadUInt32().ToString());
                        if (wdb.buildInfo.expansion >= 10 && wdb.clientBuild >= 49039)
                        {
                            entries[id].Add("RewardDisplaySpellType[" + i + "]", bin.ReadUInt32().ToString());
                        }
                    }
                }

                var ds = new DataStore(bin);

                var LogTitleLength = ds.GetIntByBits(9);
                var LogDescriptionLength = ds.GetIntByBits(12);
                var QuestDescriptionLength = ds.GetIntByBits(12);
                var AreaDescriptionLength = ds.GetIntByBits(9);
                var PortraitGiverTextLength = ds.GetIntByBits(10);
                var PortraitGiverNameLength = ds.GetIntByBits(8);
                var PortraitTurnInTextLength = ds.GetIntByBits(10);
                var PortraitTurnInNameLength = ds.GetIntByBits(8);
                var QuestCompletionLogLength = ds.GetIntByBits(11);

                if (wdb.buildInfo.expansion >= 10)
                    entries[id].Add("ReadyForTranslation", ds.GetBool().ToString());

                ds.Flush();

                for (var i = 0; i < numObjectives; i++)
                {
                    entries[id].Add("ObjectiveID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ObjectiveType[" + i + "]", bin.ReadByte().ToString());
                    entries[id].Add("ObjectiveStorageIndex[" + i + "]", bin.ReadByte().ToString());
                    entries[id].Add("ObjectiveObjectID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ObjectiveAmount[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ObjectiveFlags[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ObjectiveFlags2[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ObjectivePercentAmount[" + i + "]", bin.ReadSingle().ToString());

                    var numVisualEffects = bin.ReadUInt32();
                    entries[id].Add("ObjectiveNumVisualEffects[" + i + "]", numVisualEffects.ToString());

                    for (var j = 0; j < numVisualEffects; j++)
                    {
                        entries[id].Add("ObjectiveVisualEffects[" + i + "][" + j + "]", bin.ReadUInt32().ToString());
                    }

                    var descriptionLength = bin.ReadByte();
                    entries[id].Add("ObjectiveDescription[" + i + "]", ds.GetString(descriptionLength).Trim('\0'));
                }

                entries[id].Add("LogTitle", ds.GetString(LogTitleLength).Trim('\0'));
                entries[id].Add("LogDescription", ds.GetString(LogDescriptionLength).Trim('\0'));
                entries[id].Add("QuestDescription", ds.GetString(QuestDescriptionLength).Trim('\0'));
                entries[id].Add("AreaDescription", ds.GetString(AreaDescriptionLength).Trim('\0'));
                entries[id].Add("PortraitGiverText", ds.GetString(PortraitGiverTextLength).Trim('\0'));
                entries[id].Add("PortraitGiverName", ds.GetString(PortraitGiverNameLength).Trim('\0'));
                entries[id].Add("PortraitTurnInText", ds.GetString(PortraitTurnInTextLength).Trim('\0'));
                entries[id].Add("PortraitTurnInName", ds.GetString(PortraitTurnInNameLength).Trim('\0'));
                entries[id].Add("QuestCompletionLog", ds.GetString(QuestCompletionLogLength).Trim('\0'));
                ds.Flush();

                for (var i = 0; i < numConditionalQuestDescription; i++)
                {
                    entries[id].Add("ConditionalQuestDescPlayerConditionID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ConditionalQuestDescQuestGiverCreatureID[" + i + "]", bin.ReadUInt32().ToString());
                    var conditionalQuestDescLength = ds.GetIntByBits(12);
                    ds.Flush();
                    entries[id].Add("ConditionalQuestDesc[" + i + "]", ds.GetString(conditionalQuestDescLength).Trim('\0'));
                }

                for (var i = 0; i < numConditionalQuestCompletion; i++)
                {
                    entries[id].Add("ConditionalQuestComplPlayerConditionID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("ConditionalQuestComplQuestGiverCreatureID[" + i + "]", bin.ReadUInt32().ToString());
                    var conditionalQuestDescLength = ds.GetIntByBits(12);
                    ds.Flush();
                    entries[id].Add("ConditionalQuestCompl[" + i + "]", ds.GetString(conditionalQuestDescLength).Trim('\0'));
                }

                if (bin.BaseStream.Position != posPreread + length)
                {
                    if (bin.BaseStream.Position > posPreread + length)
                    {
                        throw new Exception("Quest " + id + " overshot reading, stopping");
                    }
                    else
                    {
                        Console.WriteLine("[Quest ID " + id + "] Should be at position " + (posPreread + length) + " but am at " + bin.BaseStream.Position + " instead, fixing");
                        bin.BaseStream.Position = posPreread + length;
                    }
                }
            }

            return entries;
        }

        private static Dictionary<uint, Dictionary<string, string>> ReadCreatureEntries(BinaryReader bin, wdbCache wdb)
        {
            var entries = new Dictionary<uint, Dictionary<string, string>>();

            if (wdb.buildInfo.expansion == 1 || wdb.buildInfo.expansion == 2 || wdb.buildInfo.expansion == 3)
            {
                return entries;
            }

            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                entries.Add(id, new Dictionary<string, string>());

                var ds = new DataStore(bin);

                var TitleLength = ds.GetIntByBits(11);
                var TitleAltLength = ds.GetIntByBits(11);
                var CursorNameLength = ds.GetIntByBits(6);
                var Leader = ds.GetBool();
                var Name0Length = ds.GetIntByBits(11);
                var NameAlt0Length = ds.GetIntByBits(11);
                var Name1Length = ds.GetIntByBits(11);
                var NameAlt1Length = ds.GetIntByBits(11);
                var Name2Length = ds.GetIntByBits(11);
                var NameAlt2Length = ds.GetIntByBits(11);
                var Name3Length = ds.GetIntByBits(11);
                var NameAlt3Length = ds.GetIntByBits(11);

                entries[id].Add("Name[0]", ds.GetString(Name0Length).Trim('\0'));
                entries[id].Add("NameAlt[0]", ds.GetString(NameAlt0Length).Trim('\0'));
                entries[id].Add("Name[1]", ds.GetString(Name1Length).Trim('\0'));
                entries[id].Add("NameAlt[1]", ds.GetString(NameAlt1Length).Trim('\0'));
                entries[id].Add("Name[2]", ds.GetString(Name2Length).Trim('\0'));
                entries[id].Add("NameAlt[2]", ds.GetString(NameAlt2Length).Trim('\0'));
                entries[id].Add("Name[3]", ds.GetString(Name3Length).Trim('\0'));
                entries[id].Add("NameAlt[3]", ds.GetString(NameAlt3Length).Trim('\0'));

                entries[id].Add("Flags[0]", bin.ReadUInt32().ToString());
                entries[id].Add("Flags[1]", bin.ReadUInt32().ToString());

                entries[id].Add("CreatureType", bin.ReadUInt32().ToString());
                entries[id].Add("CreatureFamily", bin.ReadUInt32().ToString());
                entries[id].Add("Classification", bin.ReadUInt32().ToString());
                entries[id].Add("ProxyCreatureID[0]", bin.ReadUInt32().ToString());
                entries[id].Add("ProxyCreatureID[1]", bin.ReadUInt32().ToString());

                var numCreatureDisplays = bin.ReadUInt32();
                entries[id].Add("NumCreatureDisplays", numCreatureDisplays.ToString());
                entries[id].Add("TotalProbability", bin.ReadSingle().ToString());

                for (var i = 0; i < numCreatureDisplays; i++)
                {
                    entries[id].Add("CreatureDisplayInfoID[" + i + "]", bin.ReadUInt32().ToString());
                    entries[id].Add("CreatureScale[" + i + "]", bin.ReadSingle().ToString());
                    entries[id].Add("CreatureProbability[" + i + "]", bin.ReadSingle().ToString());
                }

                entries[id].Add("HPMultiplier", bin.ReadSingle().ToString());
                entries[id].Add("EnergyMultiplier", bin.ReadSingle().ToString());

                var numQuestItems = bin.ReadUInt32();
                entries[id].Add("NumQuestItems", numQuestItems.ToString());

                entries[id].Add("CreatureMovementInfoID", bin.ReadInt32().ToString());
                entries[id].Add("RequiredExpansion", bin.ReadUInt32().ToString());
                entries[id].Add("TrackingQuestID", bin.ReadUInt32().ToString());
                entries[id].Add("VignetteID", bin.ReadUInt32().ToString());
                entries[id].Add("CreatureClassMask", bin.ReadUInt32().ToString());

                if ((wdb.buildInfo.expansion >= 9 && wdb.buildInfo.major >= 1) || wdb.buildInfo.expansion >= 10)
                {
                    entries[id].Add("CreatureDifficultyID", bin.ReadUInt32().ToString());
                }

                entries[id].Add("UIWidgetParentSetID", bin.ReadUInt32().ToString());

                if (wdb.buildInfo.expansion >= 9)
                {
                    entries[id].Add("UIWidgetSetUnitConditionID", bin.ReadUInt32().ToString());
                }

                if (wdb.buildInfo.expansion == 8 && wdb.clientBuild >= 34769)
                {
                    entries[id].Add("BfA_Int_1", bin.ReadUInt32().ToString());
                    entries[id].Add("BfA_Int_2", bin.ReadUInt32().ToString());
                }

                entries[id].Add("Title", ds.GetString(TitleLength).Trim('\0'));
                entries[id].Add("TitleAlt", ds.GetString(TitleAltLength).Trim('\0'));

                if (CursorNameLength != 1)
                {
                    entries[id].Add("CursorName", ds.GetString(CursorNameLength).Trim('\0'));
                }

                for (var i = 0; i < numQuestItems; i++)
                {
                    entries[id].Add("QuestItemID[" + i + "]", bin.ReadUInt32().ToString());
                }
            }

            return entries;
        }

        private static Dictionary<uint, Dictionary<string, string>> ReadPageTextEntries(BinaryReader bin)
        {
            var entries = new Dictionary<uint, Dictionary<string, string>>();

            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                entries.Add(id, new Dictionary<string, string>());
                entries[id].Add("PageTextID", bin.ReadUInt32().ToString());
                entries[id].Add("NextPageTextID", bin.ReadUInt32().ToString());
                entries[id].Add("PlayerConditionID", bin.ReadUInt32().ToString());
                entries[id].Add("Flags", bin.ReadByte().ToString());

                var ds = new DataStore(bin);

                var TextLength = ds.GetIntByBits(12);
                entries[id].Add("TextLength", TextLength.ToString());

                entries[id].Add("Text", ds.GetString(TextLength).Trim('\0'));
            }

            return entries;
        }

        public static Dictionary<uint, Dictionary<string, string>> ReadGameObjectEntries(BinaryReader bin, wdbCache wdb)
        {
            var entries = new Dictionary<uint, Dictionary<string, string>>();

            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                var goEntry = new Dictionary<string, string>();
                goEntry.Add("Type", bin.ReadUInt32().ToString());
                goEntry.Add("GameObjectDisplayID", bin.ReadUInt32().ToString());

                var nameSize = 4;
                for (var i = 0; i < nameSize; i++)
                {
                    goEntry.Add("Name[" + i + "]", bin.ReadCString());
                }

                goEntry.Add("Icon", bin.ReadCString());
                goEntry.Add("Action", bin.ReadCString());
                goEntry.Add("Condition", bin.ReadCString());

                var gameDataSize = 34;

                // This change somes in the middle of several branches being worked on at the same time :(
                if (wdb.buildInfo.build > 40120 &&
                    wdb.buildInfo.build != 40140 &&
                    wdb.buildInfo.build != 40179 &&
                    wdb.buildInfo.build != 40203 &&
                    wdb.buildInfo.build != 40237 &&
                    wdb.buildInfo.build != 40260 &&
                    wdb.buildInfo.build != 40347 &&
                    wdb.buildInfo.build != 40422 &&
                    wdb.buildInfo.build != 40441 &&
                    wdb.buildInfo.build != 40443 &&
                    wdb.buildInfo.build != 40488 &&
                    wdb.buildInfo.build != 40593 &&
                    wdb.buildInfo.build != 40617 &&
                    wdb.buildInfo.build != 40618 &&
                    wdb.buildInfo.build != 40725 &&
                    wdb.buildInfo.build != 40892 &&
                    wdb.buildInfo.build != 41446 &&
                    wdb.buildInfo.build != 41510)
                {
                    gameDataSize = 35;
                }

                for (var i = 0; i < gameDataSize; i++)
                {
                    goEntry.Add("GameData[" + i + "]", bin.ReadUInt32().ToString());
                }

                goEntry.Add("Scale", bin.ReadSingle().ToString());

                var numQuestItems = bin.ReadByte();
                goEntry.Add("NumQuestItems", numQuestItems.ToString());

                for (var i = 0; i < numQuestItems; i++)
                {
                    goEntry.Add("QuestItems[" + i + "]", bin.ReadUInt32().ToString());
                }

                goEntry.Add("ContentTuningID", bin.ReadUInt32().ToString());

                entries.TryAdd(id, goEntry);
            }

            return entries;
        }
    }
}
