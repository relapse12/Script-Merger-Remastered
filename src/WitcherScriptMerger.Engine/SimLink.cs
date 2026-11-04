using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WitcherScriptMerger;

public static class SimLink
{
	private const int CREATION_DISPOSITION_OPEN_EXISTING = 3;

	private const int FILE_FLAG_BACKUP_SEMANTICS = 33554432;

	[DllImport( "kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true )]
	public static extern int GetFinalPathNameByHandle( nint handle, [In][Out] StringBuilder path, int bufLen, int flags );

	[DllImport( "kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true )]
	public static extern SafeFileHandle CreateFile(
		string lpFileName,
		int dwDesiredAccess,
		int dwShareMode,
		nint SecurityAttributes,
		int dwCreationDisposition,
		int dwFlagsAndAttributes,
		nint hTemplateFile );

	public static string GetSymbolicLinkTarget( FileInfo symlink )
	{
		SafeFileHandle safeFileHandle = CreateFile( symlink.FullName, 0, 2, IntPtr.Zero, 3, 33554432, IntPtr.Zero );

		if ( safeFileHandle.IsInvalid )
		{
			throw new Win32Exception( Marshal.GetLastWin32Error() );
		}

		StringBuilder path = new StringBuilder( 512 );

		if ( GetFinalPathNameByHandle( safeFileHandle.DangerousGetHandle(), path, path.Capacity, 0 ) < 0 )
		{
			throw new Win32Exception( Marshal.GetLastWin32Error() );
		}

		if ( path[0] != '\\' || path[1] != '\\' || path[2] != '?' || path[3] != '\\' )
		{
			return path.ToString();
		}

		string text = path.ToString();

		return text.Substring( 4, text.Length - 4 );
	}
}
