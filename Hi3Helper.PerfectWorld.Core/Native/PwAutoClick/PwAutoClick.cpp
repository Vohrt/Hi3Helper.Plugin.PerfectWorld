// PwAutoClick.dll — Perfect World pw_sdk launcher "开始游戏" auto-clicker (NTE / P5X).
//
// WHAT THIS IS
//   A tiny x64 helper DLL that the plugin injects into the vendor launcher process (NTEGame.exe /
//   P5XGame.exe — both Qt 5.15.17 apps). Its only job is to press the launcher's own "开始游戏"
//   (Start Game) button *programmatically*, at the exact moment the launcher is ready, so the plugin
//   no longer has to rely on the vendor's flawed "/autoplay" flag.
//
//   WHY NOT /autoplay: "/autoplay" makes the launcher SKIP its in-process resource check
//   (GameClientAgent::beginCheckGameResVersion). For 异环/NTE that skip corrupts the per-language voice
//   state (the game then believes every voice pack is installed and never downloads the real ones,
//   and loops on "更新失败" after returning to the login screen). Pressing the real button instead runs
//   the normal ready-check first, exactly like a human click, so on-demand voice works.
//
// HOW IT WORKS (all vendor symbols verified against the shipped Qt5Core.dll / Qt5Qml.dll exports)
//   1. The launcher exposes a C++ QObject named "BackgroudStageScheduler" (class GameClientAgent) to
//      its QML UI via QQmlContext::setContextProperty(const QString&, QObject*). The play button's QML
//      is literally `onClicked: BackgroudStageScheduler.gameActionBtnClicked()`.
//   2. We IAT-hook that setContextProperty import in every loaded module and capture the QObject* the
//      first time it is registered under the configured name.
//   3. We wait on a named Event that the plugin signals once its log tail sees the launcher reach
//      "all ready, wait for start game" (GameClientAgent::onGameElementUpdateFinished).
//   4. Just before the click we bring the launcher's OWN main window to the foreground. The launcher
//      gates the first-show of its start-game "cover" window — which hosts NTE's MSI Afterburner/RTSS
//      conflict dialog — on the host window having focus (log: "hostHasFocus:0"). Without focus that show
//      stalls on a fixed ~118s vendor timeout, so a conflict dialog would only appear after ~2 minutes
//      (and normally hidden helper windows briefly surface in the taskbar meanwhile). Activating first
//      makes it appear at once; it is harmless on the normal path (the launcher self-minimises to the
//      tray right after the click anyway, exactly like a manual click).
//   5. On the ready signal we call QMetaObject::invokeMethod(obj, "gameActionBtnClicked",
//      Qt::QueuedConnection) — a thread-safe queued call that runs on the launcher's GUI thread, i.e.
//      identical to a real click. That drives GameClientAgent::launchGame -> GameLifecycleMgr::startGame
//      -> the real game exe. We never touch the game process itself.
//
// CONFIG (passed by the plugin through inherited environment variables; sensible defaults otherwise):
//   PWAC_OBJ    (UTF-16)  QML context-property name          default "BackgroudStageScheduler"
//   PWAC_METHOD (ASCII)   invokable method to call            default "gameActionBtnClicked"
//   PWAC_EVENT  (UTF-16)  named Event to wait on              default "Local\\PwAutoClick_<pid>"
//   PWAC_LOG    (UTF-16)  optional diagnostic log path        default "%TEMP%\\PwAutoClick.log"
//
// This DLL links the static CRT (/MT) so it has no runtime-DLL dependency, and it does no work in
// DllMain beyond spawning its worker thread.

#include <windows.h>
#include <tlhelp32.h>
#include <atomic>
#include <cstdio>
#include <cstdint>
#include <cwchar>
#include <cstring>

// ---------------------------------------------------------------------------------------------------
// Verified Qt 5.15.17 export symbol names (from dumpbin /exports on the shipped DLLs).
// ---------------------------------------------------------------------------------------------------
static const char* kSym_setContextProperty =
    "?setContextProperty@QQmlContext@@QEAAXAEBVQString@@PEAVQObject@@@Z"; // QQmlContext::setContextProperty(const QString&, QObject*)
static const char* kSym_utf16 =
    "?utf16@QString@@QEBAPEBGXZ";                                          // const ushort* QString::utf16() const
