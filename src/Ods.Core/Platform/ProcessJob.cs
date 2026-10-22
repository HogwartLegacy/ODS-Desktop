using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ods.Core.Platform;

/// <summary>
/// A kill-on-close job object. Every child ODS starts (llama-server today,
/// other native services later) is assigned to it, so Windows terminates them
/// when ODS exits for any reason, including a crash or Task Manager kill.
/// This is the Windows replacement for Docker's container lifetime.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private readonly SafeJobHandle _handle;

    public ProcessJob()
    {
        _handle = NativeMethods.CreateJobObject(0, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the ODS process job.");

        var info = new NativeMethods.JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
            },
        };
        if (!NativeMethods.SetInformationJobObject(
                _handle,
                NativeMethods.JobObjectExtendedLimitInformation,
                ref info,
                (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformationData>()))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "Could not configure the ODS process job.");
        }
    }

    public void Assign(Process process)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not attach process {process.Id} to the ODS process job.");
        }
    }

    public void Dispose() => _handle.Dispose();
}
