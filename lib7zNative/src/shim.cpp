// shim.cpp - implements the lib7znative.h C ABI on top of 7-Zip's UI/Common
// orchestration (CCodecs + CArchiveLink). This is the layer that gives 7zFM its
// automatic format detection and recursive "open inside"; the actual handlers/codecs
// are loaded at runtime from the bundled 7z.dll via CCodecs::LoadDll.
//
// Callback/COM patterns are modelled on the official sample CPP/7zip/UI/Client7z/Client7z.cpp.

#define LIB7ZNATIVE_EXPORTS
#include "../include/lib7znative.h"

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
// Output stream: bridges 7-Zip ISequentialOutStream -> host write callback
// ----------------------------------------------------------------------------
class CCallbackOutStream Z7_final :
  public ISequentialOutStream,
  public CMyUnknownImp
{
  Z7_IFACES_IMP_UNK_1(ISequentialOutStream)
public:
  SevenZipWriteFn WriteFn;
  void *Ctx;
};

Z7_COM7F_IMF(CCallbackOutStream::Write(const void *data, UInt32 size, UInt32 *processedSize))
{
  UInt32 processed = 0;
  const HRESULT hr = (HRESULT)WriteFn(Ctx, data, size, &processed);
  if (processedSize)
    *processedSize = processed;
  return hr;
}

// ----------------------------------------------------------------------------
// Extract callback: routes exactly one item's data to our output stream
// ----------------------------------------------------------------------------
class CExtractToCallback Z7_final :
  public IArchiveExtractCallback,
  public CMyUnknownImp
{
  Z7_IFACES_IMP_UNK_1(IArchiveExtractCallback)
  Z7_IFACE_COM7_IMP(IProgress)
public:
  UInt32 TargetIndex;
  CMyComPtr<ISequentialOutStream> Out;
  HRESULT Result;
  CExtractToCallback(): TargetIndex(0), Result(S_OK) {}
};

Z7_COM7F_IMF(CExtractToCallback::SetTotal(UInt64 /* total */)) { return S_OK; }
Z7_COM7F_IMF(CExtractToCallback::SetCompleted(const UInt64 * /* value */)) { return S_OK; }

Z7_COM7F_IMF(CExtractToCallback::GetStream(UInt32 index, ISequentialOutStream **outStream, Int32 askExtractMode))
{
  *outStream = NULL;
  if (index != TargetIndex || askExtractMode != NArchive::NExtract::NAskMode::kExtract)
    return S_OK;
  CMyComPtr<ISequentialOutStream> s = Out;
  *outStream = s.Detach();
  return S_OK;
}

Z7_COM7F_IMF(CExtractToCallback::PrepareOperation(Int32 /* askExtractMode */)) { return S_OK; }

Z7_COM7F_IMF(CExtractToCallback::SetOperationResult(Int32 opRes))
{
  Result = (opRes == NArchive::NExtract::NOperationResult::kOK) ? S_OK : E_FAIL;
  return S_OK;
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
  if (hr != S_OK) { delete arc; return hr; }
  if (arc->codecs->Formats.Size() == 0) { delete arc; return E_FAIL; }

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
  if (hr != S_OK) { delete arc; return (hr == S_FALSE) ? E_FAIL : hr; }
  if (arc->link.Arcs.Size() == 0) { delete arc; return E_FAIL; }

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

LIB7Z_API int32_t SevenZip_ExtractItem(
    SevenZipArchiveHandle handle, uint32_t index,
    SevenZipWriteFn write, void* outCtx)
{
  if (!handle || !write) return E_INVALIDARG;
  ShimArchive *arc = (ShimArchive*)handle;

  CCallbackOutStream *outSpec = new CCallbackOutStream;
  CMyComPtr<ISequentialOutStream> outRef = outSpec;
  outSpec->WriteFn = write;
  outSpec->Ctx = outCtx;

  CExtractToCallback *cbSpec = new CExtractToCallback;
  CMyComPtr<IArchiveExtractCallback> cb = cbSpec;
  cbSpec->TargetIndex = index;
  cbSpec->Out = outRef;

  const UInt32 idx = index;
  const HRESULT hr = arc->link.GetArchive()->Extract(&idx, 1, 0 /* testMode */, cb);
  if (hr != S_OK) return hr;
  return cbSpec->Result;
}

LIB7Z_API void SevenZip_Close(SevenZipArchiveHandle handle)
{
  if (handle) delete (ShimArchive*)handle;
}

} // extern "C"
