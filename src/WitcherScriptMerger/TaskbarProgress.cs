using System;
using System.Runtime.InteropServices;

internal static class TaskbarProgress
{
	public enum TaskbarStates
	{
		NoProgress = 0,
		Indeterminate = 1,
		Normal = 2,
		Error = 4,
		Paused = 8
	}

	[ComImport]
	[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ITaskbarList3
	{
		[PreserveSig]
		void HrInit();

		[PreserveSig]
		void AddTab(nint hwnd);

		[PreserveSig]
		void DeleteTab(nint hwnd);

		[PreserveSig]
		void ActivateTab(nint hwnd);

		[PreserveSig]
		void SetActiveAlt(nint hwnd);

		[PreserveSig]
		void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

		[PreserveSig]
		void SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);

		[PreserveSig]
		void SetProgressState(nint hwnd, TaskbarStates state);
	}

	[ComImport]
	[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
	[ClassInterface(ClassInterfaceType.None)]
	private class TaskbarInstance
	{
	}

	private static readonly ITaskbarList3 taskbarInstance = (ITaskbarList3)new TaskbarInstance();

	private static readonly bool taskbarSupported = Environment.OSVersion.Version >= new Version(6, 1);

	public static void SetState(nint windowHandle, TaskbarStates taskbarState)
	{
		if (taskbarSupported)
		{
			taskbarInstance.SetProgressState(windowHandle, taskbarState);
		}
	}

	public static void SetValue(nint windowHandle, double progressValue, double progressMax)
	{
		if (taskbarSupported)
		{
			taskbarInstance.SetProgressValue(windowHandle, (ulong)progressValue, (ulong)progressMax);
		}
	}
}
