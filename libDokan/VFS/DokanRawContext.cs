using DokanNet;
using Serilog;
using System.Reflection;

namespace libDokan.VFS
{
    //Direct access to the RAW numeric value of info.Context - the GCHandle value DokanNet 2.3.0.3
    //stores in this operation's native DOKAN_FILE_INFO struct - without dereferencing it.
    //
    //Why this exists (L11 root cause): DokanNet's Context property is a raw GCHandle round-tripped
    //through the kernel. The getter does ((GCHandle)(nint)context).Target with no validation, and
    //the setter ALWAYS calls GCHandle.Free() on the current value before storing a new one - even
    //when assigning null. Both are undefined behavior once the underlying handle has been freed:
    //the runtime recycles freed slots on the very next GCHandle.Alloc, so a stale number can point
    //at ANOTHER file's live stream (get = cross-file bleed) or free another file's live handle
    //(set = detonate an unrelated open, the self-worsening chain observed in the L11 forensics).
    //
    //TryZero clears the native field WITHOUT GCHandle.Free, deliberately leaking one small object
    //instead of freeing a value we distrust. TryRead returns the number for forensics/guards.
    //
    //Mechanics: info arrives as DokanNet.Legacy.DokanOperationsAdapter+DokanFileInfoAdapter, which
    //holds `private readonly DokanFileInfo* ptr` into the per-event native struct; DokanFileInfo is
    //[StructLayout(Sequential, Pack=4)] with `private long context` as its FIRST field.
    public static class DokanRawContext
    {
        static Type? cachedType;
        static FieldInfo? cachedField;
        static bool failureLogged;

        static unsafe long* FieldPointer(IDokanFileInfo info)
        {
            try
            {
                var t = info.GetType();
                if (!ReferenceEquals(t, cachedType))
                {
                    cachedField = t.GetField("ptr", BindingFlags.NonPublic | BindingFlags.Instance);
                    cachedType = t;
                    if (cachedField == null && !failureLogged)
                    {
                        failureLogged = true;
                        Log.Warning("DokanRawContext: no 'ptr' field on {Type} - raw context access unavailable", t.FullName);
                    }
                }
                if (cachedField == null) return null;

                var boxed = cachedField.GetValue(info);
                if (boxed is not Pointer p) return null;
                return (long*)Pointer.Unbox(p);
            }
            catch (Exception ex)
            {
                if (!failureLogged)
                {
                    failureLogged = true;
                    Log.Warning(ex, "DokanRawContext: raw context access failed");
                }
                return null;
            }
        }

        public static unsafe bool TryRead(IDokanFileInfo info, out long raw)
        {
            var p = FieldPointer(info);
            if (p == null) { raw = 0; return false; }
            raw = *p;
            return true;
        }

        //Clears this operation's native context field WITHOUT freeing the GCHandle. When the
        //operation completes, dokany writes the 0 back to the shared per-open UserContext
        //(ReleaseDokanOpenInfo, dokan.c), so subsequent operations on the open see a null context
        //and DokanVFS serves them by name - correct by construction. The GCHandle (if any) leaks;
        //that is intentional, see the class comment.
        public static unsafe bool TryZero(IDokanFileInfo info)
        {
            var p = FieldPointer(info);
            if (p == null) return false;
            *p = 0;
            return true;
        }
    }
}
