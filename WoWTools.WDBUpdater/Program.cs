using MySql.Data.MySqlClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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
            public DBBuild buildInfo;
            public Dictionary<string, Dictionary<string, string>> entries;
        }

        public struct DBBuild
        {
            public string version;
            public short expansion;
            public short major;
            public short minor;
            public int build;
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
                wdb.buildInfo = GetBuildInfoFromDB(wdb.clientBuild);

                var humanReadableJsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };

                if (wdb.clientLocale != "enUS" && wdb.clientLocale != "enGB")
                {
                    if (outputType == "json")
                    {
                        var wdbJson = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>(), humanReadableJsonOptions);
                        Console.WriteLine(wdbJson);
                    }

                    return;
                }

                // All WDB structures below are based on Simca's excellent 010 template.

                switch (wdb.identifier)
                {
                    case "WMOB": // Creature
                        wdb.entries = ReadCreatureEntries(bin, wdb);
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

                if (outputType == "json")
                {
                    var wdbJson = JsonSerializer.Serialize(wdb.entries, humanReadableJsonOptions);
                    Console.WriteLine(wdbJson);
                }
                else if(outputType == "mysql")
                {
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
                            case "WQST":
                                targetTable = "quests";
                                nameCol = "LogTitle";
                                break;
                            default:
                                return;
                        }

                        var currentEntries = new Dictionary<uint, DBEntry>();
                        using (var currentDataCmd = new MySqlCommand("SELECT id, name, firstseenbuild, lastupdatedbuild, json FROM " + targetTable, connection))
                        using (var reader = currentDataCmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var entry = new DBEntry()
                                {
                                    id = reader.GetUInt32(0),
                                    name = reader.GetString(1),
                                    firstSeenBuild = reader.GetUInt32(2),
                                    lastUpdatedBuild = reader.GetUInt32(3),
                                    json = reader.GetString(4)
                                };

                                currentEntries.Add(entry.id, entry);
                            }
                        }

                        Console.WriteLine(currentEntries.Count + " entries in " + targetTable + " DB");
                        Console.WriteLine(wdb.entries.Count + " entries in WDB file");

                        var newEntries = 0;
                        var updatedEntries = 0;

                        using (var updateCmd = new MySqlCommand())
                        using (var insertCmd = new MySqlCommand())
                        {
                            insertCmd.Connection = connection;
                            insertCmd.CommandText = "INSERT INTO wowdata." + targetTable + " (id, name, firstseenbuild, lastupdatedbuild, json) VALUES (@id, @name, @firstseenbuild, @lastupdatedbuild, @json)";
                            insertCmd.Parameters.AddWithValue("id", 0);
                            insertCmd.Parameters.AddWithValue("name", "");
                            insertCmd.Parameters.AddWithValue("firstseenbuild", wdb.clientBuild);
                            insertCmd.Parameters.AddWithValue("lastupdatedbuild", wdb.clientBuild);
                            insertCmd.Parameters.AddWithValue("json", "");

                            updateCmd.Connection = connection;
                            updateCmd.CommandText = "UPDATE wowdata." + targetTable + " SET name = @name, lastupdatedbuild = @lastupdatedbuild, json = @json WHERE ID = @id";
                            updateCmd.Parameters.AddWithValue("id", 0);
                            updateCmd.Parameters.AddWithValue("name", "");
                            updateCmd.Parameters.AddWithValue("lastupdatedbuild", wdb.clientBuild);
                            updateCmd.Parameters.AddWithValue("json", "");

                            foreach (var entry in wdb.entries)
                            {
                                var properID = uint.Parse(entry.Key);
                                var serializedJson = JsonSerializer.Serialize(entry.Value, storageJsonOptions);

                                if (!currentEntries.TryGetValue(properID, out DBEntry dbEntry))
                                {
                                    Console.WriteLine(entry.Key + " is new, adding!");
                                    insertCmd.Parameters["id"].Value = entry.Key;
                                    insertCmd.Parameters["name"].Value = entry.Value[nameCol];
                                    insertCmd.Parameters["json"].Value = serializedJson;
                                    insertCmd.ExecuteNonQuery();
                                    newEntries++;
                                }
                                else
                                {
                                    // Don't let classic or older builds overwrite existing records, will break classic updates, TODO: Needs proper checking between retail/ptr, maybe DBD build comparison code?
                                    if (wdb.buildInfo.expansion == 1 && wdb.buildInfo.major == 13)
                                        continue;

                                    if (wdb.buildInfo.expansion == 2 && wdb.buildInfo.major == 5)
                                        continue;

                                    if (dbEntry.json != serializedJson && wdb.clientBuild > dbEntry.lastUpdatedBuild)
                                    {
                                        Console.WriteLine("JSON for " + entry.Key + " is changed updating! Before: \n " + dbEntry.json + "\n After: \n" + serializedJson);
                                        updateCmd.Parameters["id"].Value = entry.Key;
                                        updateCmd.Parameters["name"].Value = entry.Value[nameCol];
                                        updateCmd.Parameters["json"].Value = serializedJson;
                                        updateCmd.ExecuteNonQuery();
                                        updatedEntries++;
                                    }
                                }
                            }
                        }

                        Console.WriteLine("New entries: " + newEntries);
                        Console.WriteLine("Updated entries: " + updatedEntries);
                    }
                }

            }
        }

        private struct DBEntry
        {
            public uint id;
            public string name;
            public uint firstSeenBuild;
            public uint lastUpdatedBuild;
            public string json;
        }

        private static DBBuild GetBuildInfoFromDB(uint build)
        {


            var dbBuild = new DBBuild();

            if (!File.Exists("connectionstring.txt"))
            {
#if DEBUG
                Console.WriteLine("connectionstring.txt not found! Need this for build lookup, using hardcoded build.");
                dbBuild.version = "9.0.1.35078";
                dbBuild.expansion = 9;
                dbBuild.major = 1;
                dbBuild.minor = 0;
                dbBuild.build = 38312;
                return dbBuild;
#else
                throw new Exception("connectionstring.txt not found! Need this for build lookup.");
#endif
            }

            using (var connection = new MySqlConnection(File.ReadAllText("connectionstring.txt")))
            {
                connection.Open();
                using (var buildCmd = new MySqlCommand("SELECT version, expansion, major, minor, build FROM casc.wow_builds WHERE build = @build", connection))
                {
                    buildCmd.Parameters.AddWithValue("build", build);
                    using (var reader = buildCmd.ExecuteReader())
                    {
                        if (!reader.HasRows)
                        {
                            throw new Exception("Build not found in DB!");
                        }

                        while (reader.Read())
                        {
                            dbBuild.version = reader.GetString(0);
                            dbBuild.expansion = reader.GetInt16(1);
                            dbBuild.major = reader.GetInt16(2);
                            dbBuild.minor = reader.GetInt16(3);
                            dbBuild.build = reader.GetInt32(4);
                        }
                    }
                }
            }

            return dbBuild;
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
                if(wdb.clientBuild >= 35078 && wdb.buildInfo.expansion >= 9)
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

                // Might be a few fields off, so many 0s
                if (wdb.buildInfo.expansion >= 9 && wdb.buildInfo.major >= 1)
                {
                    entries[id].Add("SL_Int_1", bin.ReadUInt32().ToString());
                }

                // Might be one or two fields off
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
                    entries[id].Add("ManagedWorldStateID", bin.ReadUInt32().ToString());
                    entries[id].Add("QuestSessionBonus", bin.ReadUInt32().ToString());
                }

                if (wdb.clientBuild >= 35078 && wdb.buildInfo.expansion >= 9)
                {
                    for (var i = 0; i < rewardDisplaySpellCount; i++)
                    {
                        entries[id].Add("RewardDisplaySpellID[" + i + "]", bin.ReadUInt32().ToString());
                        entries[id].Add("RewardDisplayPlayerConditionID[" + i + "]", bin.ReadUInt32().ToString());
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

        private static Dictionary<string, Dictionary<string, string>> ReadCreatureEntries(BinaryReader bin, wdbCache wdb)
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
                entries[id].Add("CreatureClassMask", bin.ReadUInt32().ToString());

                if (wdb.buildInfo.expansion >= 9 && wdb.buildInfo.major >= 1)
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
