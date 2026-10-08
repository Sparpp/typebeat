// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace typebeat.Build.MetalRendererPatch
{
    public static class Program
    {
        // ppy.Veldrid 4.9.69-ga405fe8484, as referenced by ppy.osu.Framework 2026.629.0.
        // Fail closed on dependency changes: this workaround must be reviewed against new upstream code.
        private const string upstream_sha256 = "61F213B3BA5E86D52517CB98AF4C4A92E94A2FCD23DF0E0DCE360621D25E8BE6";

        public static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Usage: MetalRendererPatch <upstream.dll> <patched.dll> <reference-paths.txt>");
                return 1;
            }

            try
            {
                Apply(args[0], args[1], File.ReadAllLines(args[2]));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Metal frame pacing patch failed: {exception.Message}");
                return 1;
            }
        }

        public static void Apply(string source, string destination, string[] references)
        {
            byte[] upstream = File.ReadAllBytes(source);
            if (Convert.ToHexString(SHA256.HashData(upstream)) != upstream_sha256)
                throw new InvalidOperationException("Unexpected ppy.Veldrid binary. Review the Metal frame pacing patch before updating the renderer dependency.");

            if (Path.GetFullPath(source) == Path.GetFullPath(destination))
                throw new ArgumentException("The patched assembly must be separate from the upstream assembly.");

            using var resolver = new DefaultAssemblyResolver();
            foreach (string directory in references.Append(source).Append(typeof(object).Assembly.Location)
                                                   .Select(Path.GetDirectoryName).OfType<string>().Distinct())
                resolver.AddSearchDirectory(directory);

            using var input = new MemoryStream(upstream);
            using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });
            var device = module.Types.Single(t => t.FullName == "Veldrid.MTL.MtlGraphicsDevice");
            var features = module.Types.Single(t => t.FullName == "Veldrid.MTL.MtlFeatureSupport");
            var graphicsDevice = module.Types.Single(t => t.FullName == "Veldrid.GraphicsDevice");
            var wait = device.Methods.Single(m => m.Name == "WaitForNextFrameReadyCore");
            var originalStart = wait.Body.Instructions[0];
            var il = wait.Body.GetILProcessor();

            // Equivalent source change at the start of MtlGraphicsDevice.WaitForNextFrameReadyCore:
            // if (MetalFeatures.IsMacOS && !SyncToVerticalBlank) return;
            //
            // The original body resets frameEndedEvent and waits on CVDisplayLink unconditionally.
            // Skipping BOTH operations lets the framework's selected frame limiter pace non-VSync
            // frames and keeps the display-link callback unblocked. Preserve the iOS drawable wait
            // and the entire existing VSync path, including its frame-ended handshake.
            il.InsertBefore(originalStart, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(originalStart, il.Create(OpCodes.Call, device.Methods.Single(m => m.Name == "get_MetalFeatures")));
            il.InsertBefore(originalStart, il.Create(OpCodes.Callvirt, features.Methods.Single(m => m.Name == "get_IsMacOS")));
            il.InsertBefore(originalStart, il.Create(OpCodes.Brfalse, originalStart));
            il.InsertBefore(originalStart, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(originalStart, il.Create(OpCodes.Callvirt, graphicsDevice.Methods.Single(m => m.Name == "get_SyncToVerticalBlank")));
            il.InsertBefore(originalStart, il.Create(OpCodes.Brtrue, originalStart));
            il.InsertBefore(originalStart, il.Create(OpCodes.Ret));

            using var output = new MemoryStream();
            module.Write(output);
            byte[] patched = output.ToArray();

            // Avoid making every incremental build copy an unchanged dependency again.
            if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(patched))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllBytes(destination, patched);
            Console.WriteLine("Applied macOS Metal frame pacing fix to ppy.Veldrid.");
        }
    }
}
