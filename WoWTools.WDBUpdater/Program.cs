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
                throw new Exception("Require WDB file as argument");
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
                
                switch (wdb.identifier)
                {
                    case "WMOB": // Creature
                        wdb.entries = ReadCreatureEntries(bin);
                        break;
                    case "WGOB": // Gameobject
                        wdb.entries = ReadGameObjectEntries(bin);
                        break;
                    case "WPTX": // PageText
                        //wdb.entries = ReadPageTextEntries(bin);
                        //break;
                    case "WNPC": // NPC
                    case "WPTN": // Petition
                    case "WQST": // Quest
                        Console.WriteLine(wdb.identifier + " parsing is not yet implemented.");
                        break;
                    default:
                        Console.WriteLine("Unknown cache file: " + wdb.identifier);
                        break;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };

                var wdbJson = JsonSerializer.Serialize(wdb.entries, options);
                Console.WriteLine(wdbJson);
            }
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

        //public static void LoadCreatureCache(BinaryReader bin)
        //{
        //    while(bin.BaseStream.Position < bin.BaseStream.Length)
        //    {
        //        var ID = bin.ReadUInt32();
        //        var recordLength = bin.ReadInt32();
        //        var recordBytes = bin.ReadBytes(recordLength);
        //        var bitArray = new BitArray(recordBytes);
        //        uint titleLength = bitArray.
        //        Console.WriteLine(titleLength);
        //    }
        //}

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
