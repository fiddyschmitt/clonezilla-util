// lib7zNative - a thin C ABI over 7-Zip's own UI/Common open/extract orchestration
// (OpenArchive.cpp / CArchiveLink + LoadCodecs.cpp / CCodecs). It provides what 7zFM gives us -
// automatic format detection and recursive "open inside" - without the GUI, by loading the
// format handlers/codecs from the bundled 7z.dll.
//
// All functions return an int32_t HRESULT (0 == S_OK). Strings are UTF-16, null-terminated.
// The host owns the input stream (provided via callbacks) and the output sink (write callback).
#ifndef LIB7ZNATIVE_H
#define LIB7ZNATIVE_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef LIB7ZNATIVE_EXPORTS
#    define LIB7Z_API __declspec(dllexport)
#  else
#    define LIB7Z_API __declspec(dllimport)
#  endif
#else
#  define LIB7Z_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void* SevenZipArchiveHandle;

// --- Input stream (host-provided), bridged to 7-Zip IInStream ---
// read: copy up to 'size' bytes into 'buf'; set *processed to bytes read (0 == EOF). return 0 on success.
// seek: origin 0=begin, 1=current, 2=end; set *newPosition to the resulting absolute position. return 0 on success.
typedef int32_t (*SevenZipReadFn)(void* ctx, void* buf, uint32_t size, uint32_t* processed);
typedef int32_t (*SevenZipSeekFn)(void* ctx, int64_t offset, uint32_t origin, uint64_t* newPosition);

typedef struct
{
    SevenZipReadFn read;
    SevenZipSeekFn seek;
} SevenZipInStreamCallbacks;

typedef struct
{
    uint8_t  isDir;
    uint8_t  hasOffset;          // 1 if 'offset' is set (kpidOffset present, e.g. a partition in a drive image)
    uint64_t size;
    int64_t  offset;             // byte offset of the item within the stream (e.g. partition start); valid only if hasOffset
    int64_t  modifiedFileTime;   // Windows FILETIME (100ns ticks since 1601-01-01); 0 if absent
    int64_t  createdFileTime;
    int64_t  accessedFileTime;
} SevenZipItemInfo;

// Open the host stream with automatic format detection.
// sevenZipDllPath: full path to the bundled 7z.dll that supplies the handlers/codecs.
// recursive: 1 = open nested archives/filesystems "inside" (browse a partition's files); 0 = open only
//   the outer container (e.g. list a drive image's partition table without descending into filesystems).
LIB7Z_API int32_t SevenZip_Open(
    const SevenZipInStreamCallbacks* in, void* inCtx,
    const wchar_t* sevenZipDllPath, uint8_t recursive,
    SevenZipArchiveHandle* outHandle);

LIB7Z_API int32_t SevenZip_GetItemCount(SevenZipArchiveHandle handle, uint32_t* outCount);

// Fills *outInfo and writes the item's UTF-16 path into pathBuf (capacity pathBufChars, incl. null).
// *outPathChars receives the length excluding the null terminator; if it is >= pathBufChars the path
// was truncated - call again with a buffer of at least (*outPathChars + 1) chars.
LIB7Z_API int32_t SevenZip_GetItem(
    SevenZipArchiveHandle handle, uint32_t index,
    SevenZipItemInfo* outInfo,
    wchar_t* pathBuf, uint32_t pathBufChars, uint32_t* outPathChars);

// --- On-demand seekable per-item stream (no extraction, no temp file) ---
// Opens the item's data as a seekable read-only stream via IInArchiveGetStream. Reads pull data
// directly from the archive's input stream on demand (e.g. cluster-mapped NTFS data over the
// partition). Only ONE item stream may be open per archive handle at a time (it drives the same
// underlying input stream); the host serialises this by checking out one archive handle per stream.
typedef void* SevenZipItemStreamHandle;

LIB7Z_API int32_t SevenZip_OpenItemStream(
    SevenZipArchiveHandle handle, uint32_t index,
    SevenZipItemStreamHandle* outStream);

// Reads up to 'size' bytes at the current position; sets *processed (0 == EOF). Partial reads allowed.
LIB7Z_API int32_t SevenZip_ItemRead(
    SevenZipItemStreamHandle stream, void* buf, uint32_t size, uint32_t* processed);

// origin: 0=begin, 1=current, 2=end. Sets *newPosition to the resulting absolute position.
LIB7Z_API int32_t SevenZip_ItemSeek(
    SevenZipItemStreamHandle stream, int64_t offset, uint32_t origin, uint64_t* newPosition);

LIB7Z_API void SevenZip_ItemClose(SevenZipItemStreamHandle stream);

LIB7Z_API void SevenZip_Close(SevenZipArchiveHandle handle);

#ifdef __cplusplus
}
#endif

#endif // LIB7ZNATIVE_H
