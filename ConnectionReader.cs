using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace NetMonitor;

public record ConnectionInfo(
    string Protocol,
    string Process,
    int Pid,
    string Local,
    string Remote,
    string State);

/// <summary>
/// Windows IP Helper API (iphlpapi.dll) ile aktif TCP/UDP (IPv4 + IPv6)
/// bağlantılarını ve her birinin hangi sürece (PID) ait olduğunu okur.
/// </summary>
public static class ConnectionReader
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    private delegate uint TableFetcher(IntPtr buffer, ref int size);

    private static readonly string[] States =
    {
        "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED",
        "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK",
        "TIME_WAIT", "DELETE_TCB"
    };

    private static readonly Dictionary<int, string> NameCache = new();

    public static List<ConnectionInfo> ReadAll()
    {
        var result = new List<ConnectionInfo>();
        ReadTcp4(result);
        ReadTcp6(result);
        ReadUdp4(result);
        ReadUdp6(result);
        return result;
    }

    // ---- TCP IPv4: satır = 24 bayt (durum, yerel adres/port, uzak adres/port, PID) ----
    private static void ReadTcp4(List<ConnectionInfo> list)
    {
        ReadTable(
            (IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0),
            24,
            row =>
            {
                int state = Marshal.ReadInt32(row, 0);
                string local = Endpoint4((uint)Marshal.ReadInt32(row, 4), Marshal.ReadInt32(row, 8));
                string remote = state == 2 ? "-" : Endpoint4((uint)Marshal.ReadInt32(row, 12), Marshal.ReadInt32(row, 16));
                int pid = Marshal.ReadInt32(row, 20);
                list.Add(new ConnectionInfo("TCP", GetProcessName(pid), pid, local, remote, StateName(state)));
            });
    }

    // ---- TCP IPv6: satır = 56 bayt ----
    private static void ReadTcp6(List<ConnectionInfo> list)
    {
        ReadTable(
            (IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, true, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0),
            56,
            row =>
            {
                string local = Endpoint6(row, 0, 16, 20);
                int state = Marshal.ReadInt32(row, 48);
                string remote = state == 2 ? "-" : Endpoint6(row, 24, 40, 44);
                int pid = Marshal.ReadInt32(row, 52);
                list.Add(new ConnectionInfo("TCP6", GetProcessName(pid), pid, local, remote, StateName(state)));
            });
    }

    // ---- UDP IPv4: satır = 12 bayt (yerel adres, yerel port, PID). UDP bağlantısız olduğundan uzak adres yok. ----
    private static void ReadUdp4(List<ConnectionInfo> list)
    {
        ReadTable(
            (IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, true, AF_INET, UDP_TABLE_OWNER_PID, 0),
            12,
            row =>
            {
                string local = Endpoint4((uint)Marshal.ReadInt32(row, 0), Marshal.ReadInt32(row, 4));
                int pid = Marshal.ReadInt32(row, 8);
                list.Add(new ConnectionInfo("UDP", GetProcessName(pid), pid, local, "*", "-"));
            });
    }

    // ---- UDP IPv6: satır = 28 bayt ----
    private static void ReadUdp6(List<ConnectionInfo> list)
    {
        ReadTable(
            (IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, true, AF_INET6, UDP_TABLE_OWNER_PID, 0),
            28,
            row =>
            {
                string local = Endpoint6(row, 0, 16, 20);
                int pid = Marshal.ReadInt32(row, 24);
                list.Add(new ConnectionInfo("UDP6", GetProcessName(pid), pid, local, "*", "-"));
            });
    }

    /// <summary>
    /// Tabloyu iki aşamada okur: önce gereken boyutu sorar, sonra veriyi alır.
    /// Tablo iki çağrı arasında büyürse birkaç kez yeniden dener.
    /// </summary>
    private static void ReadTable(TableFetcher fetch, int rowSize, Action<IntPtr> onRow)
    {
        int size = 0;
        uint rc = fetch(IntPtr.Zero, ref size);

        for (int attempt = 0; attempt < 5 && rc == ERROR_INSUFFICIENT_BUFFER; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                rc = fetch(buffer, ref size);
                if (rc == 0)
                {
                    int count = Marshal.ReadInt32(buffer);
                    IntPtr row = buffer + 4;
                    for (int i = 0; i < count; i++)
                    {
                        onRow(row);
                        row += rowSize;
                    }
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string StateName(int state) =>
        state >= 0 && state < States.Length ? States[state] : state.ToString();

    private static string Endpoint4(uint addr, int rawPort) =>
        $"{new IPAddress(addr)}:{Port(rawPort)}";

    private static string Endpoint6(IntPtr row, int addrOffset, int scopeOffset, int portOffset)
    {
        var bytes = new byte[16];
        Marshal.Copy(row + addrOffset, bytes, 0, 16);
        uint scope = (uint)Marshal.ReadInt32(row, scopeOffset);
        var ip = new IPAddress(bytes, scope);
        return $"[{ip}]:{Port(Marshal.ReadInt32(row, portOffset))}";
    }

    // Port numarası ağ bayt sırasında (big-endian) gelir, çeviriyoruz.
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    public static string GetProcessName(int pid)
    {
        if (pid == 0) return "System Idle";
        if (NameCache.TryGetValue(pid, out var cached)) return cached;

        string name;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch { name = "(bilinmiyor)"; }

        NameCache[pid] = name;
        return name;
    }
}