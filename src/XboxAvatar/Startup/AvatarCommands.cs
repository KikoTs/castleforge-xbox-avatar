/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2026 Kiril Tsanov
This file is part of https://github.com/KikoTs/castleforge-xbox-avatar - see LICENSE.
*/

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ModLoaderExt;

using static ModLoader.LogSystem;

namespace XboxAvatar
{
    /// <summary>
    /// The mod's chat commands.
    ///
    /// Capturing an avatar used to mean leaving the game, finding an
    /// executable in the game folder and running it, then restarting so the
    /// new file was picked up. Both halves of that are avoidable: the importer
    /// can be started from here, and the avatar it writes can be loaded without
    /// a restart.
    /// </summary>
    internal static class AvatarCommands
    {
        internal static readonly (string command, string description)[] Commands =
        {
            ("avatar",         "Show what the avatar mod is currently using."),
            ("avatar import",  "Capture your Xbox Original Avatar and load it."),
            ("avatar reload",  "Re-read avatar.ocavatar without restarting."),
            ("avatar grip",    "Set how far the first-person hand closes, 0 to 1.")
        };

        private static Process _importer;

        [Command("/avatar")]
        private static void Execute(string[] args)
        {
            try
            {
                string action = args != null && args.Length > 0
                    ? args[0].ToLowerInvariant()
                    : "status";

                switch (action)
                {
                    case "import":
                        Import();
                        break;
                    case "reload":
                        SendFeedback(AvatarNetworkBridge.ReloadLocalAvatar());
                        break;
                    case "grip":
                        Grip(args);
                        break;
                    case "status":
                        Status();
                        break;
                    default:
                        SendFeedback("Unknown option. Try: /avatar, /avatar import, /avatar reload, /avatar grip <0-1>");
                        break;
                }
            }
            catch (Exception error)
            {
                SendFeedback("ERROR: " + error.Message);
                Log("[XboxAvatar] Command failed: " + error);
            }
        }

        private static void Status()
        {
            string path = AvatarNetworkBridge.LocalAvatarPath;
            bool present = !string.IsNullOrEmpty(path) && File.Exists(path);
            SendFeedback("Avatar: " + (present
                ? "loaded (" + new FileInfo(path).Length / 1024 + " KB)"
                : "none imported yet - use /avatar import"));
            SendFeedback("Network: message id " + ZZAvatarSyncMessage.LocalMessageId() +
                ", " + ItemTuning.Describe());
        }

        /// <summary>
        /// Runs the importer, which drives the capture bridge against the Xbox
        /// Original Avatars app, then loads whatever it wrote.
        ///
        /// Started as a separate process on purpose. The bridge is x64 because
        /// the Avatars app is, and this game is x86, so the capture cannot
        /// happen inside it however convenient that would be.
        /// </summary>
        private static void Import()
        {
            if (_importer != null && !_importer.HasExited)
            {
                SendFeedback("The importer is already open.");
                return;
            }

            string folder = AvatarNetworkBridge.AvatarFolderPath;
            string importer = Path.Combine(folder, "Import Xbox Avatar.exe");
            if (!File.Exists(importer))
            {
                SendFeedback("The importer is missing from " + folder + ".");
                return;
            }

            _importer = Process.Start(new ProcessStartInfo
            {
                FileName = importer,
                WorkingDirectory = folder,
                UseShellExecute = true
            });
            SendFeedback("Importer opened. Leave the avatar you want on screen, confirm the capture,");
            SendFeedback("then come back and type /avatar reload.");

            if (_importer != null)
            {
                // Offer to finish the job rather than making them type it, but
                // never touch the game from this thread: the process exit
                // callback is not the game's, so only a flag is set here and
                // Tick does the work.
                _importer.EnableRaisingEvents = true;
                _importer.Exited += delegate { ImportFinished = true; };
            }
        }

        /// <summary>
        /// Set by the importer's exit handler, acted on by the next tick. The
        /// exit event arrives on a thread pool thread, and rebuilding a model
        /// touches the graphics device, which belongs to the game's thread.
        /// </summary>
        internal static volatile bool ImportFinished;

        internal static void PumpImportResult()
        {
            if (!ImportFinished)
            {
                return;
            }
            ImportFinished = false;
            SendFeedback(AvatarNetworkBridge.ReloadLocalAvatar());
        }

        private static void Grip(string[] args)
        {
            if (args == null || args.Length < 2)
            {
                SendFeedback("Current grip is " + ItemTuning.Grip.ToString("F2", CultureInfo.InvariantCulture) +
                    ". Use /avatar grip <0-1>.");
                return;
            }
            float value;
            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                value < 0f || value > 1.5f)
            {
                SendFeedback("Give a number between 0 and 1.");
                return;
            }

            // Written to the tuning file rather than held in memory, so it
            // survives a restart and so the file stays the one place the
            // setting lives. The runtime re-reads it within a second.
            string path = Path.Combine(AvatarNetworkBridge.AvatarFolderPath, "item-tuning.txt");
            try
            {
                var lines = File.Exists(path)
                    ? new System.Collections.Generic.List<string>(File.ReadAllLines(path))
                    : new System.Collections.Generic.List<string>();
                bool replaced = false;
                for (int index = 0; index < lines.Count; index++)
                {
                    string trimmed = lines[index].TrimStart();
                    if (trimmed.StartsWith("grip", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[index] = "grip " + value.ToString("0.##", CultureInfo.InvariantCulture);
                        replaced = true;
                        break;
                    }
                }
                if (!replaced)
                {
                    lines.Add("grip " + value.ToString("0.##", CultureInfo.InvariantCulture));
                }
                File.WriteAllLines(path, lines.ToArray());
                SendFeedback("Grip set to " + value.ToString("0.##", CultureInfo.InvariantCulture) + ".");
            }
            catch (Exception error)
            {
                SendFeedback("Could not write the tuning file: " + error.Message);
            }
        }
    }
}
