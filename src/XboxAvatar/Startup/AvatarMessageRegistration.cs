/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2026 Kiril Tsanov
This file is part of https://github.com/KikoTs/castleforge-xbox-avatar - see LICENSE.
*/

using System;
using System.Reflection;
using DNA.Net;
using DNA.Reflection;

using static ModLoader.LogSystem;

namespace XboxAvatar
{
    /// <summary>
    /// Gets the avatar packet into the game's network message table after the
    /// game has already built it.
    ///
    /// Message IDs are positional: DNA collects every Message subclass in the
    /// registered assemblies, sorts them by name, and the index is the ID on
    /// the wire. That table is built once, in Message's static constructor,
    /// during startup - long before a mod loader hands control to a mod. The
    /// patched-executable builds sidestepped this by inserting their
    /// registration call right after CommonAssembly.Initalize, before anything
    /// touched Message.
    ///
    /// A mod cannot be that early, so it registers late and rebuilds the table.
    /// That is safe here for one specific reason: this message type sorts after
    /// every stock DNA type, so rebuilding appends it and leaves every stock ID
    /// exactly where it was. AvatarMessageIdSmoke guards that property, and a
    /// peer whose ID differs from ours is detected and left on the stock model
    /// rather than sent packets its game would misread.
    ///
    /// Rebuilding also resets the send and receive instance caches, which is
    /// why this runs at load, before any session exists.
    /// </summary>
    internal static class AvatarMessageRegistration
    {
        private static bool _registered;

        internal static void Register()
        {
            if (_registered)
            {
                return;
            }

            try
            {
                // Make this assembly's Message subclasses visible to DNA's
                // type scan. Passing DNA.Common as the second argument mirrors
                // what CommonAssembly.Initalize does for the engine itself.
                ReflectionTools.RegisterAssembly(
                    typeof(AvatarMessageRegistration).Assembly,
                    typeof(Message).Assembly);

                int before = ZZAvatarSyncMessage.LocalMessageId();
                RebuildMessageTable();
                int after = ZZAvatarSyncMessage.LocalMessageId();

                if (after < 0)
                {
                    Log("[XboxAvatar] The avatar packet did not register; " +
                        "multiplayer avatar sync is disabled for this session.");
                    return;
                }

                _registered = true;
                Log("[XboxAvatar] Avatar packet registered at message id " + after +
                    (before == after ? " (already present)." : "."));
            }
            catch (Exception error)
            {
                Log("[XboxAvatar] Could not register the avatar packet: " + error.Message +
                    ". Multiplayer avatar sync is disabled for this session.");
            }
        }

        /// <summary>
        /// Re-runs DNA's own message-table population.
        ///
        /// Private, so it has to be reached by reflection. If a future engine
        /// build renames it the mod still loads - single player and the
        /// first-person hand do not depend on the network at all - so this
        /// reports rather than throws.
        /// </summary>
        private static void RebuildMessageTable()
        {
            MethodInfo populate = typeof(Message).GetMethod(
                "PopulateMessageTypes",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (populate == null)
            {
                throw new MissingMethodException(
                    "DNA.Net.Message.PopulateMessageTypes was not found on this build.");
            }
            populate.Invoke(null, null);
        }
    }
}
