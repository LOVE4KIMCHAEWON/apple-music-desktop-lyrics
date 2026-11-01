using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AppleMusicLyrics.Probe;

/// <summary>
/// Resolves a bare executable GSMTC source using a running process's package identity.
/// An executable name alone is never enough to identify Apple Music.
/// </summary>
internal static class NativeAppIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumImagePathLength = 32768;
    private const uint MaximumPackageFamilyLength = 1024;

    public static bool TryResolve(string sourceId, IReadOnlySet<string> appleFamilies, out string reason)
    {
        reason = "unmatched; no matching running process package identity";
        if (!OperatingSystem.IsWindows() || appleFamilies.Count == 0 || !IsBareExecutableName(sourceId)) return false;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(sourceId));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            reason = $"unmatched; process enumeration unavailable ({ex.GetType().Name})";
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
                    if (handle.IsInvalid) continue;

                    // Query only the image identity. Do not inspect process modules, files,
                    // command lines, environment, credentials, or audio.
                    var image = new StringBuilder(MaximumImagePathLength);
                    uint imageLength = (uint)image.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, image, ref imageLength)) continue;
                    var leaf = Path.GetFileName(image.ToString());
                    if (!string.Equals(leaf, sourceId, StringComparison.OrdinalIgnoreCase)) continue;

                    uint familyLength = 0;
                    if (GetPackageFamilyName(handle, ref familyLength, null) != ErrorInsufficientBuffer ||
                        familyLength < 2 || familyLength > MaximumPackageFamilyLength) continue;
                    var familyBuffer = new StringBuilder((int)familyLength);
                    if (GetPackageFamilyName(handle, ref familyLength, familyBuffer) != ErrorSuccess) continue;
                    var family = familyBuffer.ToString();
                    if (!appleFamilies.Any(known => string.Equals(known, family, StringComparison.OrdinalIgnoreCase))) continue;

                    // Deliberately omit the full executable path from logs. This is
                    // corroborating identity evidence, not a GSMTC-provided process ID.
                    reason = $"running-process-package-family; processId={process.Id}; executable={leaf}; packageFamilyName={family}";
                    return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
                {
                    // Processes can exit or become inaccessible during enumeration.
                    // Keep inspecting other exact-name candidates without elevation.
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static bool IsBareExecutableName(string sourceId) =>
        !string.IsNullOrWhiteSpace(sourceId) &&
        sourceId.Length <= 260 &&
        sourceId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        sourceId.Length > 4 &&
        !sourceId.Any(char.IsControl) &&
        sourceId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        sourceId.IndexOfAny(['\\', '/', ':', '!']) < 0 &&
        string.Equals(Path.GetFileName(sourceId), sourceId, StringComparison.Ordinal);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern NativeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(NativeProcessHandle process, uint flags, StringBuilder imageName, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(NativeProcessHandle process, ref uint length, StringBuilder? familyName);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class NativeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeProcessHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
