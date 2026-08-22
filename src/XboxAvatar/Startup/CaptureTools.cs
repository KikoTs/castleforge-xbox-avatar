/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2026 Kiril Tsanov
This file is part of https://github.com/KikoTs/castleforge-xbox-avatar - see LICENSE.
*/

using System;
using System.IO;
using System.Reflection;

using static ModLoader.LogSystem;

namespace XboxAvatar
{
    /// <summary>
    /// Unpacks the avatar capture tools next to the mod.
    ///
    /// These deliberately do not go through EmbeddedResolver, which exists to
    /// make a native library loadable by this process. Neither of these is:
    ///
    /// - AvatarBridgeInjector.exe is a program, and the resolver only considers
    ///   resources whose name ends in ".dll", so it would never appear at all.
    /// - AvatarBridge.dll is x64, because it is injected into the Xbox Original
    ///   Avatars app, which is x64. Castle Miner Z is x86. Handing it to
    ///   LoadLibrary in this process can only fail - it did, with error 193,
    ///   bad EXE format - and succeeding would be worse than failing.
    ///
    /// So both are embedded under names the resolver ignores and written out
    /// here with their real names, for the importer to launch as separate
    /// processes.
    /// </summary>
    internal static class CaptureTools
    {
        private const string ResourcePrefix = "XboxAvatar.Natives.";
        private const string ResourceSuffix = ".bin";

        internal static void Extract(string modFolder)
        {
            Assembly assembly = typeof(CaptureTools).Assembly;
            string nativesFolder = Path.Combine(modFolder, "Natives");
            int written = 0;

            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    !resource.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string name = resource.Substring(
                    ResourcePrefix.Length,
                    resource.Length - ResourcePrefix.Length - ResourceSuffix.Length);

                try
                {
                    using (Stream source = assembly.GetManifestResourceStream(resource))
                    {
                        if (source == null)
                        {
                            continue;
                        }
                        Directory.CreateDirectory(nativesFolder);
                        string target = Path.Combine(nativesFolder, name);

                        // Rewrite only when the size differs, so a running
                        // capture cannot be replaced underneath itself on every
                        // launch, and a locked file is not an error.
                        if (File.Exists(target) && new FileInfo(target).Length == source.Length)
                        {
                            continue;
                        }
                        using (var destination = new FileStream(
                            target, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            source.CopyTo(destination);
                        }
                        written++;
                    }
                }
                catch (Exception error)
                {
                    Log("[XboxAvatar] Could not write " + name + ": " + error.Message);
                }
            }

            if (written > 0)
            {
                Log("[XboxAvatar] Extracted " + written + " capture tool(s) to " + nativesFolder + ".");
            }
        }
    }
}
