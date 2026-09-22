/*
Technitium DNS Server
Copyright (C) 2026  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace DnsServerCore.Dns
{
    //Implements the systemd socket activation protocol (sd_listen_fds / sd_listen_fds_with_names)
    //so the DNS service can accept listening sockets handed over by systemd or Podman.

    static class SystemdSocketActivation
    {
        #region variables

        const int SD_LISTEN_FDS_START = 3;
        const string LISTEN_SOCKET_SUFFIX = ".socket";

        //well known file descriptor names, set via FileDescriptorName= in the socket unit
        public const string NAME_DNS = "dns";               //Do53: UDP and TCP, usually port 53
        public const string NAME_DNS_TLS = "dns-tls";       //DNS-over-TLS: TCP, usually port 853
        public const string NAME_DNS_HTTPS = "dns-https";   //DNS-over-HTTPS: TCP, usually port 443

        static readonly object _lock = new object();
        static bool _initialized;

        //inherited sockets grouped by descriptor name, kept open for the lifetime of the process
        static readonly Dictionary<string, List<Socket>> _inheritedSockets = new Dictionary<string, List<Socket>>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region private

        static void EnsureInitialized()
        {
            if (_initialized)
                return;

            lock (_lock)
            {
                if (_initialized)
                    return;

                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    try
                    {
                        Initialize();
                    }
                    catch
                    { }
                }

                _initialized = true;
            }
        }

        static void Initialize()
        {
            string listenPid = Environment.GetEnvironmentVariable("LISTEN_PID");
            string listenFds = Environment.GetEnvironmentVariable("LISTEN_FDS");
            string listenFdNames = Environment.GetEnvironmentVariable("LISTEN_FDNAMES");

            //unset the variables so that child processes do not inherit them
            Environment.SetEnvironmentVariable("LISTEN_PID", null);
            Environment.SetEnvironmentVariable("LISTEN_FDS", null);
            Environment.SetEnvironmentVariable("LISTEN_FDNAMES", null);

            if ((listenPid is null) || (listenFds is null))
                return;

            if (!int.TryParse(listenPid, out int pid) || (pid != Environment.ProcessId))
                return; //the fds were passed to a different process

            if (!int.TryParse(listenFds, out int count) || (count <= 0))
                return;

            string[] names = listenFdNames is null ? [] : listenFdNames.Split(':');
            bool hasNames = names.Length == count;

            for (int i = 0; i < count; i++)
            {
                int fd = SD_LISTEN_FDS_START + i;

                string name;
                if (hasNames && !string.IsNullOrEmpty(names[i]) && (names[i] != "unknown"))
                    name = NormalizeName(names[i]);
                else
                    name = NAME_DNS; //no usable name was provided, assume Do53

                Socket socket;
                try
                {
                    socket = new Socket(new SafeSocketHandle(new IntPtr(fd), ownsHandle: true));
                }
                catch
                {
                    continue;
                }

                if (!_inheritedSockets.TryGetValue(name, out List<Socket> list))
                {
                    list = new List<Socket>(1);
                    _inheritedSockets.Add(name, list);
                }

                list.Add(socket);
            }
        }

        static string NormalizeName(string name)
        {
            name = name.Trim();

            if (name.EndsWith(LISTEN_SOCKET_SUFFIX, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - LISTEN_SOCKET_SUFFIX.Length);

            return name;
        }

        #endregion

        #region public

        public static bool IsActivated
        {
            get
            {
                EnsureInitialized();

                lock (_lock)
                {
                    return _inheritedSockets.Count > 0;
                }
            }
        }

        //Returns the inherited listening sockets for the given descriptor names.
        public static IReadOnlyList<Socket> GetListenerSockets(params string[] names)
        {
            EnsureInitialized();

            List<Socket> sockets = new List<Socket>();

            lock (_lock)
            {
                foreach (string name in names)
                {
                    if (_inheritedSockets.TryGetValue(NormalizeName(name), out List<Socket> inherited))
                        sockets.AddRange(inherited);
                }
            }

            return sockets;
        }

        //Returns every descriptor name that was handed over, for logging unrecognized ones.
        public static IReadOnlyCollection<string> GetActivatedNames()
        {
            EnsureInitialized();

            lock (_lock)
            {
                return new List<string>(_inheritedSockets.Keys);
            }
        }

        //Disposes and forgets the inherited sockets registered under the given descriptor name.
        public static void DisposeListenerSockets(string name)
        {
            EnsureInitialized();

            List<Socket> removed;

            lock (_lock)
            {
                if (!_inheritedSockets.Remove(NormalizeName(name), out removed))
                    return;
            }

            foreach (Socket socket in removed)
            {
                try
                {
                    socket.Dispose();
                }
                catch
                { }
            }
        }

        #endregion
    }
}