static const char* kSym_invokeMethod =
    "?invokeMethod@QMetaObject@@SA_NPEAVQObject@@PEBDW4ConnectionType@Qt@@VQGenericArgument@@333333333@Z";
    // bool QMetaObject::invokeMethod(QObject*, const char* member, Qt::ConnectionType, QGenericArgument x10)

// ---------------------------------------------------------------------------------------------------
// Resolved function pointers. On x64 there is a single calling convention, so plain typedefs are fine.
// ---------------------------------------------------------------------------------------------------
typedef const wchar_t* (*Utf16Fn)(const void* qstringThis);
typedef void          (*SetCtxPropFn)(void* qmlContextThis, const void* qstringRef, void* qobject);
// QGenericArgument is a 16-byte trivially-copyable {const void* data; const char* name;}; a by-value
// 16-byte aggregate is passed by pointer under the Windows x64 ABI, so each val slot is a void*.
typedef bool          (*InvokeMethodFn)(void* obj, const char* member, int connType,
                                        void* v0, void* v1, void* v2, void* v3, void* v4,
                                        void* v5, void* v6, void* v7, void* v8, void* v9);

static Utf16Fn        g_utf16       = nullptr;
static InvokeMethodFn g_invoke      = nullptr;
static SetCtxPropFn   g_origSetCtx  = nullptr;
static std::atomic<void*> g_targetObj{ nullptr };

static wchar_t g_objName[128]  = L"BackgroudStageScheduler";
static char    g_method[128]   = "gameActionBtnClicked";
static wchar_t g_eventName[256] = L"";
static wchar_t g_logPath[MAX_PATH] = L"";

// ---------------------------------------------------------------------------------------------------
// Minimal diagnostic logging (no CRT file streams; plain Win32 so it is dependency-free and safe from
// any thread). Appends UTF-8 lines.
// ---------------------------------------------------------------------------------------------------
static void Log(const char* fmt, ...)
{
    char body[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(body, sizeof(body), _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[1200];
    int n = _snprintf_s(line, sizeof(line), _TRUNCATE,
                        "%02d:%02d:%02d.%03d [pid %lu] %s\r\n",
                        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                        GetCurrentProcessId(), body);
    if (n <= 0) return;

    HANDLE h = CreateFileW(g_logPath[0] ? g_logPath : L"PwAutoClick.log",
                           FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, line, (DWORD)n, &written, nullptr);
    CloseHandle(h);
}

static bool WStrEqualI(const wchar_t* a, const wchar_t* b)
{
    if (!a || !b) return false;
    return CompareStringOrdinal(a, -1, b, -1, TRUE) == CSTR_EQUAL;
}

// ---------------------------------------------------------------------------------------------------
// The hook that replaces QQmlContext::setContextProperty in every module's import table. It captures
// the QObject* the launcher registers under the configured QML name, then forwards to the real Qt
// implementation so the launcher behaves exactly as before.
// ---------------------------------------------------------------------------------------------------
static void Hooked_SetContextProperty(void* qmlContextThis, const void* qstringRef, void* qobject)
{
    __try
    {
        if (!g_targetObj.load(std::memory_order_acquire) && g_utf16 && qstringRef)
        {
            const wchar_t* name = g_utf16(qstringRef);
            if (WStrEqualI(name, g_objName))
            {
                g_targetObj.store(qobject, std::memory_order_release);
                Log("captured context object '%ls' = %p", g_objName, qobject);
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // Never let a probe fault cross back into the launcher.
    }

    if (g_origSetCtx)
        g_origSetCtx(qmlContextThis, qstringRef, qobject);
}

// ---------------------------------------------------------------------------------------------------
// Overwrite one IAT slot with the hook, flipping page protection around the write. Returns true on success.
// ---------------------------------------------------------------------------------------------------
static bool PatchIatSlot(IMAGE_THUNK_DATA* addrThunk, void* hook)
{
    DWORD oldProt = 0;
    if (!VirtualProtect(&addrThunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProt))
        return false;
    addrThunk->u1.Function = reinterpret_cast<ULONGLONG>(hook);
    VirtualProtect(&addrThunk->u1.Function, sizeof(void*), oldProt, &oldProt);
    return true;
}

// ---------------------------------------------------------------------------------------------------
// Patch the IAT entry for (dllName!funcMangled) inside a single loaded module. Returns true if the
// import was found and redirected. Handles both the normal case (an import-name table exists, matched by
// name) and the bound-imports case (OriginalFirstThunk == 0, so FirstThunk already holds resolved VAs —
// there is no name table to read, and it is matched by comparing the resolved target address instead).
// ---------------------------------------------------------------------------------------------------
static bool HookIatInModule(HMODULE hModule, const char* dllName, const char* funcMangled, void* hook)
{
    auto base = reinterpret_cast<BYTE*>(hModule);
    auto dos  = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;

    DWORD impRva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!impRva) return false;

    // Resolve the real target address once so bound imports (no name table) can be matched by address.
    void* targetAddr = nullptr;
    if (HMODULE hDll = GetModuleHandleA(dllName))
        targetAddr = reinterpret_cast<void*>(GetProcAddress(hDll, funcMangled));

    bool hooked = false;
    for (auto imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + impRva); imp->Name; ++imp)
    {
        const char* name = reinterpret_cast<const char*>(base + imp->Name);
        if (_stricmp(name, dllName) != 0) continue;

        auto addrThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->FirstThunk);

        if (imp->OriginalFirstThunk)
        {
            // Normal case: the import-name table (OriginalFirstThunk) still holds name RVAs; match by name.
            auto nameThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->OriginalFirstThunk);
            for (; nameThunk->u1.AddressOfData; ++nameThunk, ++addrThunk)
            {
                if (nameThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG) continue; // imported by ordinal, skip
                auto ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + nameThunk->u1.AddressOfData);
                if (strcmp(reinterpret_cast<const char*>(ibn->Name), funcMangled) != 0) continue;
                if (PatchIatSlot(addrThunk, hook)) hooked = true;
            }
        }
        else if (targetAddr)
        {
            // Bound-imports case: no name table — FirstThunk holds resolved addresses. Match by address so
            // we never dereference a resolved VA as if it were an IMAGE_IMPORT_BY_NAME RVA (would crash).
            for (; addrThunk->u1.Function; ++addrThunk)
            {
                if (reinterpret_cast<void*>(addrThunk->u1.Function) != targetAddr) continue;
                if (PatchIatSlot(addrThunk, hook)) hooked = true;
            }
        }
    }
    return hooked;
}

