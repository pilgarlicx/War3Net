﻿// ------------------------------------------------------------------------------
// <copyright file="RacePreferenceProvider.cs" company="Drake53">
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.
// </copyright>
// ------------------------------------------------------------------------------

using War3Net.Build.Info;

namespace War3Net.Build.Providers
{
    internal static class RacePreferenceProvider
    {
        public static string GetRacePreferenceString(PlayerRace playerRace)
        {
            switch (playerRace)
            {
                case PlayerRace.Human: return nameof(War3Api.Common.RACE_PREF_HUMAN);
                case PlayerRace.Orc: return nameof(War3Api.Common.RACE_PREF_ORC);
                case PlayerRace.NightElf: return nameof(War3Api.Common.RACE_PREF_NIGHTELF);
                case PlayerRace.Undead: return nameof(War3Api.Common.RACE_PREF_UNDEAD);
                // case Demon: return nameof(War3Api.Common.RACE_PREF_DEMON);
                case PlayerRace.Selectable: // return nameof(War3Api.Common.RACE_PREF_USER_SELECTABLE);
                default: return nameof(War3Api.Common.RACE_PREF_RANDOM);
            }
        }
    }
}