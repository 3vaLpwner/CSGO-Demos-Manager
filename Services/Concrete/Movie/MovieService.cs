﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Core;
using Services.Models;
using Services.Models.Movie;

namespace Services.Concrete.Movie
{
    public class MovieService
    {
        private readonly MovieConfiguration _config;

        /// <summary>
        /// Tick when the user's cfg will be loaded.
        /// </summary>
        private const int EXEC_USER_CFG_TICK = 50;

        /// <summary>
        /// Tick when required commands will be executed.
        /// </summary>
        private const int EXEC_COMMANDS_TICK = EXEC_USER_CFG_TICK + 64;

        /// <summary>
        /// Tick that will fast forward to the tick where recording must start
        /// </summary>
        private const int GOTO_TICK = EXEC_COMMANDS_TICK + 64;

        /// <summary>
        /// Path where all raw files will be saved.
        /// </summary>
        private string RawFullPath => _config.RawFilesDestination + Path.DirectorySeparatorChar + _config.OutputFilename;

        /// <summary>
        /// Name of the normal used for the "mirv_streams add normal" command.
        /// </summary>
        private const string NORMAL_NAME = "defaultNormal";

        public event Action OnVirtualDubStarted;
        public event Action OnVirtualDubClosed;
        public event Action OnFFmpegStarted;
        public event Action OnFFmpegClosed;
        public event Action OnGameStarted;
        public event Action OnGameRunning;
        public event Action OnGameClosed;
        public event Action OnHLAEStarted;
        public event Action OnHLAEClosed;

        public MovieService(MovieConfiguration config)
        {
            _config = config;
        }

        public static readonly List<string> DefaultCommands = new List<string>
        {
            "cl_draw_only_deathnotices 1",
            "cl_clock_correction 0",
            "mirv_fix playerAnimState 1",
            "mirv_streams record matPostprocessEnable 1",
            "mirv_streams record matDynamicTonemapping 1",
            "mirv_streams record matMotionBlurEnabled 0",
            "mirv_streams record matForceTonemapScale 0",
            "net_graph 0",
        };

        /// <summary>
        /// The following commands are required to have a working recording.
        /// </summary>
        public static readonly List<string> MandatoryCommands = new List<string>
        {
            "sv_cheats 1",
            "host_timescale 0",
            "mirv_snd_timescale 1", // fix the audio sync issue with startmovie command
            "mirv_gameoverlay enable 0",
        };

        public async Task Start()
        {
            if (_config.GenerateRawFiles)
            {
                GenerateCfgFile();
                GenerateVdmFile();
                GameLauncherConfiguration config = new GameLauncherConfiguration(_config.Demo)
                {
                    EnableHlae = true,
                    OnGameStarted = HandleGameStarted,
                    OnGameRunning = HandleGameRunning,
                    OnGameClosed = HandleGameClosed,
                    OnHLAEStarted = HandleHLAEStarted,
                    OnHLAEClosed = HandleHLAEClosed,
                    DeleteVdmFileAtStratup = false,
                    CsgoExePath = AppSettings.GetCsgoExePath(), // TODO move it?
                    EnableHlaeConfigParent = _config.EnableHlaeConfigParent,
                    Fullscreen = _config.FullScreen,
                    IsWorldwideEnabled = _config.IsWorldwideEnabled,
                    Height = _config.Height,
                    Width = _config.Width,
                    HlaeConfigParentFolderPath = _config.HlaeConfigParentFolderPath,
                    HlaeExePath = HlaeService.GetHlaeExePath(),
                    LaunchParameters = _config.LaunchParameters,
                    SteamExePath = AppSettings.SteamExePath(), // TODO move it?
                };

                GameLauncher launcher = new GameLauncher(config);
                await launcher.WatchDemo();
            }
            else
            {
                // just simulate that the game has been closed to start encoding
                await HandleGameClosed();
            }
        }

        /// <summary>
        /// Return FFmpeg CLI arguments based on the current configuration as a string.
        /// </summary>
        /// <returns></returns>
        public string GetFFmpegCommandLineAsString()
        {
            string command = string.Empty;
            if (FFmpegService.IsFFmpegInstalled())
            {
                command += FFmpegService.GetFFmpegExePath();
                List<string> args = GetFFmpegArgs();
                command += " " + string.Join(" ", args.ToArray());
            }

            return command;
        }

        /// <summary>
        /// Return true if the first tga file generated by the game for the current configuration exists.
        /// </summary>
        /// <returns></returns>
        public bool IsFirstTgaExists()
        {
            string firstTgaPath = GetFirstTgaPath();

            return firstTgaPath != null && File.Exists(firstTgaPath);
        }

