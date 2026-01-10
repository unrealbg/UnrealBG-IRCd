namespace IRCd.IntegrationTests.Infrastructure
{
    using System;
    using System.Net;
    using System.Net.Sockets;

    public static class TestPorts
    {
        public static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        public static (int ClientPort, int ServerPort, int ObservabilityPort) AllocateNodePorts()
            => (GetFreeTcpPort(), GetFreeTcpPort(), GetFreeTcpPort());
    }
}
