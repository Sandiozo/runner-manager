/*
 * The load configuration of RunnerManagerLauncher.exe. The C runtime normally supplies it; the launcher has no C
 * runtime, so it is here. Windows reads two things from it that the launcher needs:
 *   - DependentLoadFlags, which the linker fills in from /DEPENDENTLOADFLAG:0x800: the DLLs in the import table are
 *     loaded from System32 only, never from the launcher's own folder;
 *   - the Control Flow Guard tables the linker builds for /GUARD:CF. Windows turns CFG on for the process only for an
 *     image whose load configuration has them.
 * The linker defines every __guard_* symbol below except __guard_check_icall_fptr.
 *
 * __guard_check_icall_fptr: Windows writes its CFG check function into this pointer when it loads the exe, and
 * without it does not start a /GUARD:CF image at all (the process ends with 0xC000003E before the launcher's first
 * line). The launcher makes no indirect calls, so the pointer is never used; it starts at a function that does
 * nothing, as in the C runtime. It lives in a read-only section of its own, which the loader unprotects to write it.
 *
 * Left at 0 on purpose: the launcher has nothing on the stack that /GS guards (it keeps buffers and larger structures
 * on the heap) and no indirect calls through the dispatch pointer, so it needs neither a security cookie nor
 * __guard_dispatch_icall_fptr. It is still compiled with /GS and /guard:cf: if a later change needs one of them, the
 * link fails on the missing symbol (__security_cookie, __security_check_cookie, __GSHandlerCheck or
 * __guard_dispatch_icall_fptr), which is the moment to decide.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

extern PVOID __guard_fids_table[];
extern ULONG __guard_fids_count;
extern ULONG __guard_flags;
extern PVOID __guard_iat_table[];
extern ULONG __guard_iat_count;
extern PVOID __guard_longjmp_table[];
extern ULONG __guard_longjmp_count;
extern PVOID __guard_eh_cont_table[];
extern ULONG __guard_eh_cont_count;

static void __fastcall GuardCheckNothing(ULONG_PTR target)
{
    (void)target;
}

#pragma section(".00cfg", read)
__declspec(allocate(".00cfg")) volatile PVOID __guard_check_icall_fptr = (PVOID)GuardCheckNothing;

/* The count and flag symbols are absolute: their address is the value. */
const IMAGE_LOAD_CONFIG_DIRECTORY64 _load_config_used = {
    .Size = sizeof(IMAGE_LOAD_CONFIG_DIRECTORY64),
    .GuardCFCheckFunctionPointer = (ULONGLONG)&__guard_check_icall_fptr,
    .GuardCFFunctionTable = (ULONGLONG)__guard_fids_table,
    .GuardCFFunctionCount = (ULONGLONG)&__guard_fids_count,
    .GuardFlags = (DWORD)(ULONG_PTR)&__guard_flags,
    .GuardAddressTakenIatEntryTable = (ULONGLONG)__guard_iat_table,
    .GuardAddressTakenIatEntryCount = (ULONGLONG)&__guard_iat_count,
    .GuardLongJumpTargetTable = (ULONGLONG)__guard_longjmp_table,
    .GuardLongJumpTargetCount = (ULONGLONG)&__guard_longjmp_count,
    .GuardEHContinuationTable = (ULONGLONG)__guard_eh_cont_table,
    .GuardEHContinuationCount = (ULONGLONG)&__guard_eh_cont_count,
};
