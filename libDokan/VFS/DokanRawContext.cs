using DokanNet;
using Serilog;
using System.Reflection;
using System.Runtime.InteropServices;

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
        //The adapter type and its validated 'ptr' field (null = raw access unavailable for that type), published as
        //one object so concurrent Dokan threads never see a type paired with another type's field.
        sealed record FieldCache(Type Type, FieldInfo? Field);
        static FieldCache? cache;
        static bool failureLogged;

        static unsafe long* FieldPointer(IDokanFileInfo info)
        {
            try
            {
                var t = info.GetType();
                var c = Volatile.Read(ref cache);
                if (c == null || !ReferenceEquals(c.Type, t))
                {
                    //published only after validation (a throw lands in the catch and publishes nothing)
                    var field = t.GetField("ptr", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field == null)
                    {
                        WarnOnce($"DokanRawContext: no 'ptr' field on {t.FullName} - raw context access unavailable");
                    }
                    else if (!ContextIsFirstField(field.FieldType))
                    {
                        WarnOnce($"DokanRawContext: the native struct behind {t.FullName} no longer starts with an 8-byte 'context' field - raw context access disabled");
                        field = null;
                    }
                    c = new FieldCache(t, field);
                    Volatile.Write(ref cache, c);
                }
                if (c.Field == null) return null;

                var boxed = c.Field.GetValue(info);
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

        static void WarnOnce(string message)
        {
            if (failureLogged) return;
            failureLogged = true;
            Log.Warning(message);
        }

        //Every access below reads or writes the first 8 bytes of the native struct, which is the context only while
        //DokanNet lays it out first (true for 2.3.0.3). Checked once per adapter type, so a package bump that moves
        //the field disables raw access instead of zeroing a different field (gh review F24).
        static bool ContextIsFirstField(Type ptrFieldType)
        {
            var structType = ptrFieldType.IsPointer ? ptrFieldType.GetElementType() : null;
            var contextField = structType?.GetField("context", BindingFlags.NonPublic | BindingFlags.Instance);
            return structType != null
                && contextField?.FieldType == typeof(long)
                && Marshal.OffsetOf(structType, "context") == IntPtr.Zero;
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
