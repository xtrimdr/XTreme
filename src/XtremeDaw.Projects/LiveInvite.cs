using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace XtremeDaw.Projects;

/// <summary>
/// The invitation to a shared session (#291, decided with the user: a code, or a link with it): where the host listens
/// (its addresses on the local network and on a VPN such as Tailscale or ZeroTier, and the port) and the session's key,
/// in one line to paste. Whoever has it can join, so it travels only to the people invited; the key encrypts and
/// signs every message (<see cref="LiveLink"/>), so without it nothing gets in or can be read.
/// </summary>
public sealed record LiveInvite(IReadOnlyList<IPAddress> Addresses, int Port, byte[] Key)
{
    const byte Format = 1;
    public const int KeyBytes = 16;
    /// <summary>Addresses the code takes at most: it stays short enough to dictate or paste.</summary>
    const int MaxAddresses = 4;
    /// <summary>The link that carries the same code.</summary>
    public const string LinkPrefix = "xtreme://unirse/";
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static LiveInvite New(int port) => new(LocalAddresses(), port, RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>
    /// This PC's IPv4 addresses that others can reach: up, not loopback; the VPN ones (100.64.0.0/10, Tailscale's) and
    /// the local network's first.
    /// </summary>
    public static List<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address)
                        && !u.Address.ToString().StartsWith("169.254.")) list.Add(u.Address);
            }
        }
        catch (NetworkInformationException) { }
        return [.. list.Distinct().OrderBy(Rank).Take(MaxAddresses)];
    }

    static int Rank(IPAddress a)
    {
        var b = a.GetAddressBytes();
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return 0;
        if (b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16) return 1;
        return 2;
    }

    /// <summary>The code, in groups of five: "XT-ABCDE-…".</summary>
    public string Code
    {
        get
        {
            var bytes = new List<byte> { Format, (byte)(Port >> 8), (byte)Port, (byte)Addresses.Count };
            foreach (var a in Addresses) bytes.AddRange(a.MapToIPv4().GetAddressBytes());
            bytes.AddRange(Key);
            string text = Encode([.. bytes]);
            var sb = new StringBuilder("XT");
            for (int i = 0; i < text.Length; i += 5) sb.Append('-').Append(text, i, Math.Min(5, text.Length - i));
            return sb.ToString();
        }
    }

    public string Link => LinkPrefix + Code;

    /// <summary>Reads a code or a link; null when it isn't one (or came cut).</summary>
    public static LiveInvite? Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase)) text = text[LinkPrefix.Length..];
        text = text.Trim('/');
        if (text.StartsWith("XT-", StringComparison.OrdinalIgnoreCase)) text = text[3..];
        var clean = new string([.. text.ToUpperInvariant().Where(c => c != '-' && !char.IsWhiteSpace(c))]);
        if (Decode(clean) is not { Length: >= 4 } b || b[0] != Format) return null;
        int port = b[1] << 8 | b[2], n = b[3];
        if (n > MaxAddresses || b.Length != 4 + n * 4 + KeyBytes || port == 0) return null;
        var addresses = new List<IPAddress>();
        for (int i = 0; i < n; i++) addresses.Add(new IPAddress(b.AsSpan(4 + i * 4, 4)));
        return new(addresses, port, b[(4 + n * 4)..]);
    }

    static string Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte x in data)
        {
            buffer = buffer << 8 | x;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    static byte[]? Decode(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (char c in text)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0) return null;
            buffer = buffer << 5 | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
