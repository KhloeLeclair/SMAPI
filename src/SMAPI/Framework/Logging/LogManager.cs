using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using StardewModdingAPI.Framework.Commands;
using StardewModdingAPI.Internal.ConsoleWriting;
using StardewModdingAPI.Toolkit.Framework.ModData;
using StardewModdingAPI.Toolkit.Utilities;

namespace StardewModdingAPI.Framework.Logging
{
    /// <summary>Manages the SMAPI console window and log file.</summary>
    internal class LogManager : IDisposable
    {
        /*********
        ** Fields
        *********/
        /// <summary>The log file to which to write messages.</summary>
        private readonly LogFileManager LogFile;

        /// <summary>Manages console output interception.</summary>
        private readonly ConsoleInterceptionManager InterceptionManager = new ConsoleInterceptionManager();

        /// <summary>Get a named monitor instance.</summary>
        private readonly Func<string, Monitor> GetMonitorImpl;

        /// <summary>Regex patterns which match console non-error messages to suppress from the console and log.</summary>
        private readonly Regex[] SuppressConsolePatterns =
        {
            new Regex(@"^TextBox\.Selected is now '(?:True|False)'\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^(?:FRUIT )?TREE: IsClient:(?:True|False) randomOutput: \d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^loadPreferences\(\); begin", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^savePreferences\(\); async=", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex(@"^DebugOutput:\s+(?:added CLOUD|added cricket|dismount tile|Ping|playerPos)", RegexOptions.Compiled | RegexOptions.CultureInvariant)
        };

        /// <summary>Regex patterns which match console messages to show a more friendly error for.</summary>
        private readonly ReplaceLogPattern[] ReplaceConsolePatterns =
        {
            // Steam not loaded
            new ReplaceLogPattern(
                search: new Regex(@"^System\.InvalidOperationException: Steamworks is not initialized\.[\s\S]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
                replacement:
#if SMAPI_FOR_WINDOWS
                    "Oops! Steam achievements won't work because Steam isn't loaded. You can launch the game through Steam to fix that (see 'Part 2: Configure Steam' in the install guide for more info: https://smapi.io/install).",
#else
                    "Oops! Steam achievements won't work because Steam isn't loaded. You can launch the game through Steam to fix that.",
#endif
                logLevel: LogLevel.Error
            ),

            // save file not found error
            new ReplaceLogPattern(
                search: new Regex(@"^System\.IO\.FileNotFoundException: [^\n]+\n[^:]+: '[^\n]+[/\\]Saves[/\\]([^'\r\n]+)[/\\]([^'\r\n]+)'[\s\S]+LoadGameMenu\.FindSaveGames[\s\S]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
                replacement: "The game can't find the '$2' file for your '$1' save. See https://stardewvalleywiki.com/Saves#Troubleshooting for help.",
                logLevel: LogLevel.Error
            )
        };


        /*********
        ** Accessors
        *********/
        /// <summary>The core logger and monitor for SMAPI.</summary>
        public Monitor Monitor { get; }

        /// <summary>The core logger and monitor on behalf of the game.</summary>
        public Monitor MonitorForGame { get; }


        /*********
        ** Public methods
        *********/
        /****
        ** Initialization
        ****/
        /// <summary>Construct an instance.</summary>
        /// <param name="logPath">The log file path to write.</param>
        /// <param name="colorConfig">The colors to use for text written to the SMAPI console.</param>
        /// <param name="writeToConsole">Whether to output log messages to the console.</param>
        /// <param name="isVerbose">Whether verbose logging is enabled. This enables more detailed diagnostic messages than are normally needed.</param>
        /// <param name="isDeveloperMode">Whether to enable full console output for developers.</param>
        public LogManager(string logPath, ColorSchemeConfig colorConfig, bool writeToConsole, bool isVerbose, bool isDeveloperMode)
        {
            // init construction logic
            this.GetMonitorImpl = name => new Monitor(name, this.InterceptionManager, this.LogFile, colorConfig, isVerbose)
            {
                WriteToConsole = writeToConsole,
                ShowTraceInConsole = isDeveloperMode,
                ShowFullStampInConsole = isDeveloperMode
            };

            // init fields
            this.LogFile = new LogFileManager(logPath);
            this.Monitor = this.GetMonitor("SMAPI");
            this.MonitorForGame = this.GetMonitor("game");

            // redirect direct console output
            if (this.MonitorForGame.WriteToConsole)
                this.InterceptionManager.OnMessageIntercepted += message => this.HandleConsoleMessage(this.MonitorForGame, message);
        }

        /// <summary>Get a monitor instance derived from SMAPI's current settings.</summary>
        /// <param name="name">The name of the module which will log messages with this instance.</param>
        public Monitor GetMonitor(string name)
        {
            return this.GetMonitorImpl(name);
        }

        /// <summary>Set the title of the SMAPI console window.</summary>
        /// <param name="title">The new window title.</param>
        public void SetConsoleTitle(string title)
        {
            Console.Title = title;
        }

        /****
        ** Console input
        ****/
        /// <summary>Run a loop handling console input.</summary>
        [SuppressMessage("ReSharper", "FunctionNeverReturns", Justification = "The thread is aborted when the game exits.")]
        public void RunConsoleInputLoop(CommandManager commandManager, Action reloadTranslations, Action<string> handleInput, Func<bool> continueWhile)
        {
            // prepare console
            this.Monitor.Log("Type 'help' for help, or 'help <cmd>' for a command's usage", LogLevel.Info);
            commandManager
                .Add(new HelpCommand(commandManager), this.Monitor)
                .Add(new HarmonySummaryCommand(), this.Monitor)
                .Add(new ReloadI18nCommand(reloadTranslations), this.Monitor);

            // start handling command line input
            Thread inputThread = new Thread(() =>
            {
                while (true)
                {
                    // get input
                    string input = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    // handle command
                    this.Monitor.LogUserInput(input);
                    handleInput(input);
                }
            });
            inputThread.Start();

            // keep console thread alive while the game is running
            while (continueWhile())
                Thread.Sleep(1000 / 10);
            if (inputThread.ThreadState == ThreadState.Running)
                inputThread.Abort();
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        public void PressAnyKeyToExit()
        {
            this.Monitor.Log("Game has ended. Press any key to exit.", LogLevel.Info);
            this.PressAnyKeyToExit(showMessage: false);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        /// <param name="showMessage">Whether to print a 'press any key to exit' message to the console.</param>
        public void PressAnyKeyToExit(bool showMessage)
        {
            if (showMessage)
                Console.WriteLine("Game has ended. Press any key to exit.");
            Thread.Sleep(100);
            Console.ReadKey();
            Environment.Exit(0);
        }

        /****
        ** Crash/update handling
        ****/
        /// <summary>Create a crash marker and duplicate the log into the crash log.</summary>
        public void WriteCrashLog()
        {
            try
            {
                File.WriteAllText(Constants.FatalCrashMarker, string.Empty);
                File.Copy(this.LogFile.Path, Constants.FatalCrashLog, overwrite: true);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"SMAPI failed trying to track the crash details: {ex.GetLogSummary()}", LogLevel.Error);
            }
        }

        /// <summary>Write an update alert marker file.</summary>
        /// <param name="version">The new version found.</param>
        public void WriteUpdateMarker(string version)
        {
            File.WriteAllText(Constants.UpdateMarker, version);
        }

        /// <summary>Check whether SMAPI crashed or detected an update during the last session, and display them in the SMAPI console.</summary>
        public void HandleMarkerFiles()
        {
            // show update alert
            if (File.Exists(Constants.UpdateMarker))
            {
                string rawUpdateFound = File.ReadAllText(Constants.UpdateMarker);
                if (SemanticVersion.TryParse(rawUpdateFound, out ISemanticVersion updateFound))
                {
                    if (Constants.ApiVersion.IsPrerelease() && updateFound.IsNewerThan(Constants.ApiVersion))
                    {
                        this.Monitor.Log("A new version of SMAPI was detected last time you played.", LogLevel.Error);
                        this.Monitor.Log($"You can update to {updateFound}: https://smapi.io.", LogLevel.Error);
                        this.Monitor.Log("Press any key to continue playing anyway. (This only appears when using a SMAPI beta.)", LogLevel.Info);
                        Console.ReadKey();
                    }
                }
                File.Delete(Constants.UpdateMarker);
            }

            // show details if game crashed during last session
            if (File.Exists(Constants.FatalCrashMarker))
            {
                this.Monitor.Log("The game crashed last time you played. If it happens repeatedly, see 'get help' on https://smapi.io.", LogLevel.Error);
                this.Monitor.Log("If you ask for help, make sure to share your SMAPI log: https://smapi.io/log.", LogLevel.Error);
                this.Monitor.Log("Press any key to delete the crash data and continue playing.", LogLevel.Info);
                Console.ReadKey();
                File.Delete(Constants.FatalCrashLog);
                File.Delete(Constants.FatalCrashMarker);
            }
        }

        /// <summary>Log a fatal exception which prevents SMAPI from launching.</summary>
        /// <param name="exception">The exception details.</param>
        public void LogFatalLaunchError(Exception exception)
        {
            switch (exception)
            {
                // audio crash
                case InvalidOperationException ex when ex.Source == "Microsoft.Xna.Framework.Xact" && ex.StackTrace.Contains("Microsoft.Xna.Framework.Audio.AudioEngine..ctor"):
                    this.Monitor.Log("The game couldn't load audio. Do you have speakers or headphones plugged in?", LogLevel.Error);
                    this.Monitor.Log($"Technical details: {ex.GetLogSummary()}");
                    break;

                // missing content folder exception
                case FileNotFoundException ex when ex.Message == "Could not find file 'C:\\Program Files (x86)\\Steam\\SteamApps\\common\\Stardew Valley\\Content\\XACT\\FarmerSounds.xgs'.": // path in error is hardcoded regardless of install path
                    this.Monitor.Log("The game can't find its Content\\XACT\\FarmerSounds.xgs file. You can usually fix this by resetting your content files (see https://smapi.io/troubleshoot#reset-content ), or by uninstalling and reinstalling the game.", LogLevel.Error);
                    this.Monitor.Log($"Technical details: {ex.GetLogSummary()}");
                    break;

                // generic exception
                default:
                    this.MonitorForGame.Log($"The game failed to launch: {exception.GetLogSummary()}", LogLevel.Error);
                    break;
            }
        }

        /****
        ** General log output
        ****/
        /// <summary>Log the initial header with general SMAPI and system details.</summary>
        /// <param name="modsPath">The path from which mods will be loaded.</param>
        /// <param name="customSettings">The custom SMAPI settings.</param>
        public void LogIntro(string modsPath, IDictionary<string, object> customSettings)
        {
            // init logging
            this.Monitor.Log($"SMAPI {Constants.ApiVersion} with Stardew Valley {Constants.GameVersion} on {EnvironmentUtility.GetFriendlyPlatformName(Constants.Platform)}", LogLevel.Info);
            this.Monitor.Log($"Mods go here: {modsPath}", LogLevel.Info);
            if (modsPath != Constants.DefaultModsPath)
                this.Monitor.Log("(Using custom --mods-path argument.)");
            this.Monitor.Log($"Log started at {DateTime.UtcNow:s} UTC");

            // log custom settings
            if (customSettings.Any())
                this.Monitor.Log($"Loaded with custom settings: {string.Join(", ", customSettings.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value}"))}");
        }

        /// <summary>Log details for settings that don't match the default.</summary>
        /// <param name="isDeveloperMode">Whether to enable full console output for developers.</param>
        /// <param name="checkForUpdates">Whether to check for newer versions of SMAPI and mods on startup.</param>
        public void LogSettingsHeader(bool isDeveloperMode, bool checkForUpdates)
        {
            if (isDeveloperMode)
                this.Monitor.Log($"You have SMAPI for developers, so the console will be much more verbose. You can disable developer mode by installing the non-developer version of SMAPI, or by editing {Constants.ApiConfigPath}.", LogLevel.Info);
            if (!checkForUpdates)
                this.Monitor.Log($"You configured SMAPI to not check for updates. Running an old version of SMAPI is not recommended. You can enable update checks by reinstalling SMAPI or editing {Constants.ApiConfigPath}.", LogLevel.Warn);
            if (!this.Monitor.WriteToConsole)
                this.Monitor.Log("Writing to the terminal is disabled because the --no-terminal argument was received. This usually means launching the terminal failed.", LogLevel.Warn);
            this.Monitor.VerboseLog("Verbose logging enabled.");
        }

        /// <summary>Log info about loaded mods.</summary>
        /// <param name="loaded">The full list of loaded content packs and mods.</param>
        /// <param name="loadedContentPacks">The loaded content packs.</param>
        /// <param name="loadedMods">The loaded mods.</param>
        /// <param name="skippedMods">The mods which could not be loaded.</param>
        /// <param name="logParanoidWarnings">Whether to log issues for mods which directly use potentially sensitive .NET APIs like file or shell access.</param>
        public void LogModInfo(IModMetadata[] loaded, IModMetadata[] loadedContentPacks, IModMetadata[] loadedMods, IModMetadata[] skippedMods, bool logParanoidWarnings)
        {
            // log loaded mods
            this.Monitor.Log($"Loaded {loadedMods.Length} mods" + (loadedMods.Length > 0 ? ":" : "."), LogLevel.Info);
            foreach (IModMetadata metadata in loadedMods.OrderBy(p => p.DisplayName))
            {
                IManifest manifest = metadata.Manifest;
                this.Monitor.Log(
                    $"   {metadata.DisplayName} {manifest.Version}"
                    + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                    + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                    LogLevel.Info
                );
            }

            this.Monitor.Newline();

            // log loaded content packs
            if (loadedContentPacks.Any())
            {
                string GetModDisplayName(string id) => loadedMods.FirstOrDefault(p => p.HasID(id))?.DisplayName;

                this.Monitor.Log($"Loaded {loadedContentPacks.Length} content packs:", LogLevel.Info);
                foreach (IModMetadata metadata in loadedContentPacks.OrderBy(p => p.DisplayName))
                {
                    IManifest manifest = metadata.Manifest;
                    this.Monitor.Log(
                        $"   {metadata.DisplayName} {manifest.Version}"
                        + (!string.IsNullOrWhiteSpace(manifest.Author) ? $" by {manifest.Author}" : "")
                        + $" | for {GetModDisplayName(metadata.Manifest.ContentPackFor.UniqueID)}"
                        + (!string.IsNullOrWhiteSpace(manifest.Description) ? $" | {manifest.Description}" : ""),
                        LogLevel.Info
                    );
                }

                this.Monitor.Newline();
            }

            // log mod warnings
            this.LogModWarnings(loaded, skippedMods, logParanoidWarnings);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.InterceptionManager.Dispose();
            this.LogFile.Dispose();
        }


        /*********
        ** Protected methods
        *********/
        /// <summary>Redirect messages logged directly to the console to the given monitor.</summary>
        /// <param name="gameMonitor">The monitor with which to log messages as the game.</param>
        /// <param name="message">The message to log.</param>
        private void HandleConsoleMessage(IMonitor gameMonitor, string message)
        {
            // detect exception
            LogLevel level = message.Contains("Exception") ? LogLevel.Error : LogLevel.Trace;

            // ignore suppressed message
            if (level != LogLevel.Error && this.SuppressConsolePatterns.Any(p => p.IsMatch(message)))
                return;

            // show friendly error if applicable
            foreach (ReplaceLogPattern entry in this.ReplaceConsolePatterns)
            {
                string newMessage = entry.Search.Replace(message, entry.Replacement);
                if (message != newMessage)
                {
                    gameMonitor.Log(newMessage, entry.LogLevel);
                    gameMonitor.Log(message);
                    return;
                }
            }

            // forward to monitor
            gameMonitor.Log(message, level);
        }

        /// <summary>Write a summary of mod warnings to the console and log.</summary>
        /// <param name="mods">The loaded mods.</param>
        /// <param name="skippedMods">The mods which could not be loaded.</param>
        /// <param name="logParanoidWarnings">Whether to log issues for mods which directly use potentially sensitive .NET APIs like file or shell access.</param>
        private void LogModWarnings(IEnumerable<IModMetadata> mods, IModMetadata[] skippedMods, bool logParanoidWarnings)
        {
            // get mods with warnings
            IModMetadata[] modsWithWarnings = mods.Where(p => p.Warnings != ModWarning.None).ToArray();
            if (!modsWithWarnings.Any() && !skippedMods.Any())
                return;

            // log intro
            {
                int count = modsWithWarnings.Length + skippedMods.Length;
                this.Monitor.Log($"Found {count} mod{(count == 1 ? "" : "s")} with warnings:", LogLevel.Info);
            }

            // log skipped mods
            if (skippedMods.Any())
            {
                // get logging logic
                void LogSkippedMod(IModMetadata mod)
                {
                    string message = $"      - {mod.DisplayName}{(mod.Manifest?.Version != null ? " " + mod.Manifest.Version.ToString() : "")} because {mod.Error}";

                    this.Monitor.Log(message, LogLevel.Error);
                    if (mod.ErrorDetails != null)
                        this.Monitor.Log($"        ({mod.ErrorDetails})");
                }

                // find skipped dependencies
                IModMetadata[] skippedDependencies;
                {
                    HashSet<string> skippedDependencyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> skippedModIds = new HashSet<string>(from mod in skippedMods where mod.HasID() select mod.Manifest.UniqueID, StringComparer.OrdinalIgnoreCase);
                    foreach (IModMetadata mod in skippedMods)
                    {
                        foreach (string requiredId in skippedModIds.Intersect(mod.GetRequiredModIds()))
                            skippedDependencyIds.Add(requiredId);
                    }
                    skippedDependencies = skippedMods.Where(p => p.HasID() && skippedDependencyIds.Contains(p.Manifest.UniqueID)).ToArray();
                }

                // log skipped mods
                this.Monitor.Log("   Skipped mods", LogLevel.Error);
                this.Monitor.Log("   " + "".PadRight(50, '-'), LogLevel.Error);
                this.Monitor.Log("      These mods could not be added to your game.", LogLevel.Error);
                this.Monitor.Newline();

                if (skippedDependencies.Any())
                {
                    foreach (IModMetadata mod in skippedDependencies.OrderBy(p => p.DisplayName))
                        LogSkippedMod(mod);
                    this.Monitor.Newline();
                }

                foreach (IModMetadata mod in skippedMods.OrderBy(p => p.DisplayName))
                    LogSkippedMod(mod);
                this.Monitor.Newline();
            }

            // log warnings
            if (modsWithWarnings.Any())
            {
                // broken code
                this.LogModWarningGroup(modsWithWarnings, ModWarning.BrokenCodeLoaded, LogLevel.Error, "Broken mods",
                    "These mods have broken code, but you configured SMAPI to load them anyway. This may cause bugs,",
                    "errors, or crashes in-game."
                );

                // changes serializer
                this.LogModWarningGroup(modsWithWarnings, ModWarning.ChangesSaveSerializer, LogLevel.Warn, "Changed save serializer",
                    "These mods change the save serializer. They may corrupt your save files, or make them unusable if",
                    "you uninstall these mods."
                );

                // patched game code
                this.LogModWarningGroup(modsWithWarnings, ModWarning.PatchesGame, LogLevel.Info, "Patched game code",
                    "These mods directly change the game code. They're more likely to cause errors or bugs in-game; if",
                    "your game has issues, try removing these first. Otherwise you can ignore this warning."
                );

                // unvalidated update tick
                this.LogModWarningGroup(modsWithWarnings, ModWarning.UsesUnvalidatedUpdateTick, LogLevel.Info, "Bypassed safety checks",
                    "These mods bypass SMAPI's normal safety checks, so they're more likely to cause errors or save",
                    "corruption. If your game has issues, try removing these first."
                );

                // paranoid warnings
                if (logParanoidWarnings)
                {
                    this.LogModWarningGroup(
                        modsWithWarnings,
                        match: mod => mod.HasUnsuppressedWarnings(ModWarning.AccessesConsole, ModWarning.AccessesFilesystem, ModWarning.AccessesShell),
                        level: LogLevel.Debug,
                        heading: "Direct system access",
                        blurb: new[]
                        {
                            "You enabled paranoid warnings and these mods directly access the filesystem, shells/processes, or",
                            "SMAPI console. (This is usually legitimate and innocent usage; this warning is only useful for",
                            "further investigation.)"
                        },
                        modLabel: mod =>
                        {
                            List<string> labels = new List<string>();
                            if (mod.HasUnsuppressedWarnings(ModWarning.AccessesConsole))
                                labels.Add("console");
                            if (mod.HasUnsuppressedWarnings(ModWarning.AccessesFilesystem))
                                labels.Add("files");
                            if (mod.HasUnsuppressedWarnings(ModWarning.AccessesShell))
                                labels.Add("shells/processes");

                            return $"{mod.DisplayName} ({string.Join(", ", labels)})";
                        }
                    );
                }

                // no update keys
                this.LogModWarningGroup(modsWithWarnings, ModWarning.NoUpdateKeys, LogLevel.Debug, "No update keys",
                    "These mods have no update keys in their manifest. SMAPI may not notify you about updates for these",
                    "mods. Consider notifying the mod authors about this problem."
                );

                // not crossplatform
                this.LogModWarningGroup(modsWithWarnings, ModWarning.UsesDynamic, LogLevel.Debug, "Not crossplatform",
                    "These mods use the 'dynamic' keyword, and won't work on Linux/Mac."
                );
            }
        }

        /// <summary>Write a mod warning group to the console and log.</summary>
        /// <param name="mods">The mods to search.</param>
        /// <param name="match">Matches mods to include in the warning group.</param>
        /// <param name="level">The log level for the logged messages.</param>
        /// <param name="heading">A brief heading label for the group.</param>
        /// <param name="blurb">A detailed explanation of the warning, split into lines.</param>
        /// <param name="modLabel">Formats the mod label, or <c>null</c> to use the <see cref="IModMetadata.DisplayName"/>.</param>
        private void LogModWarningGroup(IModMetadata[] mods, Func<IModMetadata, bool> match, LogLevel level, string heading, string[] blurb, Func<IModMetadata, string> modLabel = null)
        {
            // get matching mods
            string[] modLabels = mods
                .Where(match)
                .Select(mod => modLabel?.Invoke(mod) ?? mod.DisplayName)
                .OrderBy(p => p)
                .ToArray();
            if (!modLabels.Any())
                return;

            // log header/blurb
            this.Monitor.Log("   " + heading, level);
            this.Monitor.Log("   " + "".PadRight(50, '-'), level);
            foreach (string line in blurb)
                this.Monitor.Log("      " + line, level);
            this.Monitor.Newline();

            // log mod list
            foreach (string label in modLabels)
                this.Monitor.Log($"      - {label}", level);

            this.Monitor.Newline();
        }

        /// <summary>Write a mod warning group to the console and log.</summary>
        /// <param name="mods">The mods to search.</param>
        /// <param name="warning">The mod warning to match.</param>
        /// <param name="level">The log level for the logged messages.</param>
        /// <param name="heading">A brief heading label for the group.</param>
        /// <param name="blurb">A detailed explanation of the warning, split into lines.</param>
        private void LogModWarningGroup(IModMetadata[] mods, ModWarning warning, LogLevel level, string heading, params string[] blurb)
        {
            this.LogModWarningGroup(mods, mod => mod.HasUnsuppressedWarnings(warning), level, heading, blurb);
        }


        /*********
        ** Protected types
        *********/
        /// <summary>A console log pattern to replace with a different message.</summary>
        private class ReplaceLogPattern
        {
            /*********
            ** Accessors
            *********/
            /// <summary>The regex pattern matching the portion of the message to replace.</summary>
            public Regex Search { get; }

            /// <summary>The replacement string.</summary>
            public string Replacement { get; }

            /// <summary>The log level for the new message.</summary>
            public LogLevel LogLevel { get; }


            /*********
            ** Public methods
            *********/
            /// <summary>Construct an instance.</summary>
            /// <param name="search">The regex pattern matching the portion of the message to replace.</param>
            /// <param name="replacement">The replacement string.</param>
            /// <param name="logLevel">The log level for the new message.</param>
            public ReplaceLogPattern(Regex search, string replacement, LogLevel logLevel)
            {
                this.Search = search;
                this.Replacement = replacement;
                this.LogLevel = logLevel;
            }
        }
    }
}