        /// <summary>
        /// Delete the directory containing all raw files for the current configuration.
        /// </summary>
        public void DeleteRawDirectory()
        {
            string lastTakeFolderPath = GetLastTakeFolderPath();
            if (!Directory.Exists(lastTakeFolderPath))
            {
                return;
            }

            Directory.Delete(lastTakeFolderPath, true);
        }

        /// <summary>
        /// Kill all processes.
        /// </summary>
        public void Cancel()
        {
            string[] names = { "csgo", "HLAE", "ffmpeg", "Veedub64", "VirtualDub" };
            foreach (string name in names)
            {
                KillProcess(name);
            }
        }

        /// <summary>
        /// Return the directory path where TGA files are created by the game / HLAE.
        /// </summary>
        /// <returns></returns>
        private string GetTgaDirectoryPath()
        {
            string lastTakeFolderPath = GetLastTakeFolderPath();
            if (lastTakeFolderPath == null)
            {
                return null;
            }

            return lastTakeFolderPath + Path.DirectorySeparatorChar + NORMAL_NAME;
        }

        /// <summary>
        /// Return the path to the first TGA created by the game / HLAE.
        /// </summary>
        /// <returns></returns>
        private string GetFirstTgaPath()
        {
            string tgaDirectoryPath = GetTgaDirectoryPath();
            if (tgaDirectoryPath == null)
            {
                return null;
            }

            return tgaDirectoryPath + Path.DirectorySeparatorChar + "00000.tga";
        }

        /// <summary>
        /// HLAE generate an incrementing "takexxxx" folder, this function return the full path to the last one created.
        /// </summary>
        /// <returns></returns>
        private string GetLastTakeFolderPath()
        {
            if (!Directory.Exists(RawFullPath))
            {
                return null;
            }

            IEnumerable<string> directories = Directory.GetDirectories(RawFullPath).ToList();
            directories = directories.Where(dir => Path.GetFileName(dir).StartsWith("take"));

            if (directories.Count() == 0)
            {
                return null;
            }

            return directories.Last();
        }

        /// <summary>
        /// HLAE generate a .wav file named "audio_xxxxxxxx" ending with a random hex letters, this function return the name of the last one created.
        /// </summary>
        /// <returns></returns>
        private string GetLastWavFilePath()
        {
            string lastTakeFolderPath = GetLastTakeFolderPath();
            if (lastTakeFolderPath == null)
            {
                return null;
            }

            DirectoryInfo directory = new DirectoryInfo(lastTakeFolderPath);
            IEnumerable<FileInfo> files = directory.GetFiles("audio_*.wav").OrderByDescending(f => f.CreationTime);

            if (files.Count() == 0)
            {
                return null;
            }

            return files.Last().FullName;
        }

        /// <summary>
        /// Return the path where the video will be saved.
        /// </summary>
        /// <returns></returns>
        private string GetOuputFilePath()
        {
            string extension = _config.UseVirtualDub ? ".avi" : ".mp4";
            return _config.OutputFileDestinationFolder + Path.DirectorySeparatorChar + _config.OutputFilename + extension;
        }

        /// <summary>
        /// Escape path to be compatible with VDM and VirtualDub jobs scripts.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private string EscapePath(string path)
        {
            return path.Replace("\\", "\\\\");
        }

        /// <summary>
        /// Create a CFG file which will be loaded when the demo start.
        /// Users can edit it from the UI.
        /// </summary>
        private void GenerateCfgFile()
        {
            string cfgPath = GetCfgPath();
            File.WriteAllText(cfgPath, string.Join(Environment.NewLine, _config.UserCfg));
        }

        /// <summary>
        /// Return the path to the CFG which will be executed when the demo start.
        /// </summary>
        /// <returns></returns>
        private static string GetCfgPath()
        {
            string csgoPath = AppSettings.GetCsgoPath();
            if (string.IsNullOrEmpty(csgoPath))
            {
                throw new Exception("Unable to find csgo folder.");
            }

            string cfgPath = csgoPath + Path.DirectorySeparatorChar + "cfg";
            if (!Directory.Exists(cfgPath))
            {
                throw new Exception("Unable to find a cfg folder within the csgo folder.");
            }

            return cfgPath + Path.DirectorySeparatorChar + "csgodm_movie.cfg";
        }

