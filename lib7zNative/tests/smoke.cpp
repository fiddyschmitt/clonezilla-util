// Standalone smoke test for lib7zNative: opens an archive (file-backed callbacks),
// auto-detects its format, lists items, and extracts the first file.
#include <cstdio>
#include <cstdint>
#include <windows.h>
#include "lib7znative.h"

struct FileCtx { FILE* f; };

static int32_t myRead(void* ctx, void* buf, uint32_t size, uint32_t* processed)
{
    FileCtx* c = (FileCtx*)ctx;
    size_t n = fread(buf, 1, size, c->f);
    if (processed) *processed = (uint32_t)n;
    return 0; // S_OK
}

static int32_t mySeek(void* ctx, int64_t offset, uint32_t origin, uint64_t* newPos)
{
    FileCtx* c = (FileCtx*)ctx;
    int o = (origin == 0) ? SEEK_SET : (origin == 1) ? SEEK_CUR : SEEK_END;
    if (_fseeki64(c->f, offset, o) != 0) return (int32_t)0x80004005; // E_FAIL
    long long p = _ftelli64(c->f);
    if (newPos) *newPos = (uint64_t)p;
    return 0;
}

struct WriteCtx { uint64_t total; unsigned long crc; };
static int32_t myWrite(void* ctx, const void* buf, uint32_t size, uint32_t* processed)
{
    WriteCtx* w = (WriteCtx*)ctx;
    w->total += size;
    if (processed) *processed = size;
    return 0;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3) { wprintf(L"usage: smoke <archive> <path-to-7z.dll>\n"); return 2; }

    FILE* f = _wfopen(argv[1], L"rb");
    if (!f) { wprintf(L"cannot open %s\n", argv[1]); return 1; }

    FileCtx fc{ f };
    SevenZipInStreamCallbacks cb{ myRead, mySeek };
    SevenZipArchiveHandle h = nullptr;

    int32_t hr = SevenZip_Open(&cb, &fc, argv[2], &h);
    wprintf(L"SevenZip_Open hr=0x%08X handle=%p\n", (unsigned)hr, h);
    if (hr != 0 || !h) { fclose(f); return 1; }

    uint32_t count = 0;
    SevenZip_GetItemCount(h, &count);
    wprintf(L"items=%u\n", count);

    uint32_t firstFile = 0xFFFFFFFFu;
    for (uint32_t i = 0; i < count; i++)
    {
        SevenZipItemInfo info{};
        wchar_t path[2048]; uint32_t pn = 0;
        SevenZip_GetItem(h, i, &info, path, 2048, &pn);
        if (i < 50)
            wprintf(L"  [%u] dir=%u size=%llu  %s\n", i, info.isDir, (unsigned long long)info.size, path);
        if (firstFile == 0xFFFFFFFFu && !info.isDir) firstFile = i;
    }

    if (firstFile != 0xFFFFFFFFu)
    {
        SevenZipItemInfo info{}; wchar_t path[2048]; uint32_t pn = 0;
        SevenZip_GetItem(h, firstFile, &info, path, 2048, &pn);
        WriteCtx w{ 0, 0 };
        int32_t ehr = SevenZip_ExtractItem(h, firstFile, myWrite, &w);
        wprintf(L"extract [%u] '%s' hr=0x%08X bytes=%llu (expected %llu) %s\n",
            firstFile, path, (unsigned)ehr,
            (unsigned long long)w.total, (unsigned long long)info.size,
            (ehr == 0 && w.total == info.size) ? L"OK" : L"MISMATCH");
    }

    SevenZip_Close(h);
    fclose(f);
    return 0;
}
