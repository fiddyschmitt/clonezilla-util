// shim.cpp - implements the lib7znative.h C ABI on top of 7-Zip's UI/Common
// orchestration (CCodecs + CArchiveLink). This is the layer that gives 7zFM its
// automatic format detection and recursive "open inside"; the actual handlers/codecs
// are loaded at runtime from the bundled 7z.dll via CCodecs::LoadDll.
//
// Callback/COM patterns are modelled on the official sample CPP/7zip/UI/Client7z/Client7z.cpp.

#define LIB7ZNATIVE_EXPORTS
#include "../include/lib7znative.h"

#include <cstdio>
#define DIAG(...) do { fprintf(stderr, "[lib7zNative] " __VA_ARGS__); fputc('\n', stderr); fflush(stderr); } while (0)

#include "Common/MyWindows.h"
#include "Common/MyInitGuid.h"      // defines the IID_* GUIDs - include exactly once per module
#include "Common/MyCom.h"
#include "Common/MyString.h"
#include "Common/StringConvert.h"

#include "Windows/PropVariant.h"
#include "Windows/PropVariantConv.h"

#include "7zip/Archive/IArchive.h"
#include "7zip/UI/Common/LoadCodecs.h"
#include "7zip/UI/Common/OpenArchive.h"
// Defines the folder/agent IIDs (IID_IFolderArchiveExtractCallback2, IID_IGetProp,
// IID_IFolderExtractToStreamCallback) that ArchiveExtractCallback.cpp references - this header,
// compiled in the same TU as MyInitGuid.h, emits those GUIDs.
#include "7zip/UI/Common/IFileExtractCallback.h"

using namespace NWindows;

// Referenced by NWindows::NDLL::GetModuleDirPrefix() (see Client7z.cpp).
#ifdef _WIN32
extern HINSTANCE g_hInstance;
HINSTANCE g_hInstance = NULL;
#endif

// ----------------------------------------------------------------------------
// Input stream: bridges 7-Zip IInStream -> host read/seek callbacks
// ----------------------------------------------------------------------------
class CCallbackInStream Z7_final :
  public IInStream,
  public CMyUnknownImp
{
  Z7_IFACES_IMP_UNK_2(ISequentialInStream, IInStream)
public:
  SevenZipReadFn ReadFn;
  SevenZipSeekFn SeekFn;
  void *Ctx;
};

Z7_COM7F_IMF(CCallbackInStream::Read(void *data, UInt32 size, UInt32 *processedSize))
{
  UInt32 processed = 0;
  const HRESULT hr = (HRESULT)ReadFn(Ctx, data, size, &processed);
  if (processedSize)
    *processedSize = processed;
  return hr;
}

Z7_COM7F_IMF(CCallbackInStream::Seek(Int64 offset, UInt32 seekOrigin, UInt64 *newPosition))
{
  UInt64 newPos = 0;
  const HRESULT hr = (HRESULT)SeekFn(Ctx, offset, seekOrigin, &newPos);
  if (newPosition)
    *newPosition = newPos;
  return hr;
}

// ----------------------------------------------------------------------------
// Opaque handle
// ----------------------------------------------------------------------------
struct ShimArchive
{
  CCodecs *codecs;
  CMyComPtr<IInStream> inStream;
  CArchiveLink link;

  ShimArchive(): codecs(NULL) {}
  ~ShimArchive()
  {
    link.Release();
    inStream.Release();
    if (codecs)
    {
      codecs->CloseLibs();           // break the CCodecs<->Libs reference loop (see CCodecs::CReleaser)
      ICompressCodecsInfo *unk = codecs;
      unk->Release();
    }
  }
};

static FString WCharToFString(const wchar_t *s) { return us2fs(UString(s)); }

static int64_t FileTimeToInt64(const FILETIME &ft)
{
  return ((int64_t)ft.dwHighDateTime << 32) | (int64_t)ft.dwLowDateTime;
}