// Hook the import in the main executable first (the launcher's own QmlView wrapper is the caller), then
// sweep every other loaded module so a vendor sub-DLL caller is covered too.
static int HookAllModules()
{
    int count = 0;
    if (HookIatInModule(GetModuleHandleW(nullptr), "Qt5Qml.dll", kSym_setContextProperty,
                        reinterpret_cast<void*>(&Hooked_SetContextProperty)))
        ++count;

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    if (snap != INVALID_HANDLE_VALUE)
    {
        MODULEENTRY32W me{};
        me.dwSize = sizeof(me);
        if (Module32FirstW(snap, &me))
        {
            do
            {
                if (me.hModule == GetModuleHandleW(nullptr)) continue;
                if (HookIatInModule(me.hModule, "Qt5Qml.dll", kSym_setContextProperty,
                                    reinterpret_cast<void*>(&Hooked_SetContextProperty)))
                    ++count;
            } while (Module32NextW(snap, &me));
        }
        CloseHandle(snap);
    }
    return count;
}

// ---------------------------------------------------------------------------------------------------
static void ResolveConfigFromEnv()
{
    wchar_t buf[256];
    DWORD n = GetEnvironmentVariableW(L"PWAC_OBJ", buf, 256);
    if (n > 0 && n < 128) wcscpy_s(g_objName, buf);

    wchar_t mbuf[256];
    n = GetEnvironmentVariableW(L"PWAC_METHOD", mbuf, 256);
    if (n > 0 && n < 128)
        WideCharToMultiByte(CP_ACP, 0, mbuf, -1, g_method, sizeof(g_method), nullptr, nullptr);

    n = GetEnvironmentVariableW(L"PWAC_EVENT", g_eventName, 256);
    if (n == 0 || n >= 256)
        _snwprintf_s(g_eventName, _countof(g_eventName), _TRUNCATE, L"Local\\PwAutoClick_%lu",
                     GetCurrentProcessId());

    n = GetEnvironmentVariableW(L"PWAC_LOG", g_logPath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH)
    {
        wchar_t tmp[MAX_PATH];
        DWORD tn = GetTempPathW(MAX_PATH, tmp);
        if (tn > 0 && tn < MAX_PATH)
            _snwprintf_s(g_logPath, _countof(g_logPath), _TRUNCATE, L"%sPwAutoClick.log", tmp);
    }
}

