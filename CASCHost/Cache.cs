using CASCEdit;
using CASCEdit.Configs;
using CASCEdit.Helpers;
using CASCEdit.Structs;
using Microsoft.AspNetCore.Hosting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace CASCHost
{
	public class Cache : ICache
	{
		public string Version { get; private set; }
		public HashSet<string> ToPurge { get; private set; }
		public IReadOnlyCollection<CacheEntry> Entries => RootFiles.Values;
		public uint MaxId => RootFiles.Values.Count == 0 ? 0 : RootFiles.Values.Max(x => x.FileDataId);

		public bool HasFiles => RootFiles.Count > 0;
		public bool HasId(uint fileid) => RootFiles.Any(x => x.Value.FileDataId == fileid);

		private IHostingEnvironment env;
		private string Patchpath => Path.Combine(CASContainer.Settings.OutputPath, ".patch");
		private Dictionary<string, CacheEntry> RootFiles;
		private Queue<string> queries = new Queue<string>();
		private bool firstrun = true;
        private string dbFileName => Startup.Settings.SqliteDatabase;


        public Cache(IHostingEnvironment environment)
		{
			env = environment;
			Startup.Logger.LogInformation("Loading cache...");
			Load();
		}

        public void wipeDB()
        {
            if (!File.Exists(dbFileName))
                return;

            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + dbFileName + ";Version=3;"))
            using (SQLiteCommand command = new SQLiteCommand())
            {
                try
                {
                    connection.Open();
                    command.Connection = connection;
                    command.CommandText = WIPE_RECORDS;
                    command.ExecuteNonQuery();
                }
                catch (SQLiteException ex)
                {
                    Startup.Logger?.LogFile(ex.Message);
                    Startup.Logger?.LogAndThrow(CASCEdit.Logging.LogType.Critical, "Something wrong with the database.");
                }
            }
        }


        public void AddOrUpdate(CacheEntry item)
		{
			if(firstrun)
			{
				Clean();
				firstrun = false;
			}

			if (RootFiles == null)
				Load();

			// Update value
			if (RootFiles.ContainsKey(item.Path))
			{
				// Matching
				if (RootFiles[item.Path] == item)
					return;

				RootFiles[item.Path] = item;

				queries.Enqueue(string.Format(REPLACE_RECORD, item.Path, item.FileDataId, item.NameHash, item.CEKey, item.EKey));
				return;
			}

			// Matching Id - Ignore root/encoding
			if (item.FileDataId > 0 && RootFiles.Values.Any(x => x.FileDataId == item.FileDataId))
			{
				var existing = RootFiles.Where(x => x.Value.FileDataId == item.FileDataId).ToArray();
				foreach (var ex in existing)
				{
					queries.Enqueue(string.Format(DELETE_RECORD, item.Path));
					RootFiles.Remove(ex.Key);
				}
			}

			// Add
			RootFiles.Add(item.Path, item);

			queries.Enqueue(string.Format(REPLACE_RECORD, item.Path, item.FileDataId, item.NameHash, item.CEKey, item.EKey));
		}

		public void Remove(string file)
		{
			if (RootFiles.ContainsKey(file))
			{
				queries.Enqueue(string.Format(DELETE_RECORD, RootFiles[file].Path));
				RootFiles.Remove(file);
			}
		}

		public void Save()
		{
			BatchTransaction();
		}

		public void Load()
		{
			if (RootFiles != null)
				return;

			RootFiles = new Dictionary<string, CacheEntry>();
			LoadOrCreate();
		}

		public void Clean()
		{
			//Delete previous Root and Encoding
			if (RootFiles.ContainsKey("__ROOT__") && File.Exists(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ROOT__"].EKey.ToString(), "data"))))
				File.Delete(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ROOT__"].EKey.ToString(), "data")));
			if (RootFiles.ContainsKey("__ENCODING__") && File.Exists(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ENCODING__"].EKey.ToString(), "data"))))
				File.Delete(Path.Combine(CASContainer.Settings.OutputPath, Helper.GetCDNPath(RootFiles["__ENCODING__"].EKey.ToString(), "data")));
		}


		#region SQL Methods
		private void LoadOrCreate()
		{
			Version = new SingleConfig(Path.Combine(env.WebRootPath, "SystemFiles", ".build.info"), "Active", "1", Startup.Settings.Product)["Version"];

            if (!File.Exists(dbFileName))
                SQLiteConnection.CreateFile(dbFileName);

            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + dbFileName + ";Version=3;"))
            using (SQLiteCommand command = new SQLiteCommand())
            {
                try
                {
                    connection.Open();
                    command.Connection = connection;

                    // create data table
                    command.CommandText = CREATE_DATA_TABLE;
                    command.ExecuteNonQuery();

                    // load data
                    command.CommandText = LOAD_DATA;
                    ReadAll(command.ExecuteReader());

                    // purge old data
                    command.CommandText = PURGE_RECORDS;
                    command.ExecuteNonQuery();
                }
                catch (SQLiteException ex)
                {
                    Startup.Logger?.LogFile(ex.Message);
                    Startup.Logger?.LogAndThrow(CASCEdit.Logging.LogType.Critical, "Something wrong with the database.");
                }
            }
		}

		private void ReadAll(DbDataReader reader)
		{
			ToPurge = new HashSet<string>();

			using (reader)
			{
				while (reader.Read())
				{
					CacheEntry entry = new CacheEntry()
					{
						Path = reader.GetFieldValue<string>(1),
						FileDataId = Convert.ToUInt32(reader.GetFieldValue<Int64>(2)),
						NameHash = Convert.ToUInt64(reader.GetFieldValue<string>(3)),
						CEKey = new MD5Hash(reader.GetFieldValue<string>(4).ToByteArray()),
						EKey = new MD5Hash(reader.GetFieldValue<string>(5).ToByteArray())
					};

					// keep files that still exist or are special and not flagged to be deleted
					bool keep = File.Exists(Path.Combine(env.WebRootPath, "Data", Startup.Settings.Product, entry.Path)) && reader.IsDBNull(6);
					if (keep || entry.FileDataId == 0)
					{
						RootFiles.Add(entry.Path, entry);
					}
					else if (reader.IsDBNull(6)) // needs to be marked for purge
					{
						queries.Enqueue(string.Format(DELETE_RECORD, entry.Path));
						Startup.Logger.LogInformation($"{entry.Path} missing. Marked for removal.");
						ToPurge.Add(entry.Path);
					}
					else if (Convert.ToDateTime(reader.GetFieldValue<string>(6)) <= DateTime.Now.Date) // needs to be purged
					{
						ToPurge.Add(entry.Path);

						string cdnpath = Helper.GetCDNPath(entry.EKey.ToString(), "", "", Startup.Settings.StaticMode);
						string filepath = Path.Combine(env.WebRootPath, "Output", Startup.Settings.Product, cdnpath);

						if (File.Exists(filepath))
							File.Delete(filepath);
						if (File.Exists(Path.Combine(env.WebRootPath, "Data", Startup.Settings.Product, entry.Path)))
							File.Delete(Path.Combine(env.WebRootPath, "Data", Startup.Settings.Product, entry.Path));
					}
				}

				reader.Close();
			}

			BatchTransaction();
		}

		private void BatchTransaction()
		{
			if (queries.Count == 0)
				return;

			Startup.Logger.LogInformation("Bulk updating database.");

			StringBuilder sb = new StringBuilder();
			while (queries.Count > 0)
			{
				sb.Clear();

				int count = Math.Min(queries.Count, 2500); // limit queries per transaction
				for (int i = 0; i < count; i++)
					sb.AppendLine(queries.Dequeue());

                Startup.Logger.LogInformation("Updating...");

                using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + dbFileName + ";Version=3;"))
                using (SQLiteCommand command = new SQLiteCommand(sb.ToString(), connection))
                {
                    try
                    {
                        connection.Open();
                        command.ExecuteNonQuery();
                    }
                    catch (SQLiteException ex)
                    {
                        Startup.Logger?.LogFile(ex.Message);
                        Startup.Logger?.LogAndThrow(CASCEdit.Logging.LogType.Critical, "DB Error.");
                    }
                }
			}
		}

        #endregion

        #region SQL Strings

        private const string CREATE_DATA_TABLE = "CREATE TABLE IF NOT EXISTS `root_entries` (" +
                                                      "`Id` INTEGER PRIMARY KEY, " +
                                                      "`Path` TEXT UNIQUE," +
                                                      " `FileDataId` INTEGER," +
                                                      "`Hash` TEXT," +
                                                      "`MD5` TEXT," +
                                                      "`BLTE` TEXT," +
                                                      "`PurgeAt` TEXT NULL" +
                                                ");";

		private const string LOAD_DATA =      "SELECT * FROM `root_entries`;";

        private const string REPLACE_RECORD = "INSERT INTO `root_entries` (`Path`, `FileDataId`, `Hash`, `MD5`, `BLTE`, `PurgeAt`) VALUES ('{0}', '{1}', '{2}', '{3}', '{4}', NULL) " +
                                              "ON CONFLICT(`Path`) DO UPDATE SET `FileDataId` = excluded.FileDataId, `Hash` = excluded.hash, `MD5` = excluded.md5, `BLTE` = excluded.blte, `PurgeAt` = excluded.PurgeAt;";


        private const string DELETE_RECORD = "UPDATE `root_entries` SET `PurgeAt` = datetime('now') WHERE `Path` = '{0}';";

		private const string PURGE_RECORDS = "DELETE FROM `root_entries` WHERE `PurgeAt` <  datetime('now');";

        private const string WIPE_RECORDS = "DELETE FROM `root_entries`";

        #endregion

    }
}
