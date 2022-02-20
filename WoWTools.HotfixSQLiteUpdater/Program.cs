using DBDefsLib;
using System.Data.SQLite;
using static DBDefsLib.Structs;

namespace WoWTools.HotfixSQLiteUpdater
{
    class Program
    {
        static Dictionary<string, DBDefinition> dbdCache = new Dictionary<string, DBDefinition>();

        static void Main(string[] args)
        {
            /* Check arguments */
            if (args.Length < 3)
            {
                throw new ArgumentException("<path to DBCache.bin file> <path to DBD definitions directory> <SQLite file to use as unpatched base>");
            }

            var hotfixFile = args[0];
            if (!File.Exists(hotfixFile))
            {
                throw new FileNotFoundException("Hotfix file " + hotfixFile + " not found!");
            }

            var definitionDir = args[1];
            if (!Directory.Exists(definitionDir))
            {
                throw new FileNotFoundException("DBD definition directory " + definitionDir + " not found!");
            }

            var inputDB = args[2];
            if (!File.Exists(inputDB))
            {
                throw new FileNotFoundException("Input SQLite DB not found!");
            }

            /* Back up non-hotfix database to new database */
            Console.WriteLine("Backing up non-hotfix database to new database..");

            SQLiteConnection dbIn = new SQLiteConnection("Data Source= " + inputDB + ";foreign keys=True;Version=3;Read Only=True;");
            SQLiteConnection dbNew = new SQLiteConnection("Data Source=export_withhotfix.db3;foreign keys=True;Version=3;");

            dbIn.Open();
            dbNew.Open();
            dbIn.BackupDatabase(dbNew, "main", "main", -1, null, -1);
            dbIn.Close();

            /* Read DBCache.bin */
            Console.WriteLine("Reading hotfix file..");
            var hotfixes = ReadHotfixFile(hotfixFile, definitionDir);
            var hotfixesCopy = hotfixes.ToList();

            /* Sort and clean up data so we only have status 1/2 hotfixes left */
            foreach (var hotfix in hotfixes)
            {
                if (hotfix.header.isValid == 4)
                {
                    //Console.WriteLine("Removing hotfix of status 4 for " + hotfix.tableName + " " + hotfix.header.recordID);
                    hotfixesCopy.Remove(hotfix);
                }
                else if (hotfix.header.isValid == 3)
                {
                    //Console.WriteLine("Removing hotfixes before PushID " + hotfix.header.pushID + " for " + hotfix.tableName + " " + hotfix.header.recordID);
                    foreach (var targetHotfix in hotfixes)
                    {
                        if (targetHotfix.tableName == hotfix.tableName && targetHotfix.header.recordID == hotfix.header.recordID && targetHotfix.header.pushID < hotfix.header.pushID)
                        {
                            //Console.WriteLine(" Removed hotfix " + targetHotfix.header.pushID + " for " + hotfix.tableName + " " + hotfix.header.recordID);
                            hotfixesCopy.Remove(targetHotfix);
                        }
                    }

                    hotfixesCopy.Remove(hotfix);
                }
            }

            hotfixes = hotfixesCopy;

            Dictionary<string, string> queryCache = new Dictionary<string, string>();

            /* Parse hotfixes and update database */
            Console.WriteLine("Parsing hotfix record data..");
            foreach (var hotfix in hotfixes)
            {
                DBDefinition dbd;

                if (!dbdCache.ContainsKey(hotfix.tableName))
                {
                    if (!File.Exists(Path.Combine(definitionDir, hotfix.tableName + ".dbd")))
                    {
                        Console.WriteLine("DBD for " + hotfix.tableName + " not found in DBD definitions, skipping hotfix..");
                        continue;
                    }

                    var reader = new DBDReader();
                    dbd = reader.Read(Path.Combine(definitionDir, hotfix.tableName + ".dbd"));
                    dbdCache.Add(hotfix.tableName, dbd);
                }
                else
                {
                    dbd = dbdCache[hotfix.tableName];
                }

                var buildFound = false;

                VersionDefinitions? versionToUse = null;
                foreach (var definition in dbd.versionDefinitions)
                {
                    foreach (var versionBuild in definition.builds)
                    {
                        if (versionBuild.build == hotfix.header.build)
                        {
                            versionToUse = definition;
                            buildFound = true;
                        }
                    }
                }

                if (!buildFound)
                {
                    Console.WriteLine("\nNo matching build found for table " + hotfix.tableName + " and build " + hotfix.header.build + ", skipping hotfix..");
                    continue;
                }

                var versionDef = (VersionDefinitions)versionToUse;

                var idField = "";

                foreach (var field in versionDef.definitions)
                {
                    if (field.isID)
                        idField = field.name;
                }

                if (idField == "")
                {
                    Console.WriteLine("Could not establish ID field name for " + hotfix.tableName + ", using ID..");
                    idField = "ID";
                }

                if (hotfix.header.isValid == 2)
                {
                    // Delete record if exists
                    Console.Write("D");
                    //Console.WriteLine("Deleting DB record for hotfix " + hotfix.header.pushID + " for " + hotfix.tableName + " " + hotfix.header.recordID);
                    using (var deleteCMD = new SQLiteCommand("DELETE FROM " + hotfix.tableName + " WHERE " + idField + " = " + hotfix.header.recordID, dbNew))
                    {
                        deleteCMD.ExecuteNonQuery();
                    }
                    continue;
                }

                var insertQueryString = "";
                if (!queryCache.TryGetValue(hotfix.tableName, out insertQueryString))
                {
                    var sqlFields = new List<string>();
                    foreach (var field in versionDef.definitions)
                    {
                        var fieldName = field.name;

                        if (field.name.ToLower() == "default")
                        {
                            fieldName = "_Default";
                        }

                        if (field.name.ToLower() == "order")
                        {
                            fieldName = "_Order";
                        }
                        if (field.name.ToLower() == "index")
                        {
                            fieldName = "_Index";
                        }

                        if (field.arrLength > 0)
                        {
                            for (var i = 0; i < field.arrLength; i++)
                            {
                                sqlFields.Add(fieldName + "_" + i);
                            }
                        }
                        else
                        {
                            sqlFields.Add(fieldName);
                        }
                    }

                    insertQueryString = "INSERT INTO " + hotfix.tableName + "(`" + string.Join("`, `", sqlFields.ToArray()) + "`) VALUES(@" + string.Join(", @", sqlFields.ToArray()) + ")";
                    insertQueryString += " ON CONFLICT(" + idField + ") DO UPDATE SET ";
                    for (var i = 0; i < sqlFields.Count; i++)
                    {
                        if (sqlFields[i] == idField)
                            continue;

                        insertQueryString += "`" + sqlFields[i] + "`" + " = @U" + sqlFields[i];

                        if (i != sqlFields.Count - 1)
                        {
                            insertQueryString += ", ";
                        }
                    }
                }

                var cmd = new SQLiteCommand(insertQueryString, dbNew);

                // Console.WriteLine(hotfix.tableName + " " + hotfix.header.recordID);
                //try
                //{
                using (var dataBin = new BinaryReader(new MemoryStream(hotfix.data)))
                {
                    foreach (var field in versionDef.definitions)
                    {
                        if (dataBin.BaseStream.Position == dataBin.BaseStream.Length)
                            continue;

                        if (field.isNonInline && field.isID)
                        {
                            cmd.Parameters.AddWithValue("@" + idField, hotfix.header.recordID);
                            continue;
                        }

                        // Add parameter
                        SetupParameter(cmd, dataBin, dbd, field);
                    }

                    if (dataBin.BaseStream.Length != dataBin.BaseStream.Position)
                    {
                        var tableHash = dataBin.ReadUInt32();
                        if (tableHash == 0)
                            continue;

                        dataBin.ReadBytes((int)(dataBin.BaseStream.Length - dataBin.BaseStream.Position));
                        //Console.WriteLine("Encountered an extra " + tableHash.ToString("X8") + " record of " + (dataBin.BaseStream.Length - dataBin.BaseStream.Position) + " bytes in " + hotfix.tableName + " ID " + hotfix.header.recordID);
                    }
                }

                Console.Write("I");
                cmd.ExecuteNonQuery();
                //}
                //catch (Exception e)
                //{
                //    Console.WriteLine("Encountered exception while reading or updating record data:" + e.Message);
                //}
            }

            Console.WriteLine("\nCleaning up new database file and closing..");
            using (var cmd = new SQLiteCommand("VACUUM", dbNew))
            {
                cmd.ExecuteNonQuery();
            }

            dbNew.Dispose();
        }

