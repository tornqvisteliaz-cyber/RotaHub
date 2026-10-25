using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RotaHub.Core;

/// <summary>
/// NTFS directory junctions: how a package in the library appears inside the sim's Community folder.
/// Unlike symbolic links they need no administrator rights or developer mode, and they work across
/// local drives. MSFS follows them like ordinary folders.
/// </summary>
public static class Junction
{
    private const uint MountPointTag = 0xA0000003;
    private const uint SymlinkTag = 0xA000000C;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint GetReparsePoint = 0x000900A8;
    private const string NonParsedPrefix = @"\??\";

    /// <summary>Creates <paramref name="link"/> (which must not exist) pointing at the folder <paramref name="target"/>.</summary>
    public static void Create(string link, string target)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException("The package folder to link is missing: " + target);
        if (Directory.Exists(link) || File.Exists(link))
            throw new IOException("Something is already at " + link);

        Directory.CreateDirectory(link);
        try
        {
            var substitute = Encoding.Unicode.GetBytes(NonParsedPrefix + target.TrimEnd('\\'));
            var print = Encoding.Unicode.GetBytes(target.TrimEnd('\\'));
            // REPARSE_DATA_BUFFER, MountPointReparseBuffer: four USHORT offsets/lengths, then both names, each NUL-terminated.
            var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
            var buffer = new byte[8 + dataLength];
            using (var w = new BinaryWriter(new MemoryStream(buffer)))
            {
                w.Write(MountPointTag);
                w.Write((ushort)dataLength);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)substitute.Length);
                w.Write((ushort)(substitute.Length + 2));
                w.Write((ushort)print.Length);
                w.Write(substitute);
                w.Write((ushort)0);
                w.Write(print);
                w.Write((ushort)0);
            }

            using var handle = Open(link, write: true);
            if (!DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not link " + link);
        }
        catch
        {
            try { Directory.Delete(link); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>True for a junction or directory symbolic link (also when its target is gone).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where a junction or directory symbolic link points, or null.</summary>
    public static string? Target(string link)
    {
        if (!IsLink(link))
            return null;
        using var handle = Open(link, write: false);
        var buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, GetReparsePoint, null, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;
        var tag = BitConverter.ToUInt32(buffer, 0);
        int pathBuffer;
        if (tag == MountPointTag)
            pathBuffer = 16;
        else if (tag == SymlinkTag)
            pathBuffer = 20; // a symlink buffer has a ULONG Flags before its names
        else
            return null;
        var offset = BitConverter.ToUInt16(buffer, 8);
        var length = BitConverter.ToUInt16(buffer, 10);
        var name = Encoding.Unicode.GetString(buffer, pathBuffer + offset, length);
        if (name.StartsWith(NonParsedPrefix, StringComparison.Ordinal))
            name = name.Substring(NonParsedPrefix.Length);
        else if (tag == SymlinkTag && !Path.IsPathRooted(name))
            name = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, name));
        return name;
    }

    /// <summary>Removes the link only; the folder it points at is untouched.</summary>
    public static void Delete(string link)
    {
        if (!IsLink(link))
            throw new IOException(link + " is not a link; refusing to delete a real folder.");
        if (!RemoveDirectory(link))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the link " + link);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static SafeFileHandle Open(string path, bool write)
    {
        var handle = CreateFile(path, write ? 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */ : 0x80000000 /* GENERIC_READ */,
            7 /* share read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 | 0x00200000 /* FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open " + path);
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectory(string path);
}
