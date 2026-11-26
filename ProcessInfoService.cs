using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NetMonitor;

public enum SignatureState
{
    Pending,      // arka planda kontrol ediliyor
    Signed,       // geçerli dijital imza
    Unsigned,     // imza yok
    Invalid,      // imza var ama bozuk / dosya değiştirilmiş
    WindowsFile,  // Windows klasöründe, gömülü imzası yok (katalog imzalı sistem dosyası)
    Unknown       // doğrulanamadı
}

/// <summary>
/// Bir sürecin çalıştırılabilir dosya yolunu ve dijital imza durumunu bulur.
/// İmza kontrolü yavaş olabileceği için arka planda yapılır ve önbelleğe alınır.
/// </summary>
public sealed class ProcessInfoService
{
    private readonly ConcurrentDictionary<int, (string? Path, DateTime At)> _paths = new();
    private readonly ConcurrentDictionary<string, SignatureState> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _limiter = new(2);

    public static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public static bool IsUnderWindowsDir(string path) =>
        path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ yol

    public string? GetPath(int pid)
    {
        if (pid <= 4) return null;

        if (_paths.TryGetValue(pid, out var entry) && (DateTime.UtcNow - entry.At).TotalSeconds < 60)
            return entry.Path;

        string? path = QueryPath(pid);
        _paths[pid] = (path, DateTime.UtcNow);
        return path;
    }

    private static string? QueryPath(int pid)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- imza

    public SignatureState GetSignature(string path)
    {
        if (_signatures.TryGetValue(path, out var state)) return state;

        if (_pending.TryAdd(path, 0))
            _ = Task.Run(() => Verify(path));

        return SignatureState.Pending;
    }

    private void Verify(string path)
    {
        _limiter.Wait();
        try
        {
            var state = CheckFile(path);
            if (state == SignatureState.Unsigned && IsUnderWindowsDir(path))
                state = SignatureState.WindowsFile;
            _signatures[path] = state;
        }
        catch
        {
            _signatures[path] = SignatureState.Unknown;
        }
        finally
        {
            _limiter.Release();
            _pending.TryRemove(path, out _);
        }
    }

    private static SignatureState CheckFile(string path)
    {
        const uint TRUST_E_NOSIGNATURE = 0x800B0100;
        const uint TRUST_E_BAD_DIGEST = 0x80096010;

        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        IntPtr dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        IntPtr noWindow = new IntPtr(-1); // arayüz gösterme
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2

        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pathPtr
            };
            Marshal.StructureToPtr(fileInfo, filePtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x10         // WTD_REVOCATION_CHECK_NONE (ağa bağlanma)
            };
            Marshal.StructureToPtr(data, dataPtr, false);

            uint result = WinVerifyTrust(noWindow, ref action, dataPtr);

            // Durum verisini serbest bırak
            data = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
            data.dwStateAction = 2;        // WTD_STATEACTION_CLOSE
            Marshal.StructureToPtr(data, dataPtr, false);
            WinVerifyTrust(noWindow, ref action, dataPtr);

            return result switch
            {
                0 => SignatureState.Signed,
                TRUST_E_NOSIGNATURE => SignatureState.Unsigned,
                TRUST_E_BAD_DIGEST => SignatureState.Invalid,
                _ => SignatureState.Unknown
            };
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    // ------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
}
