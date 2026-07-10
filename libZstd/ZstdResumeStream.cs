using System;
using System.IO;
using System.Runtime.InteropServices;
using ZstdSharp.Unsafe;

namespace libZstd
{
    /// <summary>
    /// Forward-only decode of one zstd index span: starts at a resume point (either a real frame
    /// header, or a mid-frame block boundary reached via a synthetic frame header +
    /// ZSTD_DCtx_refPrefix of the point's window) and reads compressed bytes from
    /// <paramref name="compressedStart"/> up to <paramref name="compressedEnd"/>. The window array is
    /// pinned for the stream's lifetime (refPrefix references it directly).
    /// </summary>
    public sealed unsafe class ZstdResumeStream(Stream source, long compressedStart, long compressedEnd, bool isFrameStart, byte windowDescriptor, byte[] window) : Stream
    {
        ZSTD_DCtx_s* dctx;
        GCHandle windowPin;
        readonly byte[] inputBuffer = new byte[512 * 1024];
        int inputAvailable;
        int inputPos;
        long compressedRemaining = compressedEnd - compressedStart;
        bool initialised;
        bool headerFed;
        bool frameEnded;

        void Initialise()
        {
            dctx = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(dctx, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);

            if (!isFrameStart && window.Length > 0)
            {
                windowPin = GCHandle.Alloc(window, GCHandleType.Pinned);
                var r = Methods.ZSTD_DCtx_refPrefix(dctx, (byte*)windowPin.AddrOfPinnedObject(), (nuint)window.Length);
                if (Methods.ZSTD_isError(r)) throw new InvalidDataException($"refPrefix failed: {Methods.ZSTD_getErrorName(r)}");
            }

            source.Position = compressedStart;
            initialised = true;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || frameEnded) return 0;
            if (!initialised) Initialise();

            fixed (byte* outputPtr = buffer)
            {
                var output = new ZSTD_outBuffer_s { dst = outputPtr + offset, size = (nuint)count, pos = 0 };

                while (output.pos == 0 && !frameEnded)
                {
                    if (!headerFed && !isFrameStart)
                    {
                        //mid-frame resume: hand the decoder a minimal synthetic frame header first
                        var synthetic = ZstdSeekableIndex.SyntheticFrameHeader(windowDescriptor);
                        fixed (byte* synthPtr = synthetic)
                        {
                            var headerInput = new ZSTD_inBuffer_s { src = synthPtr, size = (nuint)synthetic.Length, pos = 0 };
                            while (headerInput.pos < headerInput.size)
                            {
                                var r = Methods.ZSTD_decompressStream(dctx, &output, &headerInput);
                                if (Methods.ZSTD_isError(r)) throw new InvalidDataException($"zstd resume header rejected: {Methods.ZSTD_getErrorName(r)}");
                            }
                        }
                        headerFed = true;
                        continue;
                    }
                    headerFed = true;

                    var inputExhausted = false;
                    if (inputPos == inputAvailable)
                    {
                        var toRead = (int)Math.Min(inputBuffer.Length, compressedRemaining);
                        if (toRead > 0)
                        {
                            inputAvailable = source.Read(inputBuffer, 0, toRead);
                            inputPos = 0;
                            if (inputAvailable == 0) inputExhausted = true;
                            else compressedRemaining -= inputAvailable;
                        }
                        else
                        {
                            //no compressed bytes left in the span - but the decoder may still hold
                            //decoded-but-unflushed output, so attempt a flush-only call before EOF
                            inputAvailable = 0;
                            inputPos = 0;
                            inputExhausted = true;
                        }
                    }

                    fixed (byte* inputPtr = inputBuffer)
                    {
                        var input = new ZSTD_inBuffer_s { src = inputPtr + inputPos, size = (nuint)(inputAvailable - inputPos), pos = 0 };
                        var r = Methods.ZSTD_decompressStream(dctx, &output, &input);
                        if (Methods.ZSTD_isError(r)) throw new InvalidDataException($"zstd resume decode failed: {Methods.ZSTD_getErrorName(r)}");
                        inputPos += (int)input.pos;
                        if (r == 0) frameEnded = true;      //the (possibly synthetic) frame is complete
                        if (output.pos == 0 && inputExhausted) break;   //flushed nothing and no more input: done
                    }
                }

                return (int)output.pos;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (dctx != null)
            {
                Methods.ZSTD_freeDCtx(dctx);
                dctx = null;
            }
            if (windowPin.IsAllocated) windowPin.Free();
            base.Dispose(disposing);
        }
    }
}
