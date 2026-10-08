// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Veldrid;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class MetalFramePacingTest
    {
        // Exercise the shipped dependency's real wait method with managed events. Uninitialised
        // instances bypass native device creation so these regressions can also run on Linux/Windows.
        private const BindingFlags private_instance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void MacOSWithoutVSyncDoesNotWaitForRefreshOrBlockDisplayLink()
        {
            using var frameEnded = new ManualResetEvent(true);
            using var nextFrame = new AutoResetEvent(false);
            var device = createDevice(true, false, frameEnded, nextFrame);

            var wait = Task.Run(device.WaitForNextFrameReady);
            bool completed = wait.Wait(TimeSpan.FromMilliseconds(500));
            nextFrame.Set(); // Unblock the original implementation if this regression returns.
            wait.GetAwaiter().GetResult();

            Assert.That(completed, Is.True, "Non-VSync Metal should use the framework's frame limiter without waiting for a display callback.");
            Assert.That(frameEnded.WaitOne(0), Is.True, "The display-link callback must remain unblocked between non-VSync frames.");
        }

        [Test]
        public void MacOSVSyncStillWaitsForRefreshAndUsesFrameEndedHandshake()
        {
            using var frameEnded = new ManualResetEvent(true);
            using var nextFrame = new AutoResetEvent(false);
            var device = createDevice(true, true, frameEnded, nextFrame);

            var wait = Task.Run(device.WaitForNextFrameReady);
            bool reset = SpinWait.SpinUntil(() => !frameEnded.WaitOne(0), TimeSpan.FromMilliseconds(500));
            bool completedBeforeRefresh = wait.IsCompleted;
            nextFrame.Set();
            Assert.That(wait.Wait(TimeSpan.FromMilliseconds(500)), Is.True);
            wait.GetAwaiter().GetResult();

            Assert.That(reset, Is.True);
            Assert.That(completedBeforeRefresh, Is.False, "VSync must still wait for the next display callback.");
            Assert.That(frameEnded.WaitOne(0), Is.False);
        }

        [Test]
        public void ChangingVSyncTakesEffectOnTheNextFrame()
        {
            using var frameEnded = new ManualResetEvent(true);
            using var nextFrame = new AutoResetEvent(true);
            var device = createDevice(true, true, frameEnded, nextFrame);

            device.WaitForNextFrameReady();
            Assert.That(frameEnded.WaitOne(0), Is.False);
            frameEnded.Set(); // SwapBuffers completes the VSync frame.
            setField(device.MainSwapchain, "syncToVerticalBlank", false);
            device.WaitForNextFrameReady();
            Assert.That(frameEnded.WaitOne(0), Is.True);
            Assert.That(nextFrame.WaitOne(0), Is.False);

            setField(device.MainSwapchain, "syncToVerticalBlank", true);
            nextFrame.Set();
            device.WaitForNextFrameReady();
            Assert.That(frameEnded.WaitOne(0), Is.False);
            Assert.That(nextFrame.WaitOne(0), Is.False);
        }

        [Test]
        public void IOSKeepsItsDrawableAvailabilityWait()
        {
            using var frameEnded = new ManualResetEvent(true);
            using var nextFrame = new AutoResetEvent(true);
            var device = createDevice(false, false, frameEnded, nextFrame);

            // The fake swapchain has no framebuffer. Reaching that lookup proves the iOS path
            // still requests a drawable instead of taking the macOS-only early return.
            Assert.Throws<NullReferenceException>(() => device.WaitForNextFrameReady());
            Assert.That(frameEnded.WaitOne(0), Is.False);
            Assert.That(nextFrame.WaitOne(0), Is.False);
        }

        private static GraphicsDevice createDevice(bool macOS, bool vSync, ManualResetEvent frameEnded, AutoResetEvent nextFrame)
        {
            var assembly = typeof(GraphicsDevice).Assembly;
            var device = (GraphicsDevice)RuntimeHelpers.GetUninitializedObject(assembly.GetType("Veldrid.MTL.MtlGraphicsDevice", true)!);
            var features = RuntimeHelpers.GetUninitializedObject(assembly.GetType("Veldrid.MTL.MtlFeatureSupport", true)!);
            var swapchain = RuntimeHelpers.GetUninitializedObject(assembly.GetType("Veldrid.MTL.MtlSwapchain", true)!);

            setField(features, "<IsMacOS>k__BackingField", macOS);
            setField(device, "<MetalFeatures>k__BackingField", features);
            setField(device, "mainSwapchain", swapchain);
            setField(device, "frameEndedEvent", frameEnded);
            setField(device, "nextFrameReadyEvent", nextFrame);
            setField(swapchain, "syncToVerticalBlank", vSync);
            return device;
        }

        private static void setField(object instance, string name, object value)
            => instance.GetType().GetField(name, private_instance)!.SetValue(instance, value);
    }
}
