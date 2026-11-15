// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Scoring.Legacy
{
    public static class ScoreInfoExtensions
    {
        public static int? GetCountGeki(this IScoreInfo scoreInfo)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                case 1:
                    return getCount(scoreInfo, HitResult.LargeBonus);

                case 3:
                    return getCount(scoreInfo, HitResult.Perfect);
            }

            return null;
        }

        public static void SetCountGeki(this ScoreInfo scoreInfo, int value)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                // For legacy scores, Geki indicates hit300 + perfect strong note hit.
                // Lazer only has one result for a perfect strong note hit (LargeBonus).
                case 1:
                    scoreInfo.Statistics[HitResult.LargeBonus] = scoreInfo.Statistics.GetValueOrDefault(HitResult.LargeBonus) + value;
                    break;

                case 3:
                    scoreInfo.Statistics[HitResult.Perfect] = value;
                    break;
            }
        }

        public static int? GetCount300(this IScoreInfo scoreInfo) => getCount(scoreInfo, HitResult.Great);

        public static void SetCount300(this ScoreInfo scoreInfo, int value) => scoreInfo.Statistics[HitResult.Great] = value;

        public static int? GetCountKatu(this IScoreInfo scoreInfo)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                // For taiko, Katu is bundled into Geki.
                case 1:
                    break;

                case 2:
                    return getCount(scoreInfo, HitResult.SmallTickMiss);

                case 3:
                    return getCount(scoreInfo, HitResult.Good);
            }

            return null;
        }

        public static void SetCountKatu(this ScoreInfo scoreInfo, int value)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                // For legacy scores, Katu indicates hit100 + perfect strong note hit.
                // Lazer only has one result for a perfect strong note hit (LargeBonus).
                case 1:
                    scoreInfo.Statistics[HitResult.LargeBonus] = scoreInfo.Statistics.GetValueOrDefault(HitResult.LargeBonus) + value;
                    break;

                case 2:
                    scoreInfo.Statistics[HitResult.SmallTickMiss] = value;
                    break;

                case 3:
                    scoreInfo.Statistics[HitResult.Good] = value;
                    break;
            }
        }

        public static int? GetCount100(this IScoreInfo scoreInfo)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                case 0:
                case 1:
                case 3:
                    return getCount(scoreInfo, HitResult.Ok);

                case 2:
                    return getCount(scoreInfo, HitResult.LargeTickHit);
            }

            return null;
        }

        public static void SetCount100(this ScoreInfo scoreInfo, int value)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                case 0:
                case 1:
                case 3:
                    scoreInfo.Statistics[HitResult.Ok] = value;
                    break;

                case 2:
                    scoreInfo.Statistics[HitResult.LargeTickHit] = value;
                    break;
            }
        }

        public static int? GetCount50(this IScoreInfo scoreInfo)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                case 0:
                case 3:
                    return getCount(scoreInfo, HitResult.Meh);

                case 2:
                    return getCount(scoreInfo, HitResult.SmallTickHit);
            }

            return null;
        }

        public static void SetCount50(this ScoreInfo scoreInfo, int value)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                case 0:
                case 3:
                    scoreInfo.Statistics[HitResult.Meh] = value;
                    break;

                case 2:
                    scoreInfo.Statistics[HitResult.SmallTickHit] = value;
                    break;
            }
        }

        public static int? GetCountMiss(this IScoreInfo scoreInfo)
        {
            switch (scoreInfo.Ruleset.OnlineID)
            {
                case 0:
                case 1:
                case 3:
                    return getCount(scoreInfo, HitResult.Miss);

                case 2:
                    return (getCount(scoreInfo, HitResult.Miss) ?? 0) + (getCount(scoreInfo, HitResult.LargeTickMiss) ?? 0);
            }

            return null;
        }

        public static void SetCountMiss(this ScoreInfo scoreInfo, int value) =>
            // this does not match the implementation of `GetCountMiss()` for catch,
            // but we physically cannot recover that data anymore at this point.
            scoreInfo.Statistics[HitResult.Miss] = value;

        private static int? getCount(IScoreInfo scoreInfo, HitResult result)
        {
            if (scoreInfo.Statistics.TryGetValue(result, out int existing))
                return existing;

            return null;
        }
    }
}