        /// <summary>
        /// Generate the VirtualDub jobs file.
        /// </summary>
        private void GenerateVirtualDubScript()
        {
            string tgaDirectoryPath = GetTgaDirectoryPath();
            if (!Directory.Exists(tgaDirectoryPath))
            {
                throw new Exception("Directory containing TGA files does not exists");
            }

            string[] tgaFiles = Directory.GetFiles(tgaDirectoryPath, "*.tga");
            if (tgaFiles.Length == 0)
            {
                throw new Exception("No TGA files found");
            }

            string videoPath = EscapePath(GetOuputFilePath());
            if (videoPath == null)
            {
                throw new Exception("Ouput directory does not exists");
            }

            string firstTgaFilePath = EscapePath(GetFirstTgaPath());
            if (!File.Exists(firstTgaFilePath))
            {
                throw new Exception("TGA file 00000.tga not found");
            }

            string wavFilePath = EscapePath(GetLastWavFilePath());
            if (!File.Exists(wavFilePath))
            {
                throw new Exception("WAV file not found");
            }

            string vdScript = string.Format(Properties.Resources.vd, firstTgaFilePath, wavFilePath, _config.FrameRate, tgaFiles.Length, videoPath);
            string virtualDubPath = VirtualDubService.GetVirtualDubPath();
            if (!Directory.Exists(virtualDubPath))
            {
                throw new Exception("VirtualDub directory not found");
            }

            File.WriteAllText(virtualDubPath + Path.DirectorySeparatorChar + "csgodm.jobs", vdScript);
        }

        /// <summary>
        /// Generate the VDM file.
        /// </summary>
        private void GenerateVdmFile()
        {
            int actionCount = 0;

            string generated = string.Empty;

            // Step 1, execute the user's cfg / cvars
            generated += string.Format(Properties.Resources.execute_command, ++actionCount, EXEC_USER_CFG_TICK, "exec csgodm_movie.cfg");

            // Step 2, add explicitly mandatory cvars with a small delay to be sure user's cfg has been loaded
            foreach (string cmd in MandatoryCommands)
            {
                generated += string.Format(Properties.Resources.execute_command, ++actionCount, EXEC_COMMANDS_TICK, cmd);
            }

            // Step 3, execute required commands with dynamic args
            generated += string.Format(Properties.Resources.execute_command, ++actionCount, EXEC_COMMANDS_TICK,
                "host_framerate " + _config.FrameRate);

            // Step 4, set the destination directory path used by HLAE !!escaping quotes is required for vdm files!!
            generated += string.Format(Properties.Resources.execute_command, ++actionCount, EXEC_COMMANDS_TICK,
                "mirv_streams record name \\\"" + EscapePath(RawFullPath) + "\\\"");

            // Step 5, goto some ticks just before to start recording to be sure that the world has been fully loaded
            int goToTick = _config.StartTick < 256 ? 1 : _config.StartTick - 256;
            generated += string.Format(Properties.Resources.skip_ahead, ++actionCount, GOTO_TICK, goToTick);

            // Step 6, focus on player if a SteamID has been provided
            if (_config.FocusSteamId != 0)
            {
                generated += string.Format(Properties.Resources.spec_player_lock, ++actionCount, GOTO_TICK + 1, _config.FocusSteamId);
            }

            // Step 7, set HLAE options
            // Set the deaths notices lifetime using "mirv_deathmsg cfg noticeLifeTime f" (ATM)
            string command = $"mirv_deathmsg lifetime {_config.DeathsNoticesDisplayTime}";
            generated += string.Format(Properties.Resources.execute_command, ++actionCount, GOTO_TICK + 1, command);

            // Init with no red border (needs to be first)
            generated += string.Format(Properties.Resources.execute_command, ++actionCount, GOTO_TICK + 1,
                "mirv_deathmsg filter attackerIsLocal=0 victimIsLocal=0");

            // hide deaths notifications for specific players
            foreach (long steamId in _config.BlockedSteamIdList)
            {
                command = $"mirv_deathmsg filter add attackerMatch=x{steamId} block=1";
                generated += string.Format(Properties.Resources.execute_command, ++actionCount, GOTO_TICK + 1, command);
            }

            // hightlight kills for selected players
            foreach (long steamId in _config.HighlightSteamIdList)
            {
                // when the player is the attacker only
                command = $"mirv_deathmsg filter add attackerMatch=x{steamId} attackerIsLocal=1";
                generated += string.Format(Properties.Resources.execute_command, ++actionCount, GOTO_TICK + 1, command);
            }

            // Step 8, start recording
            string startCommand = $"mirv_streams add normal {NORMAL_NAME}; mirv_streams record start";
            generated += string.Format(Properties.Resources.execute_command, ++actionCount, _config.StartTick, startCommand);

            // Step 9, stop recording
            string stopCommand = "mirv_streams record end";
            generated += string.Format(Properties.Resources.execute_command, ++actionCount, _config.EndTick, stopCommand);

            // Step 10, auto close the game if the user want it otherwise stop playback (some ticks after recoding stopped)
            if (_config.AutoCloseGame)
            {
                generated += string.Format(Properties.Resources.execute_command, ++actionCount, _config.EndTick + 128, "quit");
            }
            else
            {
                generated += string.Format(Properties.Resources.stop_playback, ++actionCount, _config.EndTick + 128);
            }

            string content = string.Format(Properties.Resources.main, generated);
            File.WriteAllText(_config.Demo.GetVdmFilePath(), content);
        }

