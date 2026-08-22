/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2026 Kiril Tsanov
This file is part of https://github.com/KikoTs/castleforge-xbox-avatar - see LICENSE.

The patch-application shape follows the CastleForge Example mod by RussDev7
(GPL-3.0-or-later), https://github.com/RussDev7/CastleForge
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DNA.Avatars;
using DNA.CastleMinerZ;
using DNA.Net;
using DNA.Net.GamerServices;
using HarmonyLib;

using static ModLoader.LogSystem;

namespace XboxAvatar
{
    /// <summary>
    /// The three game methods this mod intercepts, and the Harmony machinery
    /// that installs them.
    ///
    /// The standalone add-on rewrote these same three sites as raw IL, plus two
    /// more: a call after CommonAssembly.Initalize to register the avatar
    /// packet, and a call before every return in CastleMinerZGame.Update. The
    /// first is handled by <see cref="AvatarMessageRegistration"/> and the
    /// second by the loader's own per-tick callback, so neither needs a patch.
    /// </summary>
    internal static class GamePatches
    {
        private static Harmony _harmony;
        private static string _harmonyId;

        internal static void ApplyAllPatches()
        {
            _harmonyId = "castleminerz.mods.xboxavatar.patches";
            _harmony = new Harmony(_harmonyId);

            int patched = 0;
            int failed = 0;
            foreach (Type patchType in PatchTypes(typeof(GamePatches).Assembly))
            {
                try
                {
                    PatchClassProcessor processor = _harmony.CreateClassProcessor(patchType);
                    List<MethodInfo> targets = processor == null ? null : processor.Patch();
                    Log("[XboxAvatar] Patched " + patchType.FullName +
                        " (" + (targets == null ? 0 : targets.Count) + " target(s)).");
                    patched++;
                }
                catch (Exception error)
                {
                    failed++;
                    Log("[XboxAvatar] FAILED patching " + patchType.FullName + ": " +
                        error.GetType().Name + ": " + error.Message);
                }
            }

            // Say plainly whether the avatar can work at all. Every one of these
            // is load-bearing: without the player patch nobody gets an imported
            // avatar, and without the message patch avatar packets reach the
            // stock handler, which does not know them.
            if (failed > 0)
            {
                Log("[XboxAvatar] " + failed + " patch class(es) failed - the avatar will not " +
                    "render correctly on this build. Please report the log above.");
            }
            else
            {
                Log("[XboxAvatar] All " + patched + " patch class(es) applied.");
            }
        }

        internal static void DisableAll()
        {
            if (_harmony == null)
            {
                return;
            }
            _harmony.UnpatchAll(_harmonyId);
            _harmony = null;
        }

        private static IEnumerable<Type> PatchTypes(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partial)
            {
                types = partial.Types.Where(t => t != null).ToArray();
            }
            return types.Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0);
        }

        // ---------------------------------------------------------------- hooks

        /// <summary>
        /// Give a freshly built player its imported avatar.
        ///
        /// The standalone add-on replaced the "newobj" that creates the stock
        /// proxy model inside Player..ctor. A postfix is both simpler and
        /// safer: by the time it runs the constructor has already assigned
        /// Avatar and Gamer, so the replacement can be built from real values
        /// and swapped in through the same property the game itself uses.
        ///
        /// The prop part is then moved to the end of the avatar's children.
        /// Entity.Update walks children in order and a held item records the
        /// world matrix it will draw with during that walk, so the anchor
        /// correction in the imported model's own update has to happen before
        /// the item reads it.
        /// </summary>
        [HarmonyPatch(typeof(Player))]
        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPatch(new[] { typeof(NetworkGamer), typeof(AvatarDescription) })]
        internal static class PlayerConstructorPatch
        {
            // Player.ProxyModel is the model the game itself hands to the stock
            // proxy entity, and it is private static. Reading it once is
            // cheaper and less brittle than reaching into the entity the game
            // just built, whose own Model property is protected.
            private static readonly FieldInfo ProxyModelField =
                AccessTools.Field(typeof(Player), "ProxyModel");

            private static void Postfix(Player __instance)
            {
                try
                {
                    Avatar avatar = __instance.Avatar;
                    if (avatar == null || ProxyModelField == null)
                    {
                        return;
                    }
                    var fallback = ProxyModelField.GetValue(null)
                        as Microsoft.Xna.Framework.Graphics.Model;
                    if (fallback == null)
                    {
                        return;
                    }
                    AvatarNetworkBridge.AdoptPlayer(__instance.Gamer, avatar, fallback);
                }
                catch (Exception error)
                {
                    ImportedAvatarModelEntity.WriteFailure(error);
                }
            }
        }

        /// <summary>
        /// Consume avatar packets before the stock dispatcher sees them.
        ///
        /// Returning false skips the original method, which is exactly what the
        /// inserted IL did: the stock handler has no case for this message and
        /// would treat it as unknown.
        /// </summary>
        [HarmonyPatch(typeof(CastleMinerZGame), "OnMessage")]
        internal static class OnMessagePatch
        {
            private static bool Prefix(Message message)
            {
                try
                {
                    return !AvatarNetworkBridge.OnMessage(message);
                }
                catch (Exception error)
                {
                    ImportedAvatarModelEntity.WriteFailure(error);
                    return true;
                }
            }
        }

        /// <summary>
        /// Tell the bridge a gamer joined, so a reused gamer id cannot inherit
        /// the previous occupant's avatar or capability state.
        /// </summary>
        [HarmonyPatch(typeof(CastleMinerZGame), "OnGamerJoined")]
        internal static class OnGamerJoinedPatch
        {
            private static void Prefix(NetworkGamer gamer)
            {
                try
                {
                    AvatarNetworkBridge.OnGamerJoined(gamer);
                }
                catch (Exception error)
                {
                    ImportedAvatarModelEntity.WriteFailure(error);
                }
            }
        }
    }
}
