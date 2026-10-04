using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ShellTrack.Windows;

public static class ConsoleInput
{
    public static Stream OpenUtf8()
    {
        IntPtr input = Native.GetStdHandle(-10);
        return Native.GetConsoleMode(input, out _) ? new Utf8Input(input) : Console.OpenStandardInput();
    }
    // Older conhost versions may replace non-ASCII ReadFile input even in CP_UTF8.
    // Read Unicode console characters (including VT key sequences), then encode
    // incrementally for ConPTY. Redirected input remains an untouched byte stream.
    private sealed class Utf8Input(IntPtr input) : Stream
    {
        private readonly char[] characters = new char[128];
        private readonly byte[] bytes = new byte[Encoding.UTF8.GetMaxByteCount(128)];
        private readonly Encoder encoder = Encoding.UTF8.GetEncoder();
        private int offset, available;
        private bool disposed, ended;
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (buffer.IsEmpty) return 0;
            while (offset == available)
            {
                if (ended) return 0;
                if (!Native.ReadConsoleW(input, characters, (uint)characters.Length, out uint read, IntPtr.Zero))
                    throw new IOException("Cannot read terminal input.", new Win32Exception(Marshal.GetLastWin32Error()));
                ended = read == 0;
                available = encoder.GetBytes(characters, 0, (int)read, bytes, 0, ended);
                offset = 0;
            }
            int take = Math.Min(buffer.Length, available - offset);
            bytes.AsSpan(offset, take).CopyTo(buffer); offset += take;
            return take;
        }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
