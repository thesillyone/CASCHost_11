using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.IO;
using System.Net.Http;
using System.Net;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using CASCEdit;
using CASCEdit.Configs;

namespace CASCHost
{
	public class Startup
	{
		public static AppSettings Settings { get; private set; }
		public static Logger Logger { get; private set; }
		public static Cache Cache { get; private set; }
		public static DataWatcher Watcher { get; private set; }

		public Startup(ILoggerFactory loggerFactory)
		{
			Logger = new Logger(loggerFactory.CreateLogger<Startup>());
		}

		public void ConfigureServices(IServiceCollection services)
		{
			var builder = new ConfigurationBuilder().AddJsonFile($"appsettings.{Program.Product}.json", optional: false, reloadOnChange: true);
			IConfigurationRoot configuration = builder.Build();

			services.AddOptions();
            services.AddHttpContextAccessor();
            services.AddSingleton<FileProvider>();
            services.Configure<AppSettings>(configuration.GetSection("AppSettings"));
		}

		public void Configure(IApplicationBuilder app, IHostingEnvironment env, IServiceProvider serviceProvider, IOptions<AppSettings> settings)
		{
			if (env.IsDevelopment())
				app.UseDeveloperExceptionPage();

            //Load settings
            Settings = settings.Value;

            //Set file handler
            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = serviceProvider.GetService<FileProvider>(),
				DefaultContentType = "application/octet-stream",
				ServeUnknownFileTypes = true
			});

            //Create directories
            Directory.CreateDirectory(Path.Combine(env.WebRootPath, "Data"));
            Directory.CreateDirectory(Path.Combine(env.WebRootPath, "Data", Settings.Product));

			Directory.CreateDirectory(Path.Combine(env.WebRootPath, "Output"));
            Directory.CreateDirectory(Path.Combine(env.WebRootPath, "Output", Settings.Product));

            Directory.CreateDirectory(Path.Combine(env.WebRootPath, "SystemFiles"));
            Directory.CreateDirectory(Path.Combine(env.WebRootPath, "SystemFiles", Settings.Product));

            if (!Directory.Exists(Settings.GameDirectory))
            {
                GuessGameDirectory(env);
            }

            Logger.LogInformation($"Using product: {Settings.Product}");

            //Check installation is corect
            StartUpChecks(env);

			//Load cache
			Cache = new Cache(env);

			//Start DataWatcher
			Watcher = new DataWatcher(env);
		}

        public static void ImportBuildInfo(string targetPath)
        {
            string remoteBuildInfoPath = Path.Combine(Settings.GameDirectory, ".build.info");

            if (File.Exists(remoteBuildInfoPath))
            {
                Logger.LogInformation("Importing .build.info from wow folder...");

                try
                {
                    File.Delete(targetPath);
                    File.Move(remoteBuildInfoPath, targetPath);
                } catch (IOException e) {
                    Logger.LogError(e.Message);
                    Logger.LogError("Failed to import .build.info. You might need to do it manually.");

                    return;
                }

                return;
            }
     
            Logger.LogInformation(".build.info not found in wow folder, skipping...");
        }

        private static void GuessGameDirectory(IHostingEnvironment env)
        {         
            string wowRoot = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft\", "installPath", "");

            while (Directory.Exists(wowRoot))
            {
                if (File.Exists(Path.Combine(wowRoot, ".product.db")))
                {
                    Settings.GameDirectory = wowRoot.TrimEnd('/');
                    Settings.Save(env);

                    Logger.LogInformation($"GameDirectory set to {Settings.GameDirectory}");

                    return;
                }
                else
                {
                    wowRoot = Path.GetDirectoryName(wowRoot);
                }
            }

            Logger.LogWarning($"Failed to set GameDirectory via registry.");
        }

		private void StartUpChecks(IHostingEnvironment env)
		{
			const string DOMAIN_REGEX = @"^(?:.*?:\/\/)?(?:[^@\n]+@)?(?:www\.)?([^\/\n]+)";

			//Normalise values
			Settings.PatchUrl = Settings.PatchUrl.TrimEnd('/');
			Settings.HostDomain = Settings.HostDomain.TrimEnd('/');
			Settings.Save(env);

            //Game Directory check
            if (!File.Exists(Path.Combine(Settings.GameDirectory, "World of Warcraft Launcher.exe")))
            {
                Logger.LogCritical($"Invalid GameDirectory specified in appsettings.{Settings.Product}.json");
                DoExit();
            }

            string buildInfoPath = Path.Combine(env.WebRootPath, "SystemFiles", ".build.info");
            
            //.build.info import
            ImportBuildInfo(buildInfoPath);

            //.build.info check
            if (!File.Exists(buildInfoPath))
			{
                Logger.LogCritical($"Missing .build.info in {Path.Combine(env.WebRootPath, "SystemFiles")}");
                Logger.LogCritical("Open Battle.net Launcher to regenerate it and try again.");
                DoExit();
            }

			//Validate the domain name - must be a valid domain or localhost
			bool hasProtocol = Settings.HostDomain.ToLowerInvariant().Contains("://");
			if (IPAddress.TryParse(Settings.HostDomain, out IPAddress address))
			{
				Logger.LogCritical("HostDomain must be a domain Name.");
                DoExit();
            }
			else if (hasProtocol || !Regex.IsMatch(Settings.HostDomain, DOMAIN_REGEX + "$", RegexOptions.IgnoreCase))
			{
				string domain = Regex.Match(Settings.HostDomain, DOMAIN_REGEX, RegexOptions.IgnoreCase).Groups[1].Value;
				Logger.LogCritical($"HostDomain invalid expected {domain.ToUpper()} got {Settings.HostDomain.ToUpper()}.");
			}

			//Validate offical patch url
			if (!Uri.IsWellFormedUriString(Settings.PatchUrl, UriKind.Absolute))
			{
				Logger.LogCritical("Malformed Patch Url.");
                DoExit();
            }
			else if (!PingPatchUrl())
			{
				Logger.LogCritical("Unreachable Patch Url.");
                DoExit();
            }
		}

		private bool PingPatchUrl()
		{
			try
			{
				using (var clientHandler = new HttpClientHandler() { AllowAutoRedirect = false })
				using (var webRequest = new HttpClient(clientHandler) { Timeout = TimeSpan.FromSeconds(10) })
				using (var request = new HttpRequestMessage(HttpMethod.Head, Settings.PatchUrl + "/versions"))
				using (var response = webRequest.SendAsync(request).Result)
					return response.StatusCode == HttpStatusCode.OK;
			}
			catch
			{
				return false;
			}
		}

        private void DoExit()
        {
            Logger.LogCritical("Exiting...");
            System.Threading.Thread.Sleep(3000);
            Environment.Exit(0);
        }
	}
}
