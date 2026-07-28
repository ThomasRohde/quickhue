using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickHue;

internal static class DpapiProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickHue.v1.application-key");

    public static string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        var input = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectOrUnprotect(input, protect: true);
        Array.Clear(input);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        var input = Convert.FromBase64String(protectedValue);
        var plaintext = ProtectOrUnprotect(input, protect: false);
        Array.Clear(input);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static byte[] ProtectOrUnprotect(byte[] input, bool protect)
    {
        var inputBlob = DataBlob.FromBytes(input);
        var entropyBlob = DataBlob.FromBytes(Entropy);
        DataBlob outputBlob = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);

            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            outputBlob.Dispose();
            entropyBlob.Dispose();
            inputBlob.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob : IDisposable
    {
        public int Size;
        public IntPtr Data;

        public static DataBlob FromBytes(byte[] bytes)
        {
            var blob = new DataBlob
            {
                Size = bytes.Length,
                Data = Marshal.AllocHGlobal(bytes.Length)
            };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public void Dispose()
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            unsafe
            {
                new Span<byte>((void*)Data, Size).Clear();
            }
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
            Size = 0;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);
}
