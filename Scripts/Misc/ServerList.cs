#region References
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

using Server.Network;
#endregion

namespace Server.Misc
{
	public class ServerList
    {
        public static string ServerName { get; } = Config.Get("Server.Name", "My Shard");

        public static IPAddress Address { get; } = Config.Get("Server.Address", IPAddress.Loopback);

        public static void Initialize()
        {
            Console.Title = ServerName;

			EventSink.ServerList += EventSink_ServerList;
		}

		private static void EventSink_ServerList(ServerListEventArgs e)
		{
			try
            {
                var ns = e.State;
                var s = ns.Socket;

                var ipep = (IPEndPoint)s.LocalEndPoint;

                var address = ipep.Address;

                if (!IPAddress.IsLoopback(address) || IsPrivateNetwork(address))
                {
                    address = Address;
                }

                e.AddServer(ServerName, new IPEndPoint(address, ipep.Port));
            }
			catch
			{
				e.Rejected = true;
			}
		}

		private static bool IsPrivateNetwork(IPAddress ip)
		{
			// 10.0.0.0/8
			// 172.16.0.0/12
			// 192.168.0.0/16
			// 169.254.0.0/16
			// 100.64.0.0/10 RFC 6598

			if (ip.AddressFamily == AddressFamily.InterNetworkV6)
			{
				return false;
			}

			if (Utility.IPMatch("192.168.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("10.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("172.16-31.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("169.254.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("100.64-127.*", ip))
			{
				return true;
			}

			return false;
		}
	}
}
