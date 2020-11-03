using CASCEdit;
using CASCEdit.Configs;
using CASCEdit.Structs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CASCHost
{
	public class DataWatcher : IDisposable
	{
		public bool RebuildInProgress { get; private set; }

		private IHostingEnvironment _env;
		private FileSystemWatcher watcher;
		private FileSystemWatcher gameDirectoryWatcher;
		private Timer timer;
		private readonly string dataPath;
		private readonly string outputPath;
		private readonly string buildInfoPath;
		private ConcurrentDictionary<string, FileSystemEventArgs> changes;
		private CASSettings settings;

        public DataWatcher(IHostingEnvironment env)
		{
			_env = env;
			dataPath = Path.Combine(env.WebRootPath, "Data", Startup.Settings.Product);
			outputPath = Path.Combine(env.WebRootPath, "Output", Startup.Settings.Product);
            buildInfoPath = Path.Combine(env.WebRootPath, "SystemFiles");
            changes = new ConcurrentDictionary<string, FileSystemEventArgs>();


            LoadSettings();
            if (settings.StaticMode)
            {
                ForceRebuild();
                return;
            }

            //Rebuild if files have changed since last run otherwise wait for a change to occur
            if (IsRebuildRequired())
            {
                ForceRebuild();
            }
            else
            {
                timer = new Timer(UpdateCASCDirectory, null, Timeout.Infinite, Timeout.Infinite);
            }
				

            watcher = new FileSystemWatcher()
			{
				Path = dataPath,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
				EnableRaisingEvents = false,
				IncludeSubdirectories = true
			};

			watcher.Changed += LogChange;
			watcher.Created += LogChange;
			watcher.Deleted += LogChange;
			watcher.Renamed += LogChange;

            gameDirectoryWatcher = new FileSystemWatcher()
            {
                Path = Startup.Settings.GameDirectory,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            gameDirectoryWatcher.Changed += onGameDirectoryChange;
            gameDirectoryWatcher.Created += onGameDirectoryChange;
        }

        #region Change Detection

        private void onGameDirectoryChange(object sender, FileSystemEventArgs e)
        {
            //Ignore folder changes
            if (IsDirectory(e.FullPath))
                return;

  
            if (e.Name == ".build.info" && File.Exists(e.FullPath))
            {
                Startup.Logger.LogInformation($".build.info changes detected. Updating...");
                Startup.ImportBuildInfo(Path.Combine(buildInfoPath, ".build.info"));

                if (!RebuildInProgress && IsRebuildRequired())
                    ForceRebuild();

                return;
            }
        }

        private void LogChange(object sender, FileSystemEventArgs e)
        {
            //Ignore folder changes - rename is handled below all files fire this event themselves
            if (IsDirectory(e.FullPath))
                return;

            //Assume anything extensionless is a folder
            if (string.IsNullOrWhiteSpace(Path.GetExtension(e.FullPath)))
                return;

            //Update or add change
            changes.AddOrUpdate(e.FullPath, e, (k, v) => e);

            //Add delay for user to finishing changing files
            timer.Change(30 * 1000, 0);
        }

        private void LogChange(object sender, RenamedEventArgs e)
		{
			if (IsDirectory(e.FullPath))
			{
				var files = Directory.EnumerateFiles(e.FullPath, "*.*", SearchOption.AllDirectories);
				if (!files.Any())
					return;

				//Update files in directory
				foreach (var f in files)
				{
					var arg = new RenamedEventArgs(e.ChangeType, Path.GetDirectoryName(f), f, f.Replace(e.FullPath, e.OldFullPath));
                    changes.AddOrUpdate(f, arg, (k, v) => arg); //Set as renamed file
                }
            }
			else
			{
                changes.AddOrUpdate(e.FullPath, e, (k, v) => e);
            }

            //Add delay for user to finishing changing files
            timer.Change((int)(30 * 1000), 0);
		}

		private bool IsDirectory(string dir) => Directory.Exists(dir) || (File.Exists(dir) && File.GetAttributes(dir).HasFlag(FileAttributes.Directory));
		#endregion


		#region CASC Update
		public void ForceRebuild()
		{
            //Wipe DB
            Startup.Cache.wipeDB();

            //Wipe CASC
            WipeCASCDirectory();

            changes.Clear();

            //Rebuild all files
            var files = Directory.EnumerateFiles(dataPath, "*.*", SearchOption.AllDirectories).OrderBy(f => f);
            foreach (var f in files)
			{
                var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(f), Path.GetFileName(f));
                changes.AddOrUpdate(f, args, (k, v) => args);
            }

            timer = new Timer(UpdateCASCDirectory, null, 0, Timeout.Infinite);
		}

        private void WipeCASCDirectory()
        {
            Directory.Delete(outputPath, true);
            Directory.CreateDirectory(outputPath);
        }


        private void UpdateCASCDirectory(object obj)
		{
			if (RebuildInProgress) //Saving already wait for build to finish
			{
				timer.Change(30 * 1000, Timeout.Infinite); //30 second delay
				return;
			}

			RebuildInProgress = true;
			timer.Change(Timeout.Infinite, Timeout.Infinite);

			Startup.Logger.LogWarning($"CASC rebuild started [{DateTime.Now}] - {changes.Count} files to be amended.");
			Stopwatch sw = Stopwatch.StartNew();

			//Open the CASC Container
			CASContainer.Open(settings);
			CASContainer.OpenCdnIndices(false);
			CASContainer.OpenEncoding();
			CASContainer.OpenRoot(settings.Locale, Startup.Settings.MinimumFileDataId, Startup.Settings.OnlineListFile);

			if(Startup.Settings.BNetAppSupport) // these are only needed by the bnet app launcher
			{
				CASContainer.OpenDownload();
				CASContainer.OpenInstall();
				CASContainer.DownloadInstallAssets();
            }

			//Remove Purged files
			foreach (var purge in Startup.Cache.ToPurge)
				CASContainer.RootHandler.RemoveFile(purge);

			//Apply file changes
			while (changes.Count > 0)
			{
				var key = changes.Keys.First();
                if (changes.TryRemove(key, out FileSystemEventArgs change))
                {
                    string fullpath = change.FullPath;
					string cascpath = GetCASCPath(fullpath);
					string oldpath = GetCASCPath((change as RenamedEventArgs)?.OldFullPath + "");


					switch (change.ChangeType)
					{
						case WatcherChangeTypes.Renamed:
							if (CASContainer.RootHandler.GetEntry(oldpath) == null)
								CASContainer.RootHandler.AddFile(fullpath, cascpath);
							else
								CASContainer.RootHandler.RenameFile(oldpath, cascpath);
							break;
						case WatcherChangeTypes.Deleted:
							CASContainer.RootHandler.RemoveFile(cascpath);
							break;
						default:
							CASContainer.RootHandler.AddFile(fullpath, cascpath);
							break;
					}
				}
			}

			//Save and Clean
			CASContainer.Save();

			//Update directory hashes
			Startup.Settings.DirectoryHash = new[]
			{
				GetDirectoryHash(dataPath),
				GetDirectoryHash(outputPath)
			};

            string GameVersion = new SingleConfig(Path.Combine(buildInfoPath, ".build.info"), "Active", "1", Startup.Settings.Product)["Version"];
            Startup.Settings.GameVersion = GameVersion;


            Startup.Settings.Save(_env);

			sw.Stop();
			Startup.Logger.LogWarning($"CASC rebuild finished [{DateTime.Now}] - {Math.Round(sw.Elapsed.TotalSeconds, 3)}s");

            if (settings.StaticMode)
            {
                Environment.Exit(0);
            }

			RebuildInProgress = false;
		}

		private string GetCASCPath(string file)
		{
			string lookup = new DirectoryInfo(_env.WebRootPath).Name;
			string[] parts = file.Split(Path.DirectorySeparatorChar);
			return Path.Combine(parts.Skip(Array.IndexOf(parts, lookup) + 3).ToArray()); //Remove top directories
		}
		#endregion


		private bool IsRebuildRequired()
		{
			Startup.Logger.LogInformation("Offline file change check.");

            string BuildInfoVersion = new SingleConfig(Path.Combine(buildInfoPath, ".build.info"), "Active", "1", Startup.Settings.Product)["Version"];
            if (BuildInfoVersion != Startup.Settings.GameVersion)
            {
                Startup.Logger.LogInformation($"New version detected: {BuildInfoVersion}. Rebuilding...");
                return true;
            }
            else
            {
                Startup.Logger.LogInformation($"Same version, nothing to do here.");
            }

            //No data files
            if (!Directory.EnumerateFiles(dataPath, "*.*", SearchOption.AllDirectories).Any())
				return false;

			string[] hashes = new[]
			{
				GetDirectoryHash(dataPath),
				GetDirectoryHash(outputPath)
			};

			//Check for offline changes
			if (!hashes.SequenceEqual(Startup.Settings.DirectoryHash) /*|| Startup.Settings.RebuildOnLoad*/)
			{
				return true;
			}

			return false;
		}

		private string GetDirectoryHash(string directory)
		{
			var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).OrderBy(x => x);
			using (IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
			{
				foreach (var f in files)
				{
					FileInfo info = new FileInfo(f);
					md5.AppendData(Encoding.UTF8.GetBytes(info.FullName)); //path
					md5.AppendData(BitConverter.GetBytes(info.Length)); //size
					md5.AppendData(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks)); //last written
				}

				return (!files.Any() ? new byte[16] : md5.GetHashAndReset()).ToMD5String(); //Enforce empty hash string if no files
			}
		}

		private void LoadSettings()
		{
			LocaleFlags locale = Enum.TryParse(Startup.Settings.Locale, true, out LocaleFlags tmp) ? tmp : LocaleFlags.enUS;

			Startup.Logger.LogConsole($"Default Locale set to {locale}.");

			settings = new CASSettings()
			{
				Host = Startup.Settings.HostDomain,
				BasePath = _env.WebRootPath,
				OutputPath = Path.Combine("Output", Startup.Settings.Product),
				SystemFilesPath = Path.Combine("SystemFiles", Startup.Settings.Product),
				BuildInfoPath = this.buildInfoPath,
				PatchUrl = Startup.Settings.PatchUrl,
				Logger = Startup.Logger,
				Cache = Startup.Cache,
				Locale = locale,
				CDNs = new HashSet<string>(),
                StaticMode = Startup.Settings.StaticMode,
                Product = Startup.Settings.Product
            };


            if (!settings.StaticMode)
            {
                settings.CDNs.Add(settings.Host);
            }
            else
            {
                Startup.Logger.LogConsole($"CASCHost running in static mode");
            }

            if (Startup.Settings.CDNs != null)
				foreach (var cdn in Startup.Settings.CDNs)
					settings.CDNs.Add(cdn);
		}


		public void Dispose()
		{
			changes?.Clear();
			changes = null;
			timer?.Dispose();
			timer = null;
			watcher?.Dispose();
			watcher = null;
            gameDirectoryWatcher?.Dispose();
            gameDirectoryWatcher = null;

        }
	}
}
