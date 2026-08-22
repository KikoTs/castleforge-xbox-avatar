/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2026 Kiril Tsanov
This file is part of https://github.com/KikoTs/castleforge-xbox-avatar - see LICENSE.

Built against the CastleForge mod loader by RussDev7 (GPL-3.0-or-later),
https://github.com/RussDev7/CastleForge
*/

using System;
using System.IO;
using System.Reflection;
using DNA.CastleMinerZ;
using DNA.Input;
using Microsoft.Xna.Framework;
using ModLoader;

using static ModLoader.LogSystem;

namespace XboxAvatar
{
    /// <summary>
    /// Xbox Original Avatars for Castle Miner Z, as a CastleForge mod.
    ///
    /// The same renderer, avatar format and multiplayer protocol as the
    /// standalone add-on, with the executable patching removed. That add-on had
    /// to rewrite five call sites in CastleMinerZ.exe with dnlib, keep a
    /// byte-exact backup, and put it all back when the game updated. CastleForge
    /// boots a mod loader through the game's own .config, so none of that is
    /// needed here: four of the five hooks become Harmony patches and the fifth
    /// is <see cref="Tick"/>, which the loader already calls.
    /// </summary>
    [Priority(Priority.Normal)]
    [RequiredDependencies("ModLoaderExtensions")]
    public class XboxAvatar : ModBase
    {
        public XboxAvatar() : base("Xbox Avatar", new Version("1.0.0.0"))
        {
            // Unpacks the capture bridge and importer beside this mod, and
            // lets the embedded Harmony copy resolve from memory.
            EmbeddedResolver.Init();

            CastleMinerZGame game = CastleMinerZGame.Instance;
            if (game != null)
            {
                game.Exiting += delegate { Shutdown(); };
            }
        }

        public override void Start()
        {
            if (CastleMinerZGame.Instance == null)
            {
                Log("[XboxAvatar] Game instance is null; not starting.");
                return;
            }

            try
            {
                string folder = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "!Mods",
                    typeof(XboxAvatar).Namespace);
                int written = EmbeddedExporter.ExtractFolder("Tools", folder);
                if (written > 0)
                {
                    Log("[XboxAvatar] Extracted " + written + " tool file(s) to " + folder + ".");
                }
            }
            catch (Exception error)
            {
                Log("[XboxAvatar] Could not extract the importer: " + error.Message);
            }

            // Order matters. The avatar packet has to exist in the game's
            // message table before any patch can consume one, and the patches
            // have to be in place before a player is constructed.
            AvatarMessageRegistration.Register();
            GamePatches.ApplyAllPatches();

            Log("[XboxAvatar] " + Describe() + " loaded.");
        }

        /// <summary>
        /// The per-frame half of the network bridge: serving avatar chunks,
        /// expiring transfers, releasing players who left, and correcting the
        /// held-item anchor for every avatar drawn this frame.
        ///
        /// The standalone add-on had to insert a call before every return in
        /// CastleMinerZGame.Update to get this. Here the loader calls it.
        /// </summary>
        public override void Tick(InputManager inputManager, GameTime gameTime)
        {
            AvatarNetworkBridge.Update();
        }

        public static void Shutdown()
        {
            try
            {
                GamePatches.DisableAll();
                Log("[XboxAvatar] shutdown complete.");
            }
            catch (Exception error)
            {
                Log("[XboxAvatar] Error during shutdown: " + error.Message);
            }
        }

        /// <summary>What this build found to work with, for the log.</summary>
        private static string Describe()
        {
            string avatar = AvatarNetworkBridge.LocalAvatarPath;
            bool present = !string.IsNullOrEmpty(avatar) && File.Exists(avatar);
            return "avatar " + (present ? "found" : "not imported yet") +
                ", message id " + ZZAvatarSyncMessage.LocalMessageId();
        }
    }
}
