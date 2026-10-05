using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LocalBack.Core.Crypto;

/// <summary>
/// Streams of any size encrypted with AES-256-GCM in 1 MiB chunks, so a multi-gigabyte file never has to be in memory.
///
/// Container: <c>"LBE1"</c>, 8 random bytes (nonce prefix), then chunks of
/// <c>[last:1][length:4 LE][ciphertext:length][tag:16]</c>. The nonce of chunk <i>i</i> is prefix + <i>i</i> (big-endian),
/// and <i>i</i> and the last flag are authenticated as associated data, so chunks cannot be reordered, dropped
/// or truncated without detection.
/// </summary>
public static class ChunkedAesGcm
{
    public const int KeySize = 32;
    public const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private const int PrefixSize = 8;
    private static readonly byte[] Magic = "LBE1"u8.ToArray();

    public static bool LooksEncrypted(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual(Magic);

    /// <summary>A write-only stream: plaintext in, container out to <paramref name="destination"/>. Dispose to write the last chunk.</summary>
    public static Stream CreateEncryptor(Stream destination, byte[] key) => new Writer(destination, key);

    /// <summary>A read-only stream: container from <paramref name="source"/>, plaintext out. Throws <see cref="CryptographicException"/> on tampering or truncation.</summary>
    public static Stream CreateDecryptor(Stream source, byte[] key) => new Reader(source, key);

    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        using var ms = new MemoryStream();
        using (var w = CreateEncryptor(ms, key)) w.Write(plaintext);
        return ms.ToArray();
    }

    public static byte[] Decrypt(byte[] container, byte[] key)
    {
        using var r = CreateDecryptor(new MemoryStream(container), key);
        using var ms = new MemoryStream();
        r.CopyTo(ms);
        return ms.ToArray();
    }

    private static void FillNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint index)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[PrefixSize..], index);
    }

    private static void FillAad(Span<byte> aad, uint index, bool last)
    {
        BinaryPrimitives.WriteUInt32BigEndian(aad, index);
        aad[4] = last ? (byte)1 : (byte)0;
    }

    private sealed class Writer : Stream
    {
        private readonly Stream _dst;
        private readonly AesGcm _aes;
        private readonly byte[] _prefix = new byte[PrefixSize];
        private readonly byte[] _buffer = new byte[ChunkSize];
        private readonly byte[] _cipher = new byte[ChunkSize];
        private int _filled;
        private uint _index;
        private bool _finished;

        public Writer(Stream dst, byte[] key)
        {
            if (key.Length != KeySize) throw new ArgumentException("Key must be 32 bytes", nameof(key));
            _dst = dst;
            _aes = new AesGcm(key, TagSize);
            RandomNumberGenerator.Fill(_prefix);
            _dst.Write(Magic);
            _dst.Write(_prefix);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            if (_finished) throw new ObjectDisposedException(nameof(Writer));
            while (data.Length > 0)
            {
                int n = Math.Min(ChunkSize - _filled, data.Length);
                data[..n].CopyTo(_buffer.AsSpan(_filled));
                _filled += n;
                data = data[n..];
                // Keep a full chunk until more data or Dispose shows whether it is the last one.
                if (_filled == ChunkSize && data.Length > 0) Emit(last: false);
            }
        }

        private void Emit(bool last)
        {
            Span<byte> nonce = stackalloc byte[12];
            Span<byte> aad = stackalloc byte[5];
            Span<byte> tag = stackalloc byte[TagSize];
            Span<byte> header = stackalloc byte[5];
            FillNonce(nonce, _prefix, _index);
            FillAad(aad, _index, last);
            _aes.Encrypt(nonce, _buffer.AsSpan(0, _filled), _cipher.AsSpan(0, _filled), tag, aad);
            header[0] = last ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(header[1..], _filled);
            _dst.Write(header);
            _dst.Write(_cipher, 0, _filled);
            _dst.Write(tag);
            _filled = 0;
            _index++;
        }

        public override void Flush() => _dst.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_finished)
            {
                _finished = true;
                Emit(last: true); // possibly empty: marks the end
                _dst.Flush();
                _aes.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class Reader : Stream
    {
        private readonly Stream _src;
        private readonly AesGcm _aes;
        private readonly byte[] _prefix = new byte[PrefixSize];
        private readonly byte[] _cipher = new byte[ChunkSize];
        private readonly byte[] _plain = new byte[ChunkSize];
        private int _available;
        private int _offset;
        private uint _index;
        private bool _sawLast;

        public Reader(Stream src, byte[] key)
        {
            if (key.Length != KeySize) throw new ArgumentException("Key must be 32 bytes", nameof(key));
            _src = src;
            _aes = new AesGcm(key, TagSize);
            Span<byte> magic = stackalloc byte[4];
            ReadFully(magic);
            if (!LooksEncrypted(magic)) throw new CryptographicException("Not an encrypted LocalBack file.");
            ReadFully(_prefix);
        }

        private void ReadFully(Span<byte> span)
        {
            int read = 0;
            while (read < span.Length)
            {
                int n = _src.Read(span[read..]);
                if (n <= 0) throw new CryptographicException("Encrypted file is truncated.");
                read += n;
            }
        }

        private bool NextChunk()
        {
            if (_sawLast) return false;
            Span<byte> header = stackalloc byte[5];
            ReadFully(header);
            bool last = header[0] == 1;
            int len = BinaryPrimitives.ReadInt32LittleEndian(header[1..]);
            if (len < 0 || len > ChunkSize) throw new CryptographicException("Encrypted file is damaged.");
            ReadFully(_cipher.AsSpan(0, len));
            Span<byte> tag = stackalloc byte[TagSize];
            ReadFully(tag);
            Span<byte> nonce = stackalloc byte[12];
            Span<byte> aad = stackalloc byte[5];
            FillNonce(nonce, _prefix, _index);
            FillAad(aad, _index, last);
            try
            {
                _aes.Decrypt(nonce, _cipher.AsSpan(0, len), tag, _plain.AsSpan(0, len), aad);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new CryptographicException("Encrypted file is damaged, or the password is wrong.");
            }
            _index++;
            _available = len;
            _offset = 0;
            _sawLast = last;
            return true;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            while (_offset >= _available)
            {
                if (!NextChunk()) return 0;
            }
            int n = Math.Min(buffer.Length, _available - _offset);
            _plain.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                _src.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
