using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace NetMonitor;

public record ConnectionInfo(
    string Process,
    int Pid,
    string Local,
    string Remote,
    string State);

/// <summary>
/// Windows IP Helper API (iphlpapi.dll) ile aktif TCP bağlantılarını ve
/// her bağlantının hangi sürece (PID) ait olduğunu okur.
/// </summary>
public static class ConnectionReader
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int RowSize = 24; // 6 adet uint

    private static readonly string[] States =
    {
        "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED",
        "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK",
        "TIME_WAIT", "DELETE_TCB"
    };

    private static readonly Dictionary<int, string> NameCache = new();

    public static List<ConnectionInfo> ReadTcp()
    {
        var result = new List<ConnectionInfo>();

        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return result;

            int count = Marshal.ReadInt32(buffer);
            IntPtr row = buffer + 4;

            for (int i = 0; i < count; i++)
            {
                int state = Marshal.ReadInt32(row, 0);
                uint localAddr = (uint)Marshal.ReadInt32(row, 4);
                int localPort = Marshal.ReadInt32(row, 8);
                uint remoteAddr = (uint)Marshal.ReadInt32(row, 12);
                int remotePort = Marshal.ReadInt32(row, 16);
                int pid = Marshal.ReadInt32(row, 20);

                string local = $"{new IPAddress(localAddr)}:{Port(localPort)}";
                string remote = state == 2 // LISTEN
                    ? "-"
                    : $"{new IPAddress(remoteAddr)}:{Port(remotePort)}";

                result.Add(new ConnectionInfo(
                    GetProcessName(pid), pid, local, remote,
                    state >= 0 && state < States.Length ? States[state] : state.ToString()));

                row += RowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    // Port numarası ağ bayt sırasında (big-endian) gelir, çeviriyoruz.
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static string GetProcessName(int pid)
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
