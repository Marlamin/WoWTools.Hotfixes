using DBDefsLib;
using System;
using System.Collections.Generic;
using System.IO;
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

            var dumpKeys = false;
            if (args.Length == 3 && args[2] == "true")
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

            const int xfthMagic = 'X' << 0 | 'F' << 8 | 'T' << 16 | 'H' << 24;

            uint build;

            var hotfixes = new List<DBCacheEntry>();

            using (var ms = new MemoryStream(File.ReadAllBytes(hotfixFile)))
            using (var bin = new BinaryReader(ms))
            //using (var fs = new FileStream(hotfixFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096 * 16))
            //using (var bin = new BinaryReader(fs))
            {
                if (bin.ReadUInt32() != xfthMagic)
                    throw new Exception("Invalid hotfix file!");

                var version = bin.ReadUInt32();
                if (version != 7)
                    throw new Exception("Unsupported version: " + version);

                build = bin.ReadUInt32();

                //var hash = bin.ReadBytes(32);
                bin.BaseStream.Position += 32;

                while (bin.BaseStream.Length > bin.BaseStream.Position)
                {
                    var hotfix = new DBCacheEntry();
                    hotfix.header = bin.Read<DBCacheEntryHeader>();

                    if (hotfix.header.magic != xfthMagic)
                        throw new Exception("Invalid hotfix entry magic!");

                    if (tableHashes.TryGetValue(hotfix.header.tableHash, out var tname))
                    {
                        hotfix.tableName = tname;
                    }
                    else
                    {
                        hotfix.tableName = "UNKNOWN";
                    }

                    hotfix.data = bin.ReadBytes(hotfix.header.dataSize);

                    hotfixes.Add(hotfix);
                }
            }

            var dbdCache = new Dictionary<string, DBDefinition>();

            var filteredList = new List<HotfixEntry>(hotfixes.Count);
            var md5 = System.Security.Cryptography.MD5.Create();
            foreach (var hotfix in hotfixes)
            {
                var hotfixDataMD5 = "";
                if (hotfix.data.Length > 0)
                {
                    Span<byte> md5Hash = md5.ComputeHash(hotfix.data);
                    //hotfixDataMD5 = md5Hash.ToHexString();
                    hotfixDataMD5 = md5Hash.ToHexStringUnsafe();
                    //hotfixDataMD5 = BitConverter.ToString(md5Hash).Replace("-", string.Empty).ToLower();
                }

                filteredList.Add(new HotfixEntry
                {
                    pushID = hotfix.header.pushID,
                    recordID = hotfix.header.recordID,
                    isValid = hotfix.header.isValid,
                    tableName = hotfix.tableName,
                    dataMD5 = hotfixDataMD5
                });

                if (dumpKeys)
                {
                    if (!dbdCache.ContainsKey(hotfix.tableName))
                    {
                        var dbdPath = Path.Combine(definitionDir, hotfix.tableName + ".dbd");
                        if (File.Exists(dbdPath))
                        {
                            var reader = new DBDReader();
                            var dbd2 = reader.Read(dbdPath);
                            dbdCache.Add(hotfix.tableName, dbd2);
                        }
                    }

                    if (hotfix.header.isValid == 1 && dbdCache.TryGetValue(hotfix.tableName, out var dbd))
                    {
                        VersionDefinitions? versionToUse = null;

                        foreach (var definition in dbd.versionDefinitions)
                        {
                            foreach (var versionBuild in definition.builds)
                            {
                                if (versionBuild.build == build)
                                {
                                    versionToUse = definition;
                                }
                            }
                        }

                        if (versionToUse.HasValue)
                        {
                            long dataLength = 0;
                            var versionDef = versionToUse.Value;
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

                                        if (tableHashes.TryGetValue(tableHash, out var tname))
                                        {
                                            Console.WriteLine($"Encountered an extra {tname} record of {dataBin.BaseStream.Length - dataBin.BaseStream.Position} bytes in {hotfix.tableName} ID {hotfix.header.recordID}");

                                            if (tableHashes[tableHash] == "TactKey")
                                            {
                                                var lookup = dataBin.ReadUInt64();
                                                var keyBytes = dataBin.ReadBytes(16);
                                                Console.WriteLine($"{lookup:X16} {BitConverter.ToString(keyBytes).Replace("-", "")}");
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Encountered an extra {tableHash:X8} (unk table) record of {dataBin.BaseStream.Length - dataBin.BaseStream.Position} bytes in {hotfix.tableName} ID {hotfix.header.recordID}");
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
            cache.entries = filteredList;

            if (!dumpKeys)
            {
                //Console.WriteLine(JsonConvert.SerializeObject(cache, Formatting.None));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(cache, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }));
            }
        }

        private struct DBCache
        {
            public uint build;
            public List<HotfixEntry> entries;
        }

        private struct HotfixEntry
        {
            public int pushID;
            public uint recordID;
            public byte isValid;
            public string tableName;
            public string dataMD5;
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
            public byte[] data;
        }
    }
}
