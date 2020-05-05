using MySql.Data.MySqlClient;
using System;
using System.Collections;
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
            public Dictionary<string, Dictionary<string, string>> entries;
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                throw new Exception("Arguments: <wdbpath> (json (default)/mysql)");
            }

            var outputType = "json";
            if(args.Length == 2)
            {
                if(args[1].ToLower() == "mysql")
                {
                    outputType = "mysql";
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
                
                // All WDB structures below are based on Simca's excellent 010 template.

                switch (wdb.identifier)
                {
                    case "WMOB": // Creature
                        wdb.entries = ReadCreatureEntries(bin);
                        break;
                    case "WGOB": // Gameobject
                        wdb.entries = ReadGameObjectEntries(bin);
                        break;
                    case "WPTX": // PageText
                        wdb.entries = ReadPageTextEntries(bin);
                        break;
                    case "WQST": // Quest
                        wdb.entries = ReadQuestEntries(bin, wdb);
                        break;
                    case "WNPC": // NPC
                    case "WPTN": // Petition
                        Console.WriteLine(wdb.identifier + " parsing is not yet implemented.");
                        break;
                    default:
                        Console.WriteLine("Unknown cache file: " + wdb.identifier);
                        break;
                }

                var humanReadableJsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };

                var storageJsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                };

                if (outputType == "json")
                {
                    var wdbJson = JsonSerializer.Serialize(wdb.entries, humanReadableJsonOptions);
                    Console.WriteLine(wdbJson);
                }
                else if(outputType == "mysql")
                {
                    if (!File.Exists("connectionstring.txt"))
                    {
                        throw new FileNotFoundException("connectionstring.txt not found! Need this for MySQL output.");
                    }

                    using (var connection = new MySqlConnection(File.ReadAllText("connectionstring.txt")))
                    {
                        connection.Open();

                        string targetTable;
                        string nameCol;
                        switch (wdb.identifier)
                        {
                            case "WMOB":
                                targetTable = "creatures";
                                nameCol = "Name[0]";
                                break;
                            default:
                                throw new Exception("WDB identifier " + wdb.identifier + " has no fitting MySQL table to output to.");
                        }

                        using (var cmd = new MySqlCommand())
                        {
                            cmd.Connection = connection;

                            cmd.CommandText = "INSERT INTO wowdata." + targetTable + " (id, name, json) VALUES (@id, @name, @json)";
                            cmd.Parameters.AddWithValue("id", 0);
                            cmd.Parameters.AddWithValue("name", "");
                            cmd.Parameters.AddWithValue("json", "");

                            foreach (var entry in wdb.entries)
                            {
                                cmd.Parameters["id"].Value = entry.Key;
                                cmd.Parameters["name"].Value = entry.Value[nameCol];
                                cmd.Parameters["json"].Value = JsonSerializer.Serialize(entry.Value, storageJsonOptions);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                }

            }
        }

        private static Dictionary<string, Dictionary<string, string>> ReadQuestEntries(BinaryReader bin, wdbCache wdb)
        {
            var entries = new Dictionary<string, Dictionary<string, string>>();

            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32().ToString();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                entries.Add(id, new Dictionary<string, string>());
                entries[id].Add("QuestID", bin.ReadUInt32().ToString());
                entries[id].Add("QuestType", bin.ReadUInt32().ToString());
                
                if (wdb.recordVersion <= 12 && wdb.clientBuild < 33978)
                {
                    // Removed in 9.0.1.33978 - without a RecordVersion change
                    entries[id].Add("QuestLevel", bin.ReadUInt32().ToString());
                }

                if (wdb.recordVersion >= 11)
                {
                    // Unknown, seems to frequently mirror SuggestedGroupNum (but is more expansive)
                    // Theory: Maximum party size number for LFG Tool to create a group
                    entries[id].Add("B27075_Int_1", bin.ReadUInt32().ToString());
                }

                if (wdb.recordVersion <= 12 && wdb.clientBuild < 33978)
                {
                    // Removed in 9.0.1.33978 - without a RecordVersion change
                    entries[id].Add("QuestMaxScalingLevel", bin.ReadUInt32().ToString());
                }

                entries[id].Add("QuestPackageID", bin.ReadUInt32().ToString());

                if (wdb.recordVersion <= 12 && wdb.clientBuild < 33978)
                {
                    // Removed in 9.0.1.33978 - without a RecordVersion change
                    entries[id].Add("QuestMinLevel", bin.ReadUInt32().ToString());
                }

                entries[id].Add("QuestSortID", bin.ReadUInt32().ToString());
                entries[id].Add("QuestInfoID", bin.ReadUInt32().ToString());
                entries[id].Add("SuggestedGroupNum", bin.ReadUInt32().ToString());
                entries[id].Add("RewardNextQuest", bin.ReadUInt32().ToString());
                entries[id].Add("RewardXPDifficulty", bin.ReadUInt32().ToString());
                entries[id].Add("RewardXPMultiplier", bin.ReadSingle().ToString());
                entries[id].Add("RewardMoney", bin.ReadUInt32().ToString());
                entries[id].Add("RewardMoneyDifficulty", bin.ReadUInt32().ToString());
                entries[id].Add("RewardMoneyMultiplier", bin.ReadSingle().ToString());
                entries[id].Add("RewardBonusMoney", bin.ReadUInt32().ToString());

                for(var i = 0; i < 3; i++)
                {
                    entries[id].Add("RewardDisplaySpell[" + i + "]", bin.ReadUInt32().ToString());
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
                entries[id].Add("BFA_UnkDisplayID", bin.ReadUInt32().ToString());
                entries[id].Add("PortraitTurnInDisplayID", bin.ReadUInt32().ToString());

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
                entries[id].Add("TimeAllowed", bin.ReadUInt32().ToString());

                var numObjectives = bin.ReadUInt32();
                entries[id].Add("NumObjectives", numObjectives.ToString());
                entries[id].Add("RaceFlags", bin.ReadUInt64().ToString());
                entries[id].Add("QuestRewardID", bin.ReadUInt32().ToString());
                entries[id].Add("ExpansionID", bin.ReadUInt32().ToString());

                if (wdb.recordVersion > 11)
                {
                    entries[id].Add("B30993_Int_1", bin.ReadUInt32().ToString());
                    entries[id].Add("B31984_Int_1", bin.ReadUInt32().ToString());
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

                    for(var j = 0; j < numVisualEffects; j++)
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
            }

            return entries;
        }

        private static Dictionary<string, Dictionary<string, string>> ReadCreatureEntries(BinaryReader bin)
        {
            var entries = new Dictionary<string, Dictionary<string, string>>();

            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32().ToString();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                entries.Add(id, new Dictionary<string, string>());

                var ds = new DataStore(bin);

                var TitleLength = ds.GetIntByBits(11);
                var TitleAltLength = ds.GetIntByBits(11);
                var CursorNameLength = ds.GetIntByBits(6);
                var Leader = ds.GetIntByBits(1);
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

                entries[id].Add("UnkBFAMultiplier", bin.ReadSingle().ToString());

                for(var i = 0; i < numCreatureDisplays; i++)
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
                entries[id].Add("B28202_Int_1", bin.ReadUInt32().ToString());
                entries[id].Add("B28938_Int_1", bin.ReadUInt32().ToString());
                entries[id].Add("B28938_Int_2", bin.ReadUInt32().ToString());

                entries[id].Add("Title", ds.GetString(TitleLength).Trim('\0'));
                entries[id].Add("TitleAlt", ds.GetString(TitleAltLength).Trim('\0'));

                if(CursorNameLength != 1)
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

        private static Dictionary<string, Dictionary<string, string>> ReadPageTextEntries(BinaryReader bin)
        {
            var entries = new Dictionary<string, Dictionary<string, string>>();

            while (bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32().ToString();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                entries.Add(id, new Dictionary<string, string>());
                entries[id].Add("PageTextID", bin.ReadUInt32().ToString());
                entries[id].Add("NextPageTextID", bin.ReadUInt32().ToString());
                entries[id].Add("unkInt", bin.ReadUInt32().ToString());
                entries[id].Add("unkByte", bin.ReadByte().ToString());

                var ds = new DataStore(bin);

                var TextLength = ds.GetIntByBits(12);
                entries[id].Add("TextLength", TextLength.ToString());

                entries[id].Add("Text", ds.GetString(TextLength).Trim('\0'));
            }

            return entries;
        }

        public static Dictionary<string, Dictionary<string, string>> ReadGameObjectEntries(BinaryReader bin)
        {
            var entries = new Dictionary<string, Dictionary<string, string>>();

            while(bin.BaseStream.Position < bin.BaseStream.Length)
            {
                var id = bin.ReadUInt32().ToString();
                var length = bin.ReadUInt32();

                if (length == 0)
                    break;

                entries.Add(id, new Dictionary<string, string>());
                entries[id].Add("Type", bin.ReadUInt32().ToString());
                entries[id].Add("GameObjectDisplayID", bin.ReadUInt32().ToString());

                var nameSize = 4;
                for(var i = 0; i < nameSize; i++)
                {
                    entries[id].Add("Name[" + i + "]", bin.ReadCString());
                }

                entries[id].Add("Icon", bin.ReadCString());
                entries[id].Add("Action", bin.ReadCString());
                entries[id].Add("Condition", bin.ReadCString());

                var gameDataSize = 34;
                for (var i = 0; i < gameDataSize; i++)
                {
                    entries[id].Add("GameData[" + i  + "]", bin.ReadUInt32().ToString());
                }

                entries[id].Add("Scale", bin.ReadSingle().ToString());
                
                var numQuestItems = bin.ReadByte();
                entries[id].Add("NumQuestItems", numQuestItems.ToString());

                for(var i = 0; i < numQuestItems; i++)
                {
                    entries[id].Add("QuestItems[" + i + "]", bin.ReadUInt32().ToString());
                }

                entries[id].Add("MinLevel", bin.ReadUInt32().ToString());
            }

            return entries;
        }
    }
}