        private static void SetupParameter(SQLiteCommand cmd, BinaryReader dataBin, DBDefinition dbd, Definition field)
        {
            var fieldName = field.name;

            if (field.name.ToLower() == "default")
            {
                fieldName = "_Default";
            }

            if (field.name.ToLower() == "order")
            {
                fieldName = "_Order";
            }
            if (field.name.ToLower() == "index")
            {
                fieldName = "_Index";
            }

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
                            cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadSingle());
                        }
                        else
                        {
                            cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadCString());
                        }
                    }
                    else
                    {
                        switch (field.size)
                        {
                            case 8:
                                if (field.isSigned)
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadSByte());
                                }
                                else
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadByte());
                                }
                                break;
                            case 16:
                                if (field.isSigned)
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadInt16());
                                }
                                else
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadUInt16());
                                }
                                break;
                            case 32:
                                if (field.isSigned)
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadInt32());
                                }
                                else
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadUInt32());
                                }
                                break;
                            case 64:
                                if (field.isSigned)
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadInt64());
                                }
                                else
                                {
                                    cmd.Parameters.AddWithValue("@" + fieldName + "_" + i, dataBin.ReadUInt16());
                                }
                                break;
                        }
                    }

                    cmd.Parameters.AddWithValue("@U" + fieldName + "_" + i, cmd.Parameters["@" + fieldName + "_" + i].Value);
                }
            }
            else
            {
                if (field.size == 0)
                {
                    if (dbd.columnDefinitions[field.name].type == "float")
                    {
                        cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadSingle());
                    }
                    else
                    {
                        cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadCString());
                    }
                }
                else
                {
                    switch (field.size)
                    {
                        case 8:
                            if (field.isSigned)
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadSByte());
                            }
                            else
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadByte());
                            }
                            break;
                        case 16:
                            if (field.isSigned)
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadInt16());
                            }
                            else
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadUInt16());
                            }
                            break;
                        case 32:
                            if (field.isSigned)
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadInt32());
                            }
                            else
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadUInt32());
                            }
                            break;
                        case 64:
                            if (field.isSigned)
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadInt64());
                            }
                            else
                            {
                                cmd.Parameters.AddWithValue("@" + fieldName, dataBin.ReadUInt16());
                            }
                            break;
                    }
                }

                if (!field.isID)
                    cmd.Parameters.AddWithValue("@U" + fieldName, cmd.Parameters["@" + fieldName].Value);
            }
        }

        private static List<DBCacheEntry> ReadHotfixFile(string hotfixFile, string definitionDir)
        {
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
                if (version != 8)
                    throw new Exception("Unsupported version: " + version);

                build = bin.ReadUInt32();

                var hash = bin.ReadBytes(32);

                while (bin.BaseStream.Length > bin.BaseStream.Position)
                {

                    if (bin.ReadUInt32() != xfthMagic)
                        throw new Exception("Invalid hotfix entry magic!");

                    var hotfix = new DBCacheEntry();
                    hotfix.header = new DBCacheEntryHeader();

                    hotfix.header.build = build;
                    hotfix.header.pushID = bin.ReadInt32();
                    hotfix.header.uniqueID = bin.ReadUInt32();
                    hotfix.header.tableHash = bin.ReadUInt32();
                    hotfix.header.recordID = bin.ReadUInt32();
                    hotfix.header.dataSize = bin.ReadInt32();
                    hotfix.header.isValid = bin.ReadByte();
                    hotfix.header.pad0 = bin.ReadByte();
                    hotfix.header.pad1 = bin.ReadByte();
                    hotfix.header.pad2 = bin.ReadByte();

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

            return hotfixes.OrderBy(x => x.header.pushID).ToList();
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
            public uint build;
            public int pushID;
            public uint uniqueID;
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