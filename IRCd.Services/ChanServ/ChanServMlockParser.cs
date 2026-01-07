namespace IRCd.Services.ChanServ
{
    using System;
    using System.Collections.Generic;

    using IRCd.Core.State;

    internal static class ChanServMlockParser
    {
        public static bool TryParse(string input, out ChannelMlock mlock, out string? error)
        {
            mlock = new ChannelMlock();
            error = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "Missing mode string";
                return false;
            }

            var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                error = "Missing mode string";
                return false;
            }

            var modeToken = tokens[0];
            if (modeToken.Length == 0 || (modeToken[0] != '+' && modeToken[0] != '-'))
            {
                error = "Invalid mode string";
                return false;
            }

            var set = ChannelModes.None;
            var clear = ChannelModes.None;
            var keyLocked = false;
            string? key = null;
            var limitLocked = false;
            int? limit = null;

            var sign = modeToken[0];
            var argIdx = 1;

            for (int i = 1; i < modeToken.Length; i++)
            {
                var c = modeToken[i];
                if (c == '+' || c == '-')
                {
                    sign = c;
                    continue;
                }

                var enable = sign == '+';

                switch (c)
                {
                    case 'n':
                        Apply(ref set, ref clear, ChannelModes.NoExternalMessages, enable);
                        break;
                    case 't':
                        Apply(ref set, ref clear, ChannelModes.TopicOpsOnly, enable);
                        break;
                    case 'i':
                        Apply(ref set, ref clear, ChannelModes.InviteOnly, enable);
                        break;
                    case 'm':
                        Apply(ref set, ref clear, ChannelModes.Moderated, enable);
                        break;
                    case 'p':
                        Apply(ref set, ref clear, ChannelModes.Private, enable);
                        break;
                    case 's':
                        Apply(ref set, ref clear, ChannelModes.Secret, enable);
                        break;
                    case 'k':
                        keyLocked = true;
                        if (enable)
                        {
                            if (tokens.Length <= argIdx)
                            {
                                error = "Missing +k key";
                                return false;
                            }

                            key = tokens[argIdx++];
                        }
                        else
                        {
                            key = null;
                        }

                        break;
                    case 'l':
                        limitLocked = true;
                        if (enable)
                        {
                            if (tokens.Length <= argIdx)
                            {
                                error = "Missing +l limit";
                                return false;
                            }

                            var raw = tokens[argIdx++];
                            if (!int.TryParse(raw, out var parsed) || parsed <= 0)
                            {
                                error = "Invalid +l limit";
                                return false;
                            }

                            limit = parsed;
                        }
                        else
                        {
                            limit = null;
                        }

                        break;

                    default:
                        // Ignore unsupported modes for v1.
                        break;
                }
            }

            mlock = new ChannelMlock
            {
                SetModes = set,
                ClearModes = clear,
                KeyLocked = keyLocked,
                Key = key,
                LimitLocked = limitLocked,
                Limit = limit,
                Raw = input.Trim()
            };

            return true;
        }

        private static void Apply(ref ChannelModes set, ref ChannelModes clear, ChannelModes mode, bool enable)
        {
            if (enable)
            {
                set |= mode;
                clear &= ~mode;
            }
            else
            {
                clear |= mode;
                set &= ~mode;
            }
        }

        public static string Describe(ChannelMlock mlock)
        {
            if (mlock is null)
            {
                return "";
            }

            return string.IsNullOrWhiteSpace(mlock.Raw) ? "(empty)" : mlock.Raw;
        }
    }
}