static bool ResolveQtSymbols()
{
    HMODULE core = GetModuleHandleW(L"Qt5Core.dll");
    HMODULE qml  = GetModuleHandleW(L"Qt5Qml.dll");
    // The launcher statically imports both, so they are already mapped by the time we are injected; a
    // short retry only guards against being injected unusually early.
    for (int i = 0; (!core || !qml) && i < 100; ++i)
    {
        Sleep(50);
        if (!core) core = GetModuleHandleW(L"Qt5Core.dll");
        if (!qml)  qml  = GetModuleHandleW(L"Qt5Qml.dll");
    }
    if (!core || !qml)
    {
        Log("Qt modules not found (Qt5Core=%p Qt5Qml=%p)", core, qml);
        return false;
    }

    g_utf16      = reinterpret_cast<Utf16Fn>(GetProcAddress(core, kSym_utf16));
    g_invoke     = reinterpret_cast<InvokeMethodFn>(GetProcAddress(core, kSym_invokeMethod));
    g_origSetCtx = reinterpret_cast<SetCtxPropFn>(GetProcAddress(qml, kSym_setContextProperty));

    Log("resolved utf16=%p invokeMethod=%p setContextProperty=%p", g_utf16, g_invoke, g_origSetCtx);
    return g_utf16 && g_invoke && g_origSetCtx;
}

static bool InvokeClick()
{
    void* target = g_targetObj.load(std::memory_order_acquire);
    if (!g_invoke || !target) return false;

    // Ten default-constructed QGenericArgument()s = ten zeroed 16-byte {data=null,name=null} structs.
    // Separate buffers avoid any aliasing if the callee writes through a by-value parameter copy. The
    // Windows x64 ABI requires the caller-created indirect temporary for a by-value aggregate to be
    // 16-byte aligned, so pin the alignment explicitly rather than rely on natural 8-byte alignment.
    struct alignas(16) GA { const void* data; const char* name; } ga[10];
    static_assert(sizeof(GA) == 16, "QGenericArgument must be 16 bytes");
    static_assert(alignof(GA) == 16, "QGenericArgument temporary must be 16-byte aligned");
    memset(ga, 0, sizeof(ga));

    const int kQueuedConnection = 2; // Qt::QueuedConnection — marshals onto the object's GUI thread.
    bool ok = g_invoke(target, g_method, kQueuedConnection,
                       &ga[0], &ga[1], &ga[2], &ga[3], &ga[4], &ga[5], &ga[6], &ga[7], &ga[8], &ga[9]);
    Log("invokeMethod('%s', QueuedConnection) -> %d", g_method, ok ? 1 : 0);
    return ok;
}

// ---------------------------------------------------------------------------------------------------
// Bring the launcher's own main window to the foreground just before the click. See header step 4: the
// launcher gates its start-game cover/conflict dialog's first-show on the host window having focus, so
// without focus that show (e.g. NTE's MSI Afterburner/RTSS conflict dialog) stalls on a fixed ~118s
// vendor timeout. We activate the window from inside the launcher process, which — with the standard
// AttachThreadInput foreground-lock workaround — is reliable even when another app owns the foreground.
// ---------------------------------------------------------------------------------------------------
struct FindMainWndCtx { DWORD pid; HWND best; long long bestArea; };

static BOOL CALLBACK EnumMainWndProc(HWND hwnd, LPARAM lp)
{
    auto* ctx = reinterpret_cast<FindMainWndCtx*>(lp);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid) return TRUE;                                   // another process
    if (!IsWindowVisible(hwnd)) return TRUE;                            // hidden helper window
    if (GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;             // owned popup, not the main window
    if (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) return TRUE;

    RECT r{};
    if (!GetWindowRect(hwnd, &r)) return TRUE;
    long long area = static_cast<long long>(r.right - r.left) * (r.bottom - r.top);
    if (area <= 0) return TRUE;
    if (area > ctx->bestArea) { ctx->bestArea = area; ctx->best = hwnd; } // largest = the real launcher window
    return TRUE;
}

