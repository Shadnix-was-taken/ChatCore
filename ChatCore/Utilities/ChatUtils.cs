﻿using ChatCore.Interfaces;
using ChatCore.Models.Mixer;
using ChatCore.Models.Twitch;
using ChatCore.Services.Mixer;
using ChatCore.Services.Twitch;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace ChatCore
{
    public static class ChatUtils
    {
        public static TwitchService AsTwitchService(this IChatService svc)
        {
            return svc as TwitchService;
        }

        public static TwitchMessage AsTwitchMessage(this IChatMessage msg)
        {
            return msg as TwitchMessage;
        }

        public static TwitchChannel AsTwitchChannel(this IChatChannel channel)
        {
            return channel as TwitchChannel;
        }

        public static TwitchUser AsTwitchUser(this IChatUser user)
        {
            return user as TwitchUser;
        }

        public static TwitchBadge AsTwitchBadge(this IChatBadge badge)
        {
            return badge as TwitchBadge;
        }

        public static TwitchEmote AsTwitchEmote(this IChatEmote emote)
        {
            return emote as TwitchEmote;
        }


        public static MixerService AsMixerService(this IChatService svc)
        {
            return svc as MixerService;
        }

        public static MixerMessage AsMixerMessage(this IChatMessage msg)
        {
            return msg as MixerMessage;
        }

        public static MixerChannel AsMixerChannel(this IChatChannel channel)
        {
            return channel as MixerChannel;
        }

        public static MixerUser AsMixerUser(this IChatUser user)
        {
            return user as MixerUser;
        }

        //public static MixerBadge AsMixerBadge(this IChatBadge badge)
        //{
        //    return badge as MixerBadge;
        //}

        public static MixerEmote AsMixerEmote(this IChatEmote emote)
        {
            return emote as MixerEmote;
        }

        private static ConcurrentDictionary<int, string> _userColors = new ConcurrentDictionary<int, string>();
        public static string GetNameColor(string name)
        {
            int nameHash = name.GetHashCode();
            if (!_userColors.TryGetValue(nameHash, out var nameColor))
            {
                // Generate a psuedo-random color based on the users display name
                Random rand = new Random(nameHash);
                int argb = (rand.Next(255) << 16) + (rand.Next(255) << 8) + rand.Next(255);
                string colorString = string.Format("#{0:X6}FF", argb);
                _userColors.TryAdd(nameHash, colorString);
                nameColor = colorString;
            }
            return nameColor;
        }
    }
}
