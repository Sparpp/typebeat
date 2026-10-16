// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseReadingContextTest
    {
        [TestCase("word")]
        [TestCase("line")]
        [TestCase("editor-word")]
        [TestCase("editor-line")]
        [TestCase("polyglot")]
        public void KanjiLookupCompletesWithoutPostingBackToTheWaitingEditor(string path)
        {
            var context = new WaitingEditorContext();
            Exception? error = null;
            string? reading = null;
            string? original = null;
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    if (path == "word")
                    {
                        var word = LyricOriginals.RomaniseWord("天", "japanese");
                        Assert.That(word.Flagged, Is.False);
                        reading = word.Text;
                        original = word.Source;
                    }
                    else if (path == "line")
                    {
                        var line = LyricOriginals.RomaniseLine("天", "japanese");
                        Assert.That(line.Words.Single().Flagged, Is.False);
                        reading = line.Words.Single().Text;
                        original = line.Source;
                    }
                    else if (path == "polyglot")
                    {
                        var source = new LyricLine
                        {
                            RawText = "ten", StartTime = 1000, EndTime = 3000, SingEndTime = 2500,
                            Units = new[]
                            {
                                new TimedUnit
                                {
                                    Text = "ten", Original = "天", StartTime = 1000, EndTime = 2500,
                                    SyllableBoundaries = new[] { 1500.0 }, SyllableSplits = new[] { 2 },
                                },
                            },
                        };
                        var derived = PolyglotLine.Derive(source, "japanese").Line;
                        reading = source.RawText;
                        original = derived.RawText;
                        Assert.That(derived.Units.Single().SyllableBoundaries, Is.Empty, "a single original character cannot carry two syllables");
                        Assert.That(derived.Units.Single().StartTime, Is.EqualTo(1000));
                        Assert.That(derived.Units.Single().EndTime, Is.EqualTo(2500));
                    }
                    else
                    {
                        var beatmap = new Beatmap();
                        beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
                        beatmap.Metadata.Language = BeatmapLanguage.Japanese;
                        var hitObject = new TypeBeatHitObject
                        {
                            StartTime = 1000, Granularity = TimingGranularity.Word,
                            Line = new LyricLine
                            {
                                RawText = "alpha", StartTime = 1000, EndTime = 3000, SingEndTime = 2500,
                                Units = new[] { new TimedUnit { Text = "alpha", StartTime = 1000, EndTime = 2500 } },
                            },
                        };
                        beatmap.HitObjects.Add(hitObject);
                        var editor = new EditorBeatmap(beatmap);
                        bool changed = path == "editor-word"
                            ? TypeBeatEditorOperations.SetWordOriginal(editor, hitObject, 0, "天")
                            : TypeBeatEditorOperations.SetLineText(editor, hitObject, "天");
                        Assert.That(changed, Is.True);
                        reading = hitObject.Line.RawText;
                        original = hitObject.Line.Units.Single().Original;
                        Assert.That(hitObject.Line.Units.Single().StartTime, Is.EqualTo(1000));
                        Assert.That(hitObject.Line.Units.Single().EndTime, Is.EqualTo(2500));
                        Assert.That(PolyglotLine.Derive(hitObject.Line, "japanese").Line.RawText, Is.EqualTo("天"));
                    }
                }
                catch (Exception e)
                {
                    error = e;
                }
            }) { IsBackground = true };

            thread.Start();
            bool completedWithoutPumping = thread.Join(TimeSpan.FromSeconds(5));
            if (!completedWithoutPumping)
            {
                // Release a regressed lookup after detecting the deadlock so it cannot retain the
                // shared dictionary lock and hang other tests or the test runner.
                var cleanup = Stopwatch.StartNew();
                while (!thread.Join(10) && cleanup.Elapsed < TimeSpan.FromSeconds(5))
                    context.RunPending();
            }

            Assert.That(completedWithoutPumping, Is.True, "the editor cannot pump continuations while its synchronous lookup is waiting");
            Assert.That(context.PostCount, Is.Zero, "dictionary continuations must not require the editor thread");
            Assert.That(error, Is.Null);
            Assert.That(reading, Is.EqualTo("ten"));
            Assert.That(original, Is.EqualTo("天"));
        }

        private sealed class WaitingEditorContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> pending = new ConcurrentQueue<(SendOrPostCallback, object?)>();
            private int posts;
            public int PostCount => posts;

            public override void Post(SendOrPostCallback callback, object? state)
            {
                Interlocked.Increment(ref posts);
                pending.Enqueue((callback, state));
            }

            public void RunPending()
            {
                while (pending.TryDequeue(out var work))
                    work.Callback(work.State);
            }
        }
    }
}
