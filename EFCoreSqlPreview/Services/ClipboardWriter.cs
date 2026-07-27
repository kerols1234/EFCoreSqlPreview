using System.Runtime.InteropServices;

namespace EFCoreSqlPreview.Services
{
    /// <summary>
    /// Puts text on the Windows clipboard.
    /// </summary>
    /// <remarks>
    /// The extension targets <c>net8.0-windows8.0</c> without WPF or WinForms, so neither
    /// <c>System.Windows.Clipboard</c> nor <c>System.Windows.Forms.Clipboard</c> is available, and Remote UI
    /// XAML cannot host a converter or code-behind that would do it on the Visual Studio side. The Win32
    /// clipboard API is the only route left, and it works from any thread in the interactive session.
    /// </remarks>
    internal static class ClipboardWriter
    {
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        /// <summary>
        /// Attempts to place <paramref name="text"/> on the clipboard as Unicode text.
        /// </summary>
        /// <param name="text">The text to copy. Empty text clears nothing and reports success.</param>
        /// <returns><see langword="true"/> when the clipboard now holds the text.</returns>
        public static bool TrySetText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            var handle = IntPtr.Zero;
            var opened = false;

            try
            {
                // The clipboard is a shared, contended resource; a single attempt can lose to another process.
                for (var attempt = 0; attempt < 5 && !opened; attempt++)
                {
                    opened = OpenClipboard(IntPtr.Zero);
                    if (!opened)
                    {
                        Thread.Sleep(20);
                    }
                }

                if (!opened)
                {
                    return false;
                }

                if (!EmptyClipboard())
                {
                    return false;
                }

                // The block must include the terminating null and ownership passes to the clipboard on success,
                // so it must not be freed afterwards.
                var byteCount = (text!.Length + 1) * sizeof(char);
                handle = GlobalAlloc(GmemMoveable, (UIntPtr)byteCount);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                var target = GlobalLock(handle);
                if (target == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
                {
                    return false;
                }

                handle = IntPtr.Zero;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    GlobalFree(handle);
                }

                if (opened)
                {
                    CloseClipboard();
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint format, IntPtr data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr memory);
    }
}
