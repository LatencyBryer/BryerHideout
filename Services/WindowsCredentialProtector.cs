using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BryersHideoutPlugin.Services;

/// <summary>
/// Protects small pieces of local plugin data with Windows DPAPI. The encrypted
/// value can only be decrypted by the same Windows user account that saved it.
/// </summary>
internal static class WindowsCredentialProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var input = CreateInputBlob(plainBytes);
        DataBlob output = default;
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "Bryer's Hideout remembered staff password",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var encryptedBytes = new byte[output.Size];
            Marshal.Copy(output.Data, encryptedBytes, 0, output.Size);
            return Convert.ToBase64String(encryptedBytes);
        }
        finally
        {
            Array.Clear(plainBytes, 0, plainBytes.Length);
            FreeInputBlob(input);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    public static string Unprotect(string encodedValue)
    {
        if (string.IsNullOrWhiteSpace(encodedValue)) return string.Empty;

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(encodedValue);
        }
        catch
        {
            return string.Empty;
        }

        var input = CreateInputBlob(encryptedBytes);
        DataBlob output = default;
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output))
            {
                return string.Empty;
            }

            var plainBytes = new byte[output.Size];
            try
            {
                Marshal.Copy(output.Data, plainBytes, 0, output.Size);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Array.Clear(encryptedBytes, 0, encryptedBytes.Length);
            FreeInputBlob(input);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static DataBlob CreateInputBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static void FreeInputBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        for (var i = 0; i < blob.Size; i++) Marshal.WriteByte(blob.Data, i, 0);
        Marshal.FreeHGlobal(blob.Data);
    }
}
