using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TogetherServer.CompanionChecks;

// Read the Windows TCP owner table. Port presence alone cannot show which EXE
// would appear in an access-control prompt.
internal static class WindowsListenerOwners
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint InsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int ipVersion, int tableClass, uint reserved);

    internal static void RequireTogetherServerOwner(int port, Process process)
    {
        var executable = process.MainModule?.FileName;
        if (executable is null ||
            !Path.GetFileName(executable).Equals("TogetherServer.exe", StringComparison.OrdinalIgnoreCase) ||
            FileVersionInfo.GetVersionInfo(executable).ProductName != "TogetherServer")
            throw new Exception("The packaged listener process is not TogetherServer.exe.");
        var owners = ListenerPids(port);
        if (owners.Count == 0 || owners.Any(pid => pid != process.Id))
            throw new Exception($"TCP {port} is not owned only by packaged TogetherServer.exe PID {process.Id}. " +
                $"Observed owners: {string.Join(", ", owners)}.");
    }

    private static IReadOnlyList<int> ListenerPids(int port)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var bytes = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref bytes, true, AfInet,
            TcpTableOwnerPidListener, 0);
        if (result != InsufficientBuffer || bytes < sizeof(int))
            throw new SocketException(unchecked((int)result));
        var table = Marshal.AllocHGlobal(bytes);
        try
        {
            result = GetExtendedTcpTable(table, ref bytes, true, AfInet,
                TcpTableOwnerPidListener, 0);
            if (result != 0) throw new SocketException(unchecked((int)result));
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            if (count < 0 || (long)count * rowSize + sizeof(int) > bytes)
                throw new InvalidDataException("Windows returned an invalid TCP owner table.");
            var owners = new List<int>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(
                    IntPtr.Add(table, sizeof(int) + index * rowSize));
                if (BinaryPrimitives.ReverseEndianness((ushort)row.LocalPort) == port)
                    owners.Add(checked((int)row.OwningPid));
            }
            return owners;
        }
        finally { Marshal.FreeHGlobal(table); }
    }
}
