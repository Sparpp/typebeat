// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// The six ENGINE events that move <see cref="TypeBeatHealthProcessor"/> without travelling on a
    /// judgement result, in one pure place. Everything else health reads arrives as a cell's osu
    /// RESULT (Great/Ok/Meh recover, a seal Miss drains, the unfixed typo and the line container are
    /// inert), which the drawables apply and the Player hands to the processor; these six are the
    /// rest, and until backlog 306 they lived as private handlers inside
    /// <c>TypeBeatPlayfield</c>, where only a full playfield could reach them.
    ///
    /// <list type="bullet">
    /// <item><see cref="TypingEngine.CharJudged"/> with <see cref="JudgementType.WrongChar"/>: the
    /// typo's drain, at the keypress (backlog 166). Raised for EVERY typed-through wrong key,
    /// including a second letter overwriting a typo PARKED on a word gap, so that overwrite drains
    /// again.</item>
    /// <item><see cref="TypingEngine.TypoErased"/>: the typo's refund.</item>
    /// <item><see cref="TypingEngine.WordAbandoned"/>: one drain per abandoned cell, at the skip
    /// (backlog 167).</item>
    /// <item><see cref="TypingEngine.AbandonReclaimed"/> and <see cref="TypingEngine.AbandonSealed"/>:
    /// the two exits from the abandoned state, each refunding what the skip took for its cells. The
    /// seal's own Miss results then drain again, so a never-reclaimed skip nets to one charge per
    /// cell.</item>
    /// <item><see cref="TypingEngine.WrongKeyRejected"/>: the mash guard, 1/13 of the bar and a fail
    /// at <see cref="TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK"/>.</item>
    /// </list>
    ///
    /// <para>PURE AND STATIC, so a headless driver can put a real <see cref="TypeBeatHealthProcessor"/>
    /// behind an engine with no playfield and no drawables: <see cref="Attach"/> subscribes exactly
    /// these six and nothing else, and the caller applies the cell results itself. The live playfield
    /// attaches through the very same call, which is what lets the browser's port of the pool
    /// (<c>typebeat-core.js</c>'s <c>HealthAccount</c>) be held against the desktop's by the
    /// server repo's cross-repo parity test rather than by inspection.</para>
    ///
    /// <para>It is a SEAM, not a rule: every hook forwards to the processor method the playfield's
    /// handler used to call, with the same argument, so no value the bar takes moves.</para>
    /// </summary>
    public static class TypeBeatHealthFeed
    {
        /// <summary>
        /// A cell judgement. Only a TYPED-THROUGH wrong character reaches health here: its cell's
        /// osu result is deferred (it may still be fixed), so its HP is settled separately, charged
        /// now and refunded by <see cref="OnTypoErased"/>. Every other judgement type either carries
        /// a result (which moves health through the Player) or, for an abandoned cell, is priced by
        /// <see cref="OnWordAbandoned"/>.
        /// </summary>
        public static void OnCharJudged(TypeBeatHealthProcessor? health, CharJudgement judgement)
        {
            if (judgement.Type == JudgementType.WrongChar)
                health?.ApplyTypoDrain();
        }

        /// <summary>A backspace took a wrong character back out of its cell: refund its drain.</summary>
        public static void OnTypoErased(TypeBeatHealthProcessor? health) => health?.RefundTypoDrain();

        /// <summary>A word skip abandoned cells: one drain per cell, now.</summary>
        public static void OnWordAbandoned(TypeBeatHealthProcessor? health, AbandonedCells abandoned)
            => health?.ApplyAbandonDrain(abandoned.Count);

        /// <summary>A backspace reclaimed abandoned cells: refund what the skip took for them.</summary>
        public static void OnAbandonReclaimed(TypeBeatHealthProcessor? health, AbandonedCells abandoned)
            => health?.RefundAbandonDrain(abandoned.Count);

        /// <summary>
        /// The line sealed on abandoned cells nobody came back for: refund the skip's drain into the
        /// Miss drain each is about to take. The engine raises this BEFORE
        /// <see cref="TypingEngine.LineSealed"/>, so the refund lands before those results do.
        /// </summary>
        public static void OnAbandonSealed(TypeBeatHealthProcessor? health, AbandonedCells abandoned)
            => health?.RefundAbandonDrain(abandoned.Count);

        /// <summary>
        /// A wrong key was rejected: the mash guard, read off the engine's streak at the moment of the
        /// rejection (<see cref="TypingEngine.ConsecutiveWrongKeys"/>, already counting this key).
        /// </summary>
        public static void OnWrongKeyRejected(TypeBeatHealthProcessor? health, int consecutiveWrongKeys)
            => health?.ApplyWrongKeyStreak(consecutiveWrongKeys);

        /// <summary>
        /// Subscribe the six hooks above to <paramref name="engine"/>, each resolving its processor
        /// through <paramref name="health"/> at the moment the event fires (so a caller whose
        /// processor is absent, a bare playfield in a test scene, feeds nothing). Dispose the result
        /// to unsubscribe.
        /// </summary>
        public static IDisposable Attach(TypingEngine engine, Func<TypeBeatHealthProcessor?> health)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(health);

            return new Subscription(engine, health);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly TypingEngine engine;
            private readonly Func<TypeBeatHealthProcessor?> health;
            private bool disposed;

            public Subscription(TypingEngine engine, Func<TypeBeatHealthProcessor?> health)
            {
                this.engine = engine;
                this.health = health;

                engine.CharJudged += onCharJudged;
                engine.TypoErased += onTypoErased;
                engine.WordAbandoned += onWordAbandoned;
                engine.AbandonReclaimed += onAbandonReclaimed;
                engine.AbandonSealed += onAbandonSealed;
                engine.WrongKeyRejected += onWrongKeyRejected;
            }

            private void onCharJudged(CharJudgement judgement) => OnCharJudged(health(), judgement);

            private void onTypoErased() => OnTypoErased(health());

            private void onWordAbandoned(AbandonedCells abandoned) => OnWordAbandoned(health(), abandoned);

            private void onAbandonReclaimed(AbandonedCells abandoned) => OnAbandonReclaimed(health(), abandoned);

            private void onAbandonSealed(AbandonedCells abandoned) => OnAbandonSealed(health(), abandoned);

            private void onWrongKeyRejected(char _) => OnWrongKeyRejected(health(), engine.ConsecutiveWrongKeys);

            public void Dispose()
            {
                if (disposed)
                    return;

                disposed = true;

                engine.CharJudged -= onCharJudged;
                engine.TypoErased -= onTypoErased;
                engine.WordAbandoned -= onWordAbandoned;
                engine.AbandonReclaimed -= onAbandonReclaimed;
                engine.AbandonSealed -= onAbandonSealed;
                engine.WrongKeyRejected -= onWrongKeyRejected;
            }
        }
    }
}
