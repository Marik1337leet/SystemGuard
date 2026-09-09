using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace SystemGuard.Desktop.Services;

// Wake-on-LAN: magic packet на MAC-адрес.
public static class WakeOnLanService
{
    public static byte[] BuildMagicPacket(string mac)
    {
        var hex = new string(mac.Where(c => Uri.IsHexDigit(c)).ToArray());
        if (hex.Length != 12) throw new FormatException("MAC must contain 12 hex digits");
        var addr = Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Array.Copy(addr, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    public static string Send(string mac, string broadcastIp = "255.255.255.255", int port = 9)
    {
        try
        {
            var packet = BuildMagicPacket(mac);
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Parse(broadcastIp), port));
            return $"Magic packet sent to {mac}";
        }
        catch (Exception ex) { return $"WoL failed: {ex.Message}"; }
    }
}
