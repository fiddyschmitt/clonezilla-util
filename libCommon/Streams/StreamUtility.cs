using libCommon.Streams.Sparse;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public static class StreamUtility
    {
        public static void ExtractToFile(string streamName, Stream? compressedOrigin, Stream decompressedStream, FileStream fileStream, bool makeSparse)
        {
            if (libCommon.Utility.IsOnNTFS(fileStream.Name) && makeSparse && decompressedStream is ISparseAwareReader sparseAwareInput)
            {
                //a hack to speed things up. Let's make the output file sparse, so that we don't have to write zeroes for all the unpopulated ranges

                //tell the input stream to not bother with the remainder of the file if it's all null
                sparseAwareInput.StopReadingWhenRemainderOfFileIsNull = true;

                //tell the output stream to create a sparse file
                if (OperatingSystem.IsWindows())
                {
                    fileStream.SafeFileHandle.MarkAsSparse();
                }

                //This doesn't seem to be required
                //fileStream.SetLength(uncompressedLength);

                //tell the writer not to bother writing the null bytes to the file (because it's already sparse)
                var outputStream = new SparseAwareWriteStream(fileStream, false);

                sparseAwareInput
                    .Sparsify(outputStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                    progress =>
                    {
                        var totalCopiedStr = Extensions.BytesToString(progress.TotalRead);

                        if (decompressedStream.Length == 0)
                        {
                            if (compressedOrigin == null)
                            {
                                Log.Information($"Extracted {totalCopiedStr}");
                            }
                            else
                            {
                                var perThroughCompressedSource = (double)compressedOrigin.Position / compressedOrigin.Length * 100;

                                Log.Information($"Extracted {totalCopiedStr}    ({perThroughCompressedSource:N0}% through source file)");
                            }
                        }
                        else
                        {
                            var per = (double)progress.TotalRead / decompressedStream.Length * 100;
                            var totalStr = Extensions.BytesToString(decompressedStream.Length);

                            if (compressedOrigin == null)
                            {
                                Log.Information($"Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                            }
                            else
                            {
                                var perThroughCompressedSource = (double)compressedOrigin.Position / compressedOrigin.Length * 100;

                                Log.Information($"Extracted {totalCopiedStr} / {totalStr}    ({perThroughCompressedSource:N0}% through source file)");
                            }
                        }
                    });
            }
            else
            {
                //just a regular file, with null bytes and all
                decompressedStream
                    .CopyTo(fileStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                    progress =>
                    {
                        var per = (double)progress.TotalRead / decompressedStream.Length * 100;

                        var totalCopiedStr = Extensions.BytesToString(progress.TotalRead);
                        var totalStr = Extensions.BytesToString(decompressedStream.Length);
                        Log.Information($"{streamName} Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                    });
            }
        }
    }
}
