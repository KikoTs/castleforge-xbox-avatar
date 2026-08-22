/*
SPDX-License-Identifier: GPL-3.0-or-later
Copyright (c) 2026 Kiril Tsanov
This file is part of https://github.com/KikoTs/castleforge-xbox-avatar - see LICENSE.
*/

using System;
using System.IO;
using System.Linq;
using System.Reflection;

/// <summary>
/// Guards the wiring, which is what actually broke.
///
/// The other three tests check the avatar format, the network protocol and the
/// hand geometry, and all three passed green while the mod shipped with no
/// working capture at all: the bridge injector was never embedded in a form
/// anything would extract, the importer looked for the bridge in a folder that
/// does not exist under this layout, and the chat command was registered
/// against a type that has no commands on it. A build being green said nothing
/// about any of that.
///
/// Each check here corresponds to a bug that reached the user.
/// </summary>
internal static class ModPackagingSmoke
{
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static bool _expectCapture;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "Usage: ModPackagingSmoke <XboxAvatar.dll> [--expect-capture]");
            return 2;
        }

        // The capture bridge is optional at build time - it needs a C++/WinRT
        // toolchain, and a build without it still imports an existing avatar.
        // The caller says which kind of build this is, so "no bridge" can be a
        // deliberate choice rather than the silent omission it once was.
        _expectCapture = args.Contains("--expect-capture");
        string modPath = Path.GetFullPath(args[0]);
        string modDirectory = Path.GetDirectoryName(modPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string wanted = new AssemblyName(e.Name).Name;
            string candidate = Path.Combine(modDirectory, wanted + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        try
        {
            return Run(modPath);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL ModPackagingSmoke: " + error.Message);
            return 1;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(string modPath)
    {
        Assembly mod = Assembly.LoadFrom(modPath);
        string[] resources = mod.GetManifestResourceNames();

        // 1. Everything the mod claims to carry is actually in it. The injector
        //    was silently absent, so capture could never have started.
        foreach (string required in new[]
        {
            "XboxAvatar.Embedded.0Harmony.dll",
            "XboxAvatar.Tools.Import Xbox Avatar.exe"
        })
        {
            Require(resources.Contains(required), "the mod does not embed \"" + required + "\"");
        }

        // The capture tools are embedded under names EmbeddedResolver ignores,
        // because it would try to LoadLibrary them into an x86 process and the
        // bridge is x64. If that suffix is ever dropped, the resolver takes
        // them back and the failure returns.
        string[] captureTools = { "AvatarBridge.dll", "AvatarBridgeInjector.exe" };
        foreach (string tool in captureTools)
        {
            string expected = "XboxAvatar.Natives." + tool + ".bin";
            if (_expectCapture)
            {
                Require(resources.Contains(expected),
                    "the capture tool \"" + tool + "\" is not embedded as \"" + expected + "\"");
            }
            Require(!resources.Contains("XboxAvatar.Natives." + tool),
                "\"" + tool + "\" is embedded without the .bin suffix, so EmbeddedResolver will try to load it");
        }

        // Whatever the build decided, it must be all or nothing. Half a capture
        // is the state that shipped: the bridge present, the injector absent,
        // and an importer that could only fail once the user pressed the button.
        int embeddedTools = captureTools.Count(
            tool => resources.Contains("XboxAvatar.Natives." + tool + ".bin"));
        Require(embeddedTools == 0 || embeddedTools == captureTools.Length,
            "only " + embeddedTools + " of " + captureTools.Length +
            " capture tools are embedded; capture would fail at the point of use");
        bool hasCapture = embeddedTools == captureTools.Length;

        // 2. Extraction really writes them, under their real names, where the
        //    importer will look.
        string sandbox = Path.Combine(Path.GetTempPath(), "XboxAvatarPackaging" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            Type extractor = mod.GetType("XboxAvatar.CaptureTools", true);
            extractor.GetMethod("Extract", Hidden).Invoke(null, new object[] { sandbox });

            string natives = Path.Combine(sandbox, "Natives");
            if (hasCapture)
            {
                foreach (string tool in captureTools)
                {
                    Require(File.Exists(Path.Combine(natives, tool)),
                        "extraction did not produce Natives\\" + tool);
                }
            }

            // 3. The importer agrees on where that is. It resolves the bridge
            //    relative to its own directory, and under this layout its own
            //    directory is the avatar folder - not the game folder, which is
            //    what every other build assumes.
            string importerPath = Path.Combine(sandbox, "Import Xbox Avatar.exe");
            using (Stream source = mod.GetManifestResourceStream("XboxAvatar.Tools.Import Xbox Avatar.exe"))
            using (var target = new FileStream(importerPath, FileMode.Create, FileAccess.Write))
            {
                source.CopyTo(target);
            }
            Assembly importer = Assembly.LoadFrom(importerPath);
            Type importerType = importer.GetType("AvatarImporter", true);
            string[] bridgeSegments = (string[])importerType
                .GetField("BridgeSegments", Hidden).GetValue(null);
            string[] avatarSegments = (string[])importerType
                .GetField("AvatarSegments", Hidden).GetValue(null);

            Require(bridgeSegments.Length == 1 && bridgeSegments[0] == "Natives",
                "the importer looks for the bridge in \"" + string.Join("\\", bridgeSegments) +
                "\", but extraction writes it to \"Natives\"");
            Require(avatarSegments.Length == 0,
                "the importer expects the avatar under \"" + string.Join("\\", avatarSegments) +
                "\", but it runs from the avatar folder itself");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }

        // 4. The chat command is reachable. CommandDispatcher reflects over the
        //    runtime type it is handed, so pointing it at the mod class - which
        //    has no [Command] on it - registered nothing and every /avatar came
        //    back "Unknown command."
        Type commands = mod.GetType("XboxAvatar.AvatarCommands", true);
        Require(!commands.IsAbstract || !commands.IsSealed,
            "AvatarCommands is a static class, so CommandDispatcher cannot be given an instance of it");

        object instance = Activator.CreateInstance(commands, true);
        Type dispatcherType = Type.GetType(
            "ModLoaderExt.CommandDispatcher, ModLoaderExtensions", false);
        Require(dispatcherType != null,
            "ModLoaderExtensions could not be loaded; put it beside the mod for this test");

        object dispatcher = Activator.CreateInstance(dispatcherType, new[] { instance });
        var registered = ((System.Collections.Generic.IEnumerable<string>)dispatcherType
            .GetMethod("RegisteredCommands").Invoke(dispatcher, null)).ToArray();
        Require(registered.Contains("/avatar"),
            "no /avatar command was registered; the dispatcher found " +
            (registered.Length == 0 ? "nothing" : string.Join(" ", registered)));

        Console.WriteLine(
            "PASS ModPackagingSmoke: importer embedded, capture " +
            (hasCapture ? "tools extracted to Natives and the importer resolves that folder" : "deliberately not built in") +
            ", and " + registered.Length + " chat command(s) register: " +
            string.Join(" ", registered) + ".");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
