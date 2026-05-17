using System;
using System.Net.NetworkInformation;
using System.Linq;

namespace Hostix.Runtime.Utils
{
    public static class PortScanner
    {
        public static bool IsPortAvailable(int port)
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            
            // Check TCP listeners
            var tcpListeners = properties.GetActiveTcpListeners();
            if (tcpListeners.Any(l => l.Port == port)) return false;

            // Check Active TCP connections
            var tcpConnections = properties.GetActiveTcpConnections();
            if (tcpConnections.Any(c => c.LocalEndPoint.Port == port)) return false;

            // Check UDP listeners
            var udpListeners = properties.GetActiveUdpListeners();
            if (udpListeners.Any(l => l.Port == port)) return false;

            return true;
        }
    }
}
