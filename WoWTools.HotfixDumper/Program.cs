using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace WoWTools.HotfixDumper
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                throw new ArgumentException("Need DBCache.bin location and DBef dir location!");
            }

            var hotfixFile = args[0];

            if (!File.Exists(hotfixFile))
            {
                throw new FileNotFoundException("File " + hotfixFile + " not found!");
            }

            var definitionDir = args[1];

            if (!Directory.Exists(definitionDir))
            {
                throw new FileNotFoundException("DBD definition directory " + definitionDir + " not found!");
            }

            var tableHashes = new Dictionary<uint, string>();

            foreach (var file in Directory.GetFiles(definitionDir))
            {
                var dbName = Path.GetFileNameWithoutExtension(file);
                tableHashes.Add(Utils.Hash(dbName.ToUpper()), dbName);
            }

            var xfthMagic = 'X' << 0 | 'F' << 8 | 'T' << 16 | 'H' << 24;

            uint build;

            var hotfixes = new List<DBCacheEntry>();

            using (var ms = new MemoryStream(File.ReadAllBytes(hotfixFile)))
            using (var bin = new BinaryReader(ms))
            {
                if (bin.ReadUInt32() != xfthMagic)
                    throw new Exception("Invalid hotfix file!");

                var version = bin.ReadUInt32();
                if (version != 7)
                    throw new Exception("Unsupported version: " + version);

                build = bin.ReadUInt32();

                var hash = bin.ReadBytes(32);

                while (bin.BaseStream.Length > bin.BaseStream.Position)
                {
                    var hotfix = new DBCacheEntry();
                    hotfix.header = bin.Read<DBCacheEntryHeader>();
                    if (tableHashes.ContainsKey(hotfix.header.tableHash))
                    {
                        hotfix.tableName = tableHashes[hotfix.header.tableHash];
                    }
                    else
                    {
                        hotfix.tableName = "UNKNOWN";
                    }

                    bin.ReadBytes(hotfix.header.dataSize);

                    if (hotfix.header.magic != xfthMagic)
                        throw new Exception("Invalid hotfix entry magic!");

                    hotfixes.Add(hotfix);
                }
            }

            var filteredList = new List<HotfixEntry>();
            foreach(var hotfix in hotfixes)
            {
                if (hotfix.header.pushID == -1)
                    continue;

                filteredList.Add(new HotfixEntry
                {
                    pushID = hotfix.header.pushID,
                    recordID = hotfix.header.recordID,
                    isValid = hotfix.header.isValid,
                    tableName = hotfix.tableName
                });
            }

            var cache = new DBCache();
            cache.build = build;
            cache.entries = filteredList.ToArray();

            Console.WriteLine(JsonConvert.SerializeObject(cache, Formatting.None));
        }

        private struct DBCache
        {
            public uint build;
            public HotfixEntry[] entries;
        }
        private struct HotfixEntry
        {
            public int pushID;
            public uint recordID;
            public byte isValid;
            public string tableName;
        }

        private struct DBCacheEntryHeader
        {
            public uint magic;
            public int pushID;
            public uint tableHash;
            public uint recordID;
            public int dataSize;
            public byte isValid;
            public byte pad0;
            public byte pad1;
            public byte pad2;
        }

        private struct DBCacheEntry
        {
            public DBCacheEntryHeader header;
            public string tableName;
        }
    }
}
