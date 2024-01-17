using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using DBDefsLib;
using static DBDefsLib.Structs;

namespace WoWTools.HotfixDumper
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("Need DBCache.bin location and DBef dir location!");
            }

            var actuallyV8 = false;

            var dumpKeys = false;
            if(args.Length == 3 && args[2] == "true")
            {
                dumpKeys = true;
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
                if (version != 7 && version != 8 && version != 9)
                    throw new Exception("Unsupported version: " + version);

                build = bin.ReadUInt32();

                var hash = bin.ReadBytes(32);

                // --- Temporary code to detect if DBCache is actually V8 or not by checking if next hotfix magic is in the right spot or not
                if(version == 8)
                {
                    var prePos = bin.BaseStream.Position;

                    if (bin.ReadUInt32() != xfthMagic)
                        throw new Exception("Invalid hotfix entry magic!");

                    bin.ReadUInt32(); // PushID
                    bin.ReadUInt32(); // UniqueID but also maybe not!
                    bin.ReadUInt32(); // TableHash
                    bin.ReadUInt32(); // RecordID

                    var dataSize = bin.ReadInt32(); // DataSize

                    bin.ReadBytes(dataSize + 4);

                    if (bin.ReadUInt32() != xfthMagic)
                    {
                        actuallyV8 = false;
                    }
                    else
                    {
                        actuallyV8 = true;
                    }

                    bin.BaseStream.Position = prePos;
                }
                // -- End of temp code

                while (bin.BaseStream.Length > bin.BaseStream.Position)
                {
                    var hotfix = new DBCacheEntry();
                    hotfix.header = new DBCacheEntryHeader();

                    hotfix.header.magic = bin.ReadUInt32();

                    if (version == 9)
                        bin.ReadInt32(); // Region

                    hotfix.header.pushID = bin.ReadInt32();

                    if (actuallyV8 || version == 9)
                        hotfix.header.uniqueID = bin.ReadUInt32();

                    hotfix.header.tableHash = bin.ReadUInt32();
                    hotfix.header.recordID = bin.ReadUInt32();
                    hotfix.header.dataSize = bin.ReadInt32();
                    hotfix.header.isValid = bin.ReadByte();
                    hotfix.header.pad0 = bin.ReadByte();
                    hotfix.header.pad1 = bin.ReadByte();
                    hotfix.header.pad2 = bin.ReadByte();

                    //hotfix.header = bin.Read<DBCacheEntryHeader>();

                    if (hotfix.header.magic != xfthMagic)
                        throw new Exception("Invalid hotfix entry magic!");

                    if (tableHashes.ContainsKey(hotfix.header.tableHash))
                    {
                        hotfix.tableName = tableHashes[hotfix.header.tableHash];
                    }
                    else
                    {
                        hotfix.tableName = "UNKNOWN " + hotfix.header.tableHash.ToString("X8");
                    }

                    hotfix.data = bin.ReadBytes(hotfix.header.dataSize);

                    hotfixes.Add(hotfix);
                }
            }

            var dbdCache = new Dictionary<string, DBDefinition>();

            var filteredList = new List<HotfixEntry>();
            foreach(var hotfix in hotfixes)
            {
                var hotfixDataMD5 = "";
                if(hotfix.data.Length > 0)
                {
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                    {
                        md5.TransformFinalBlock(hotfix.data, 0, hotfix.data.Length);
                        hotfixDataMD5 = BitConverter.ToString(md5.Hash).Replace("-", string.Empty).ToLower();
                    }
                }

                filteredList.Add(new HotfixEntry
                {
                    pushID = hotfix.header.pushID,
                    uniqueID = hotfix.header.uniqueID,
                    recordID = hotfix.header.recordID,
                    isValid = hotfix.header.isValid,
                    tableName = hotfix.tableName,
                    dataMD5 = hotfixDataMD5
                });

                if (dumpKeys)
                {
                    if (!dbdCache.ContainsKey(hotfix.tableName) && File.Exists(Path.Combine(definitionDir, hotfix.tableName + ".dbd")))
                    {
                        var reader = new DBDReader();
                        var dbd = reader.Read(Path.Combine(definitionDir, hotfix.tableName + ".dbd"));
                        dbdCache.Add(hotfix.tableName, dbd);
                    }

                    if (dbdCache.ContainsKey(hotfix.tableName) && hotfix.header.isValid == 1)
                    {
                        var dbd = dbdCache[hotfix.tableName];
                        VersionDefinitions? versionToUse = null;
                        var buildFound = false;

                        foreach (var definition in dbd.versionDefinitions)
                        {
                            foreach (var versionBuild in definition.builds)
                            {
                                if (versionBuild.build == build)
                                {
                                    versionToUse = definition;
                                    buildFound = true;
                                }
                            }
                        }

                        if (buildFound)
                        {
                            long dataLength = 0;
                            var versionDef = (VersionDefinitions)versionToUse;
                            //Console.WriteLine(hotfix.header.pad0 + " " + hotfix.header.pad1 + " " + hotfix.header.pad2 + " " + hotfix.tableName + " " + hotfix.header.recordID);
                            try
                            {
                                using (var dataBin = new BinaryReader(new MemoryStream(hotfix.data)))
                                {
                                    foreach (var field in versionDef.definitions)
                                    {
                                        if (dataBin.BaseStream.Position == dataBin.BaseStream.Length)
                                            continue;

                                        if (field.isNonInline && field.isID)
                                            continue;
                                   
                                        if (field.arrLength > 0)
                                        {
                                            for (var i = 0; i < field.arrLength; i++)
                                            {
                                                if (dataBin.BaseStream.Position == dataBin.BaseStream.Length)
                                                {
                                                    continue;
                                                }

                                                if (field.size == 0)
                                                {
                                                    if (dbd.columnDefinitions[field.name].type == "float")
                                                    {
                                                        dataBin.ReadSingle();
                                                        dataLength += 4;
                                                    }
                                                    else
                                                    {
                                                        var prevPos = dataBin.BaseStream.Position;
                                                        dataBin.ReadCString();
                                                        dataLength += dataBin.BaseStream.Position - prevPos;
                                                    }
                                                }
                                                else
                                                {
                                                    dataLength += field.size / 8;
                                                    dataBin.ReadBytes(field.size / 8);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (field.size == 0)
                                            {
                                                if (dbd.columnDefinitions[field.name].type == "float")
                                                {
                                                    dataBin.ReadSingle();
                                                    dataLength += 4;
                                                }
                                                else
                                                {
                                                    var prevPos = dataBin.BaseStream.Position;
                                                    dataBin.ReadCString();
                                                    dataLength += dataBin.BaseStream.Position - prevPos;
                                                }
                                            }
                                            else
                                            {
                                                dataLength += field.size / 8;
                                                dataBin.ReadBytes(field.size / 8);
                                            }
                                        }
                                    }

                                    if (dataBin.BaseStream.Length != dataBin.BaseStream.Position)
                                    {
                                        var tableHash = dataBin.ReadUInt32();
                                        if (tableHash == 0)
                                            continue;

                                        if (!tableHashes.ContainsKey(tableHash))
                                        {
                                            Console.WriteLine("Encountered an extra " + tableHash.ToString("X8") + " (unk table) record of " + (dataBin.BaseStream.Length - dataBin.BaseStream.Position) + " bytes in " + hotfix.tableName + " ID " + hotfix.header.recordID);
                                        }
                                        else
                                        {
                                            Console.WriteLine("Encountered an extra " + tableHashes[tableHash] + " record of " + (dataBin.BaseStream.Length - dataBin.BaseStream.Position) + " bytes in " + hotfix.tableName + " ID " + hotfix.header.recordID);

                                            if (tableHashes[tableHash] == "TactKey")
                                            {
                                                var lookup = dataBin.ReadUInt64();
                                                var keyBytes = dataBin.ReadBytes(16);
                                                Console.WriteLine(lookup.ToString("X8").PadLeft(16, '0') + " " + BitConverter.ToString(keyBytes).Replace("-", ""));
                                            }
                                        }
                                        
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Encountered exception while reading record data:" + e.Message);
                            }
                        }
                    }
                }
            }

            var cache = new DBCache();
            cache.build = build;
            cache.entries = filteredList.ToArray();

            if (!dumpKeys)
            {
                Console.WriteLine(JsonConvert.SerializeObject(cache, Formatting.None));
            }
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
            public uint uniqueID;
            public byte isValid;
            public string tableName;
            public string dataMD5;
        }

        private struct DBCacheEntry
        {
            public DBCacheEntryHeader header;
            public string tableName;
            public byte[] data;
        }

        private struct DBCacheEntryHeader
        {
            public uint magic;
            public int pushID;
            public uint uniqueID; // Uncomment when v8 change is live on ptr/retail
            public uint tableHash;
            public uint recordID;
            public int dataSize;
            public byte isValid;
            public byte pad0;
            public byte pad1;
            public byte pad2;
        }
    }
}
