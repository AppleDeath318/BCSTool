using System;
using System.Linq;
using System.Threading.Tasks;
using System.Net.NetworkInformation;

namespace BCSTool.Services;

/// <summary>
/// Checks whether the configured server port is currently occupied.
///
/// This is a safety layer. Even if the main Bannerlord process disappears,
/// a child/orphan process may still hold the network port. Starting a second
/// server at that point would fail with "port already in use".
/// </summary>
public sealed class PortMonitor
{
    /// <summary>
    /// Checks both TCP and UDP listener tables.
    /// </summary>
    public bool IsPortInUse(int port)
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();

            if (properties.GetActiveTcpListeners().Any(x => x.Port == port))
                return true;

            if (properties.GetActiveUdpListeners().Any(x => x.Port == port))
                return true;
        }
        catch
        {
            // If Windows refuses the inspection, fail conservatively:
            // do not claim that the port is occupied unless we know it is.
        }

        return false;
    }

    /// <summary>
    /// Polls until a port becomes free or the timeout expires.
    /// </summary>
    public async Task<bool> WaitForPortFreeAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (IsPortInUse(port))
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(500, cancellationToken);
        }

        return true;
    }
}