        /// <summary>
        /// Return FFmpeg CLI arguments based on the current configuration.
        /// </summary>
        /// <returns></returns>
        private List<string> GetFFmpegArgs()
        {
            List<string> args = new List<string>();
            args.Add("-y"); // override file if it exists
            args.Add("-f image2");
            args.Add("-framerate " + _config.FrameRate);
            if (!string.IsNullOrEmpty(_config.FFmpegInputParameters))
            {
                args.Add(_config.FFmpegInputParameters);
            }

            args.Add("-i \"" + GetTgaDirectoryPath() + Path.DirectorySeparatorChar + "%05d.tga\"");
            args.Add("-i \"" + GetLastWavFilePath() + "\"");
            args.Add("-vcodec " + _config.VideoCodec);
            args.Add("-qp " + _config.VideoQuality);
            args.Add("-acodec " + _config.AudioCodec);
            args.Add("-b:a " + _config.AudioBitrate + "K");
            if (!string.IsNullOrEmpty(_config.FFmpegExtraParameters))
            {
                args.Add(_config.FFmpegExtraParameters);
            }

            args.Add("\"" + GetOuputFilePath() + "\"");

            return args;
        }

        /// <summary>
        /// Callback when VirtualDub or FFMpeg is closed.
        /// </summary>
        private void HandleEncodingEnded()
        {
            // delete all RAW files generated by the game and HLAE
            if (_config.CleanUpRawFiles)
            {
                if (Directory.Exists(RawFullPath))
                {
                    Directory.Delete(RawFullPath, true);
                }
            }

            // Select the output file in the explorer
            if (_config.OpenInExplorer)
            {
                string filePath = GetOuputFilePath();
                string argument = "/select, \"" + filePath + "\"";
                Process.Start("explorer.exe", argument);
            }
        }

        /// <summary>
        /// Callback when the game is closed.
        /// </summary>
        private async Task HandleGameClosed()
        {
            OnGameClosed?.Invoke();

            // delete VDM file, we don't need it anymore
            string vdmPath = _config.Demo.GetVdmFilePath();
            if (File.Exists(vdmPath))
            {
                File.Delete(vdmPath);
            }

            // delete the CFG file, we don't need it anymore
            string cfgPath = GetCfgPath();
            if (File.Exists(cfgPath))
            {
                File.Delete(cfgPath);
            }

            // do not continue if TGA file not present, user may have close the game prematurely
            if (!IsFirstTgaExists())
            {
                return;
            }

            // generate a video file if the user want it
            if (_config.GenerateVideoFile)
            {
                if (_config.UseVirtualDub)
                {
                    // VirtualDub
                    GenerateVirtualDubScript();
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        Arguments = "/s csgodm.jobs /x",
                        FileName = VirtualDubService.GetVirtualDubExePath(),
                        WorkingDirectory = VirtualDubService.GetVirtualDubPath(),
                    };
                    Process p = new Process
                    {
                        StartInfo = psi,
                    };
                    p.Start();
                    OnVirtualDubStarted?.Invoke();
                    await p.WaitForExitAsync();
                    OnVirtualDubClosed?.Invoke();
                    HandleEncodingEnded();
                }
                else
                {
                    // FFmpeg
                    List<string> argsList = GetFFmpegArgs();
                    string args = string.Join(" ", argsList.ToArray());
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        Arguments = args,
                        FileName = FFmpegService.GetFFmpegExePath(),
                        UseShellExecute = false,
                    };
                    Process p = new Process
                    {
                        StartInfo = psi,
                    };
                    OnFFmpegStarted?.Invoke();
                    p.Start();
                    await p.WaitForExitAsync();
                    OnFFmpegClosed?.Invoke();
                    HandleEncodingEnded();
                }
            }
        }

        private Task HandleGameStarted()
        {
            return Task.Run(() => OnGameStarted?.Invoke());
        }

        private Task HandleGameRunning()
        {
            return Task.Run(() => OnGameRunning?.Invoke());
        }

        private Task HandleHLAEStarted()
        {
            return Task.Run(() => OnHLAEStarted?.Invoke());
        }

        private Task HandleHLAEClosed()
        {
            return Task.Run(() => OnHLAEClosed?.Invoke());
        }

        private static void KillProcess(string processName)
        {
            Process[] currentProcess = Process.GetProcessesByName(processName);
            if (currentProcess.Length > 0)
            {
                currentProcess[0].Kill();
            }
        }
    }
}