extern "C" {

LIB7Z_API int32_t SevenZip_Open(
    const SevenZipInStreamCallbacks* in, void* inCtx,
    const wchar_t* sevenZipDllPath,
    SevenZipArchiveHandle* outHandle)
{
  if (outHandle) *outHandle = NULL;
  if (!in || !in->read || !in->seek || !sevenZipDllPath || !outHandle)
    return E_INVALIDARG;

  ShimArchive *arc = new ShimArchive();

  arc->codecs = new CCodecs;
  { ICompressCodecsInfo *unk = arc->codecs; unk->AddRef(); }

  HRESULT hr = arc->codecs->LoadDll(WCharToFString(sevenZipDllPath), true);
  if (hr != S_OK) { DIAG("LoadDll failed hr=0x%08X", (unsigned)hr); delete arc; return hr; }
  if (arc->codecs->Formats.Size() == 0) { DIAG("no formats loaded from 7z.dll"); delete arc; return E_FAIL; }

  CCallbackInStream *inSpec = new CCallbackInStream;
  arc->inStream = inSpec;
  inSpec->ReadFn = in->read;
  inSpec->SeekFn = in->seek;
  inSpec->Ctx = inCtx;

  // COpenOptions leaves these as null/garbage; CArchiveLink::Open dereferences all three.
  // Empty containers == auto-detect, no excluded formats, no open properties.
  CObjectVector<COpenType> openTypes;
  CIntVector excludedFormats;
  CObjectVector<CProperty> openProps;

  COpenOptions options;
  options.codecs = arc->codecs;
  options.stream = arc->inStream;
  options.types = &openTypes;
  options.excludedFormats = &excludedFormats;
  options.props = &openProps;
  // options.openType.Recursive defaults to true -> recursive "open inside"

  hr = arc->link.Open(options);
  // "opened" but no handler recognised the data == not an archive/filesystem (e.g. a raw bios_grub
  // partition). Normalise that to S_FALSE so the host can tell it apart from a genuine error.
  if (hr == S_OK && arc->link.Arcs.Size() == 0)
    hr = S_FALSE;
  if (hr != S_OK)
  {
    // S_FALSE ("not a recognised archive/filesystem", e.g. a raw bios_grub partition) is expected
    // and handled by the host - don't treat it as noise. Only diagnose genuine failures.
    if (hr != S_FALSE)
    {
      const CArcErrorInfo &e = arc->link.NonOpen_ErrorInfo;
      DIAG("link.Open hr=0x%08X arcs=%u nonOpenFmtIdx=%d errFlags=0x%08X warnFlags=0x%08X",
           (unsigned)hr, (unsigned)arc->link.Arcs.Size(), e.ErrorFormatIndex,
           (unsigned)e.GetErrorFlags(), (unsigned)e.GetWarningFlags());
    }
    delete arc;
    // Return the real HRESULT. S_FALSE (0x00000001) distinctly means "not a recognised
    // archive/filesystem"; genuine failures return their own error codes.
    return hr;
  }

  *outHandle = arc;
  return S_OK;
}

LIB7Z_API int32_t SevenZip_GetItemCount(SevenZipArchiveHandle handle, uint32_t* outCount)
{
  if (!handle || !outCount) return E_INVALIDARG;
  ShimArchive *arc = (ShimArchive*)handle;
  UInt32 n = 0;
  const HRESULT hr = arc->link.GetArchive()->GetNumberOfItems(&n);
  *outCount = n;
  return hr;
}

LIB7Z_API int32_t SevenZip_GetItem(
    SevenZipArchiveHandle handle, uint32_t index,
    SevenZipItemInfo* outInfo,
    wchar_t* pathBuf, uint32_t pathBufChars, uint32_t* outPathChars)
{
  if (!handle || !outInfo) return E_INVALIDARG;
  ShimArchive *arc = (ShimArchive*)handle;
  IInArchive *a = arc->link.GetArchive();

  UString path;
  HRESULT hr = arc->link.GetArc()->GetItem_Path(index, path);
  if (hr != S_OK) return hr;

  if (outPathChars) *outPathChars = (uint32_t)path.Len();
  if (pathBuf && pathBufChars > 0)
  {
    uint32_t n = (uint32_t)path.Len();
    if (n > pathBufChars - 1) n = pathBufChars - 1;
    if (n) memcpy(pathBuf, (const wchar_t*)path, (size_t)n * sizeof(wchar_t));
    pathBuf[n] = 0;
  }

  bool isDir = false;
  Archive_IsItem_Dir(a, index, isDir);
  outInfo->isDir = isDir ? 1 : 0;

  outInfo->size = 0;
  { NCOM::CPropVariant prop;
    if (a->GetProperty(index, kpidSize, &prop) == S_OK)
    { UInt64 v = 0; if (ConvertPropVariantToUInt64(prop, v)) outInfo->size = v; } }

  outInfo->modifiedFileTime = outInfo->createdFileTime = outInfo->accessedFileTime = 0;
  { NCOM::CPropVariant prop; if (a->GetProperty(index, kpidMTime, &prop) == S_OK && prop.vt == VT_FILETIME) outInfo->modifiedFileTime = FileTimeToInt64(prop.filetime); }
  { NCOM::CPropVariant prop; if (a->GetProperty(index, kpidCTime, &prop) == S_OK && prop.vt == VT_FILETIME) outInfo->createdFileTime  = FileTimeToInt64(prop.filetime); }
  { NCOM::CPropVariant prop; if (a->GetProperty(index, kpidATime, &prop) == S_OK && prop.vt == VT_FILETIME) outInfo->accessedFileTime = FileTimeToInt64(prop.filetime); }

  return S_OK;
}

// ----------------------------------------------------------------------------
// On-demand seekable per-item stream (IInArchiveGetStream)
// ----------------------------------------------------------------------------
struct ShimItemStream
{
  CMyComPtr<IInStream> stream;
};

LIB7Z_API int32_t SevenZip_OpenItemStream(
    SevenZipArchiveHandle handle, uint32_t index,
    SevenZipItemStreamHandle* outStream)
{
  if (outStream) *outStream = NULL;
  if (!handle || !outStream) return E_INVALIDARG;
  ShimArchive *arc = (ShimArchive*)handle;

  CMyComPtr<IInArchiveGetStream> getStream;
  HRESULT hr = arc->link.GetArchive()->QueryInterface(IID_IInArchiveGetStream, (void**)&getStream);
  if (hr != S_OK || !getStream) { DIAG("handler has no IInArchiveGetStream hr=0x%08X", (unsigned)hr); return (hr == S_OK) ? E_NOINTERFACE : hr; }

  CMyComPtr<ISequentialInStream> seqStream;
  hr = getStream->GetStream(index, &seqStream);
  if (hr != S_OK) { DIAG("GetStream(%u) hr=0x%08X", index, (unsigned)hr); return hr; }
  if (!seqStream) { DIAG("GetStream(%u) returned null (item has no seekable stream)", index); return E_FAIL; }

  CMyComPtr<IInStream> inStream;
  hr = seqStream->QueryInterface(IID_IInStream, (void**)&inStream);
  if (hr != S_OK || !inStream) { DIAG("item stream is not seekable (no IInStream) hr=0x%08X", (unsigned)hr); return (hr == S_OK) ? E_NOINTERFACE : hr; }

  ShimItemStream *item = new ShimItemStream();
  item->stream = inStream;
  *outStream = item;
  return S_OK;
}

LIB7Z_API int32_t SevenZip_ItemRead(
    SevenZipItemStreamHandle stream, void* buf, uint32_t size, uint32_t* processed)
{
  if (processed) *processed = 0;
  if (!stream) return E_INVALIDARG;
  ShimItemStream *item = (ShimItemStream*)stream;
  UInt32 got = 0;
  const HRESULT hr = item->stream->Read(buf, size, &got);
  if (processed) *processed = got;
  return hr;
}

LIB7Z_API int32_t SevenZip_ItemSeek(
    SevenZipItemStreamHandle stream, int64_t offset, uint32_t origin, uint64_t* newPosition)
{
  if (!stream) return E_INVALIDARG;
  ShimItemStream *item = (ShimItemStream*)stream;
  UInt64 newPos = 0;
  const HRESULT hr = item->stream->Seek(offset, origin, &newPos);
  if (newPosition) *newPosition = newPos;
  return hr;
}

LIB7Z_API void SevenZip_ItemClose(SevenZipItemStreamHandle stream)
{
  if (stream) delete (ShimItemStream*)stream;
}

LIB7Z_API void SevenZip_Close(SevenZipArchiveHandle handle)
{
  if (handle) delete (ShimArchive*)handle;
}

} // extern "C"