static HWND FindMainLauncherWindow()
{
    FindMainWndCtx ctx{ GetCurrentProcessId(), nullptr, 0 };
    EnumWindows(&EnumMainWndProc, reinterpret_cast<LPARAM>(&ctx));
    return ctx.best;
}

static void ForegroundMainWindow()
{
    HWND hwnd = FindMainLauncherWindow();
    if (!hwnd)
    {
        Log("foreground activation skipped: main window not found");
        return;
    }

    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

    // Foreground-lock workaround: briefly attach this thread's input to the current foreground thread so
    // SetForegroundWindow is honoured even when another app currently owns the foreground.
    DWORD thisThread = GetCurrentThreadId();
    HWND  hFore      = GetForegroundWindow();
    DWORD foreThread = hFore ? GetWindowThreadProcessId(hFore, nullptr) : 0;
    bool  attached   = foreThread && foreThread != thisThread &&
                       AttachThreadInput(thisThread, foreThread, TRUE) != 0;

    BringWindowToTop(hwnd);
    BOOL sfw = SetForegroundWindow(hwnd);

    if (attached) AttachThreadInput(thisThread, foreThread, FALSE);

    // Let the GUI thread process the resulting WM_ACTIVATE BEFORE we post the click, so the start-game
    // flow observes the host window as focused. Bounded poll (up to ~1s) plus a short settle delay.
    for (int i = 0; i < 40 && GetForegroundWindow() != hwnd; ++i) Sleep(25);
    Sleep(150);

    Log("foreground activation: hwnd=%p setForeground=%d foreground=%p",
        hwnd, sfw ? 1 : 0, GetForegroundWindow());
}

static DWORD WINAPI WorkerThread(LPVOID)
{
    ResolveConfigFromEnv();
    Log("PwAutoClick worker start: obj='%ls' method='%s' event='%ls'", g_objName, g_method, g_eventName);

    if (!ResolveQtSymbols())
    {
        Log("symbol resolution failed; auto-click disabled for this launch");
        return 0;
    }

    int hookedModules = HookAllModules();
    Log("installed setContextProperty IAT hook in %d module(s)", hookedModules);

    // A manual-reset named Event so the plugin's "ready" signal is not missed regardless of ordering.
    HANDLE ev = CreateEventW(nullptr, TRUE, FALSE, g_eventName);
    if (!ev)
    {
        Log("CreateEventW('%ls') failed err=%lu", g_eventName, GetLastError());
        return 0;
    }

    // Wait for the plugin to report the launcher reached "all ready, wait for start game".
    DWORD wr = WaitForSingleObject(ev, 20 * 60 * 1000); // generous cap; process exit ends us anyway
    if (wr != WAIT_OBJECT_0)
    {
        Log("ready-event wait ended without signal (wr=%lu); no click", wr);
        CloseHandle(ev);
        return 0;
    }
    Log("ready signal received");

    // The object is captured at QML-view creation, which precedes the ready state, but wait briefly in
    // case of an unusual ordering.
    for (int i = 0; i < 200 && !g_targetObj.load(std::memory_order_acquire); ++i) Sleep(50);
    if (!g_targetObj.load(std::memory_order_acquire))
    {
        Log("ready signalled but context object was never captured; no click (manual fallback)");
        CloseHandle(ev);
        return 0;
    }

    // Bring the launcher's main window to the foreground so the start-game flow (and any conflict dialog
    // it raises) sees the host window as focused and shows at once, instead of stalling on the launcher's
    // ~118s no-focus timeout. Harmless on the normal path — the launcher self-minimises after the click.
    ForegroundMainWindow();

    InvokeClick();
    CloseHandle(ev);
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    UNREFERENCED_PARAMETER(hModule);
    if (reason == DLL_PROCESS_ATTACH)
    {
        // Do NOT call DisableThreadLibraryCalls: this DLL links the static CRT (/MT), which relies on the
        // per-thread DLL_THREAD_ATTACH/DETACH notifications to initialise its per-thread state. All real work
        // runs on the worker thread, so no heavy work happens under the loader lock here.
        HANDLE t = CreateThread(nullptr, 0, &WorkerThread, nullptr, 0, nullptr);
        if (!t) return FALSE; // report load failure so the injector's exit-code check reports it, not a false success
        CloseHandle(t);
    }
    return TRUE;
}
