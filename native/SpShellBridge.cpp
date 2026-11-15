// Packaged replacements for StartupContent\SpShell.dll and SpArgs40.dll.
// Originals become SpShellCore.dll / SpArgs40Core.dll. The native host
// preloads SpArgs40, then opens required archives, then may load SpShell.
// Xbox AppContainer cannot create a D:→SSD reparse and must not copy the
// archives onto internal storage, so CreateFile2 / CreateFileW / attribute
// queries for missing D: overlay files are remapped through archive-link.txt
// onto the SSD and opened with FromApp or the StorageFolder broker handle.
// SpArgs40 DllMain patches the host IAT before that asset check.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <roapi.h>
#include <windows.storage.h>
#include <wrl.h>
#include <wrl/wrappers/corewrappers.h>
#include <cstdint>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Wrappers::HStringReference;
using ABI::Windows::Foundation::IAsyncInfo;
using ABI::Windows::Foundation::AsyncStatus;
using ABI::Windows::Storage::IKnownFoldersStatics;
using ABI::Windows::Storage::IStorageFolder;
using ABI::Windows::Storage::IStorageItem;
using ABI::Windows::Storage::IStorageFile;
enum HANDLE_OPTIONS
{
    HO_NONE = 0,
    HO_SEQUENTIAL_SCAN = 0x8000000,
    HO_RANDOM_ACCESS = 0x10000000,
    HO_OVERLAPPED = 0x40000000
};
enum HANDLE_ACCESS_OPTIONS
{
    HAO_NONE = 0,
    HAO_READ_ATTRIBUTES = 0x80,
    HAO_READ = 0x120089,
    HAO_WRITE = 0x120116,
    HAO_DELETE = 0x10000
};
enum HANDLE_SHARING_OPTIONS
{
    HSO_SHARE_NONE = 0,
    HSO_SHARE_READ = 0x1,
    HSO_SHARE_WRITE = 0x2,
    HSO_SHARE_DELETE = 0x4
};
struct __declspec(uuid("5CA296B2-2C25-4D22-B785-B885C8201E6A")) IStorageItemHandleAccess : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE Create(
        HANDLE_ACCESS_OPTIONS accessOptions,
        HANDLE_SHARING_OPTIONS sharingOptions,
        HANDLE_OPTIONS options,
        IUnknown* oplockBreakingHandler,
        HANDLE* interopHandle) = 0;
};

using CreateFile2Fn = HANDLE(WINAPI*)(LPCWSTR, DWORD, DWORD, DWORD, LPCREATEFILE2_EXTENDED_PARAMETERS);
using CreateFileWFn = HANDLE(WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
using CreateFileAFn = HANDLE(WINAPI*)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
using GetFileAttributesExWFn = BOOL(WINAPI*)(LPCWSTR, GET_FILEEX_INFO_LEVELS, LPVOID);
using GetFileAttributesWFn = DWORD(WINAPI*)(LPCWSTR);
using GetFileAttributesAFn = DWORD(WINAPI*)(LPCSTR);
using GetProcAddressFn = FARPROC(WINAPI*)(HMODULE, LPCSTR);
using LoadLibraryExWFn = HMODULE(WINAPI*)(LPCWSTR, HANDLE, DWORD);
using LoadPackagedLibraryFn = HMODULE(WINAPI*)(LPCWSTR, DWORD);
using CreateFile2FromAppWFn = HANDLE(WINAPI*)(LPCWSTR, DWORD, DWORD, DWORD, LPCREATEFILE2_EXTENDED_PARAMETERS);
using GetFileAttributesExFromAppWFn = BOOL(WINAPI*)(LPCWSTR, GET_FILEEX_INFO_LEVELS, LPVOID);

static HMODULE g_self;
static HMODULE g_core;
static CreateFile2Fn g_CreateFile2;
static CreateFileWFn g_CreateFileW;
static CreateFileAFn g_CreateFileA;
static GetFileAttributesExWFn g_GetFileAttributesExW;
static GetFileAttributesWFn g_GetFileAttributesW;
static GetFileAttributesAFn g_GetFileAttributesA;
static GetProcAddressFn g_GetProcAddress;
static LoadLibraryExWFn g_LoadLibraryExW;
static LoadPackagedLibraryFn g_LoadPackagedLibrary;
static CreateFile2FromAppWFn g_CreateFile2FromAppW;
static GetFileAttributesExFromAppWFn g_GetFileAttributesExFromAppW;
static SRWLOCK g_lock = SRWLOCK_INIT;
static bool g_hooked;
static wchar_t g_linkTarget[MAX_PATH];
static wchar_t g_runtimeRoot[MAX_PATH];
static unsigned g_loggedOpens;
static void* HookNamed(const char* name);
static const wchar_t kLooseRoot[] = L"D:\\DevelopmentFiles\\LooseApps\\SpiderManRuntime-20261008";

static void Log(const wchar_t* text)
{
    if (!g_CreateFile2) return;
    wchar_t dirs[4][MAX_PATH]{};
    if (g_runtimeRoot[0]) wcscpy_s(dirs[0], g_runtimeRoot);
    GetCurrentDirectoryW(MAX_PATH, dirs[1]);
    wcscpy_s(dirs[2], kLooseRoot);
    wcscpy_s(dirs[3], L"Q:\\Users\\UserMgr1\\AppData\\Local\\Packages\\Local.UwpSpiderCombinedPrototype_tw4nvkrr1gm8e\\LocalState");
    for (int i = 0; i < 4; ++i)
    {
        if (!dirs[i][0] || wcslen(dirs[i]) + 22 >= MAX_PATH) continue;
        wchar_t path[MAX_PATH];
        wcscpy_s(path, dirs[i]);
        wcscat_s(path, L"\\storage-bridge.log");
        HANDLE file = g_CreateFile2(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_ALWAYS, nullptr);
        if (file == INVALID_HANDLE_VALUE) continue;
        SYSTEMTIME utc;
        GetSystemTime(&utc);
        wchar_t line[1024];
        int n = swprintf_s(line, L"%04u-%02u-%02uT%02u:%02u:%02uZ %s\r\n",
            utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, text);
        if (n > 0)
        {
            DWORD written = 0;
            WriteFile(file, line, static_cast<DWORD>(n * sizeof(wchar_t)), &written, nullptr);
        }
        CloseHandle(file);
    }
}

static bool Equals(const wchar_t* a, const wchar_t* b)
{
    return _wcsicmp(a, b) == 0;
}

static const wchar_t* FindInsensitive(const wchar_t* hay, const wchar_t* needle)
{
    size_t n = wcslen(needle);
    if (n == 0) return hay;
    for (const wchar_t* p = hay; *p; ++p)
    {
        size_t i = 0;
        while (i < n && p[i] && towlower(p[i]) == towlower(needle[i])) ++i;
        if (i == n) return p;
    }
    return nullptr;
}

static bool ReadLinkFile(const wchar_t* file, wchar_t* dest, size_t cch)
{
    if (!g_CreateFile2) return false;
    HANDLE handle = g_CreateFile2(file, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    char utf8[MAX_PATH * 3];
    DWORD n = 0;
    BOOL ok = ReadFile(handle, utf8, sizeof(utf8) - 1, &n, nullptr);
    CloseHandle(handle);
    if (!ok || n == 0) return false;
    utf8[n] = 0;
    while (n && (utf8[n - 1] == '\n' || utf8[n - 1] == '\r' || utf8[n - 1] == ' ')) utf8[--n] = 0;
    if (!MultiByteToWideChar(CP_UTF8, 0, utf8, -1, dest, static_cast<int>(cch)))
        return MultiByteToWideChar(CP_ACP, 0, utf8, -1, dest, static_cast<int>(cch)) > 0;
    return dest[0] != 0;
}

static bool TryLinkAt(const wchar_t* root)
{
    if (!root || !root[0] || wcslen(root) + 18 >= MAX_PATH) return false;
    wchar_t link[MAX_PATH];
    wcscpy_s(link, root);
    wcscat_s(link, L"\\archive-link.txt");
    if (!ReadLinkFile(link, g_linkTarget, MAX_PATH) || !g_linkTarget[0]) return false;
    size_t t = wcslen(g_linkTarget);
    while (t && (g_linkTarget[t - 1] == L'\\' || g_linkTarget[t - 1] == L'/')) g_linkTarget[--t] = 0;
    if (!g_linkTarget[0]) return false;
    wcscpy_s(g_runtimeRoot, root);
    return true;
}

static bool LoadArchiveLink(const wchar_t* requested)
{
    if (g_linkTarget[0]) return true;
    wchar_t full[MAX_PATH];
    if (requested && GetFullPathNameW(requested, MAX_PATH, full, nullptr))
    {
        const wchar_t* marker = FindInsensitive(full, L"\\StartupContent\\asset_archive");
        if (!marker) marker = FindInsensitive(full, L"\\StartupContent");
        if (marker)
        {
            size_t rootLen = static_cast<size_t>(marker - full);
            if (rootLen > 0 && rootLen < MAX_PATH)
            {
                wchar_t derived[MAX_PATH];
                wcsncpy_s(derived, full, rootLen);
                derived[rootLen] = 0;
                if (TryLinkAt(derived)) return true;
            }
        }
    }
    wchar_t cwd[MAX_PATH];
    if (GetCurrentDirectoryW(MAX_PATH, cwd))
    {
        wchar_t scratch[MAX_PATH];
        wcscpy_s(scratch, cwd);
        wchar_t* marker = const_cast<wchar_t*>(FindInsensitive(scratch, L"\\StartupContent"));
        if (marker)
        {
            *marker = 0;
            if (TryLinkAt(scratch)) return true;
        }
        if (TryLinkAt(cwd)) return true;
    }
    return TryLinkAt(kLooseRoot);
}

static bool EndsWithI(const wchar_t* value, const wchar_t* ext)
{
    size_t n = wcslen(value), m = wcslen(ext);
    return n >= m && _wcsicmp(value + n - m, ext) == 0;
}

static DWORD AttrOf(const wchar_t* path)
{
    return g_GetFileAttributesW ? g_GetFileAttributesW(path) : INVALID_FILE_ATTRIBUTES;
}

static bool IsFile(const wchar_t* path)
{
    DWORD attr = AttrOf(path);
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

static bool IsDir(const wchar_t* path)
{
    DWORD attr = AttrOf(path);
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

static bool NeedsFromApp(const wchar_t* path)
{
    wchar_t full[MAX_PATH];
    if (!path || !GetFullPathNameW(path, MAX_PATH, full, nullptr) || full[1] != L':') return true;
    wchar_t drive = static_cast<wchar_t>(towlower(full[0]));
    return drive != L'd' && drive != L'c' && drive != L'q' && drive != L's';
}

static bool MapPath(const wchar_t* requested, wchar_t* mapped, size_t cch, bool writing)
{
    wchar_t full[MAX_PATH];
    if (!requested || !GetFullPathNameW(requested, MAX_PATH, full, nullptr)) return false;
    const wchar_t* marker = FindInsensitive(full, L"\\StartupContent");
    if (!marker) return false;
    const wchar_t* rest = marker + wcslen(L"\\StartupContent");
    if (*rest == L'\\' || *rest == L'/') ++rest;
    if (EndsWithI(rest, L".dll") || EndsWithI(rest, L".exe")) return false;
    if (!writing && IsFile(full)) return false;
    if (!g_runtimeRoot[0]) LoadArchiveLink(full);
    const wchar_t* root = g_runtimeRoot[0] ? g_runtimeRoot : kLooseRoot;
    if (wcslen(root) + 16 + wcslen(rest) >= cch) return false;
    wchar_t overlay[MAX_PATH];
    wcscpy_s(overlay, cch, root);
    wcscat_s(overlay, cch, L"\\StartupContent");
    if (*rest)
    {
        wcscat_s(overlay, cch, L"\\");
        wcscat_s(overlay, cch, rest);
    }
    if (_wcsicmp(full, overlay) != 0 && (writing || IsFile(overlay) || IsDir(overlay)))
    {
        wcscpy_s(mapped, cch, overlay);
        return true;
    }
    const wchar_t* archives = FindInsensitive(rest, L"asset_archive");
    if (writing || !archives) return false;
    const wchar_t* aRest = archives + wcslen(L"asset_archive");
    if (*aRest == L'\\' || *aRest == L'/') ++aRest;
    if (!g_linkTarget[0] && !LoadArchiveLink(full)) return false;
    if (wcslen(g_linkTarget) + 1 + wcslen(aRest) >= cch) return false;
    wcscpy_s(mapped, cch, g_linkTarget);
    if (*aRest)
    {
        wcscat_s(mapped, cch, L"\\");
        wcscat_s(mapped, cch, aRest);
    }
    return mapped[0] != 0;
}

static bool IsWrite(DWORD access, DWORD disposition)
{
    if (access & (GENERIC_WRITE | GENERIC_ALL | FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE)) return true;
    return disposition == CREATE_ALWAYS || disposition == CREATE_NEW || disposition == TRUNCATE_EXISTING;
}

static HRESULT WaitAsync(IUnknown* op)
{
    ComPtr<IAsyncInfo> info;
    HRESULT hr = op->QueryInterface(IID_PPV_ARGS(&info));
    if (FAILED(hr)) return hr;
    AsyncStatus status = AsyncStatus::Started;
    while (SUCCEEDED(info->get_Status(&status)) && status == AsyncStatus::Started)
        Sleep(1);
    if (status != AsyncStatus::Completed)
    {
        HRESULT error = E_FAIL;
        info->get_ErrorCode(&error);
        return FAILED(error) ? error : E_FAIL;
    }
    return S_OK;
}

static HRESULT FolderNamed(IStorageFolder* parent, const wchar_t* name, IStorageFolder** out)
{
    ComPtr<__FIAsyncOperation_1_Windows__CStorage__CStorageFolder> op;
    HRESULT hr = parent->GetFolderAsync(HStringReference(name).Get(), &op);
    if (FAILED(hr)) return hr;
    hr = WaitAsync(op.Get());
    if (FAILED(hr)) return hr;
    return op->GetResults(out);
}

static HRESULT FileNamed(IStorageFolder* parent, const wchar_t* name, IStorageFile** out)
{
    ComPtr<__FIAsyncOperation_1_Windows__CStorage__CStorageFile> op;
    HRESULT hr = parent->GetFileAsync(HStringReference(name).Get(), &op);
    if (FAILED(hr)) return hr;
    hr = WaitAsync(op.Get());
    if (FAILED(hr)) return hr;
    return op->GetResults(out);
}

static HANDLE OpenBroker(const wchar_t* path, DWORD access, DWORD share, DWORD flags)
{
    RoInitialize(RO_INIT_MULTITHREADED);
    ComPtr<IKnownFoldersStatics> known;
    HRESULT hr = RoGetActivationFactory(HStringReference(RuntimeClass_Windows_Storage_KnownFolders).Get(), IID_PPV_ARGS(&known));
    if (FAILED(hr)) return INVALID_HANDLE_VALUE;
    ComPtr<IStorageFolder> removable;
    hr = known->get_RemovableDevices(&removable);
    if (FAILED(hr) || !removable) return INVALID_HANDLE_VALUE;
    ComPtr<__FIAsyncOperation_1___FIVectorView_1_Windows__CStorage__CStorageFolder> foldersOp;
    hr = removable->GetFoldersAsyncOverloadDefaultOptionsStartAndCount(&foldersOp);
    if (FAILED(hr)) return INVALID_HANDLE_VALUE;
    hr = WaitAsync(foldersOp.Get());
    if (FAILED(hr)) return INVALID_HANDLE_VALUE;
    ComPtr<__FIVectorView_1_Windows__CStorage__CStorageFolder> drives;
    hr = foldersOp->GetResults(&drives);
    if (FAILED(hr) || !drives) return INVALID_HANDLE_VALUE;
    unsigned count = 0;
    drives->get_Size(&count);
    wchar_t full[MAX_PATH];
    if (!GetFullPathNameW(path, MAX_PATH, full, nullptr)) return INVALID_HANDLE_VALUE;
    ComPtr<IStorageFolder> folder;
    for (unsigned i = 0; i < count; ++i)
    {
        ComPtr<IStorageFolder> drive;
        if (FAILED(drives->GetAt(i, &drive)) || !drive) continue;
        ComPtr<IStorageItem> item;
        if (FAILED(drive.As(&item))) continue;
        HSTRING value = nullptr;
        if (FAILED(item->get_Path(&value)) || !value) continue;
        UINT32 len = 0;
        const wchar_t* drivePath = WindowsGetStringRawBuffer(value, &len);
        bool match = drivePath && len >= 2 && towlower(drivePath[0]) == towlower(full[0]) && full[1] == L':';
        WindowsDeleteString(value);
        if (match) { folder = drive; break; }
    }
    if (!folder) return INVALID_HANDLE_VALUE;
    wchar_t* scan = full;
    if (scan[1] == L':' && (scan[2] == L'\\' || scan[2] == L'/')) scan += 3;
    while (*scan)
    {
        wchar_t* slash = wcspbrk(scan, L"\\/");
        wchar_t part[MAX_PATH];
        if (slash)
        {
            size_t n = static_cast<size_t>(slash - scan);
            if (n >= MAX_PATH) return INVALID_HANDLE_VALUE;
            wcsncpy_s(part, scan, n);
            scan = slash + 1;
            ComPtr<IStorageFolder> next;
            if (FAILED(FolderNamed(folder.Get(), part, &next)) || !next) return INVALID_HANDLE_VALUE;
            folder = next;
        }
        else
        {
            wcscpy_s(part, scan);
            ComPtr<IStorageFile> file;
            if (FAILED(FileNamed(folder.Get(), part, &file)) || !file) return INVALID_HANDLE_VALUE;
            ComPtr<IStorageItemHandleAccess> accessIface;
            if (FAILED(file.As(&accessIface))) return INVALID_HANDLE_VALUE;
            HANDLE_ACCESS_OPTIONS hao = HAO_READ_ATTRIBUTES;
            if (access & (GENERIC_READ | FILE_READ_DATA | FILE_READ_ATTRIBUTES | GENERIC_ALL)) hao = static_cast<HANDLE_ACCESS_OPTIONS>(hao | HAO_READ);
            if (access & (GENERIC_WRITE | FILE_WRITE_DATA | GENERIC_ALL)) hao = static_cast<HANDLE_ACCESS_OPTIONS>(hao | HAO_WRITE);
            HANDLE_SHARING_OPTIONS hso = static_cast<HANDLE_SHARING_OPTIONS>(share & (HSO_SHARE_READ | HSO_SHARE_WRITE | HSO_SHARE_DELETE));
            HANDLE_OPTIONS ho = HO_NONE;
            if (flags & FILE_FLAG_OVERLAPPED) ho = static_cast<HANDLE_OPTIONS>(ho | HO_OVERLAPPED);
            if (flags & FILE_FLAG_RANDOM_ACCESS) ho = static_cast<HANDLE_OPTIONS>(ho | HO_RANDOM_ACCESS);
            if (flags & FILE_FLAG_SEQUENTIAL_SCAN) ho = static_cast<HANDLE_OPTIONS>(ho | HO_SEQUENTIAL_SCAN);
            HANDLE handle = nullptr;
            if (FAILED(accessIface->Create(hao, hso, ho, nullptr, &handle)) || !handle) return INVALID_HANDLE_VALUE;
            return handle;
        }
    }
    return INVALID_HANDLE_VALUE;
}

static HANDLE OpenSsd(const wchar_t* path, DWORD access, DWORD share, DWORD disposition, LPCREATEFILE2_EXTENDED_PARAMETERS ex)
{
    DWORD flags = 0;
    if (ex) flags = ex->dwFileFlags;
    if (g_CreateFile2FromAppW)
    {
        HANDLE handle = g_CreateFile2FromAppW(path, access, share, disposition, ex);
        if (handle != INVALID_HANDLE_VALUE) return handle;
    }
    if (disposition != OPEN_EXISTING && disposition != OPEN_ALWAYS) return INVALID_HANDLE_VALUE;
    return OpenBroker(path, access, share, flags);
}

static void LogOpen(const wchar_t* requested, const wchar_t* mapped, HANDLE handle)
{
    if (!requested) return;
    bool failed = !handle || handle == INVALID_HANDLE_VALUE;
    if (!failed && g_loggedOpens >= 400) return;
    if (!FindInsensitive(requested, L"StartupContent") && !FindInsensitive(requested, L"archive-link")) return;
    ++g_loggedOpens;
    wchar_t line[768];
    swprintf_s(line, L"open err=%u handle=%p %s%s%s",
        GetLastError(), handle, requested,
        mapped && mapped[0] ? L" -> " : L"",
        mapped && mapped[0] ? mapped : L"");
    Log(line);
}

static HANDLE OpenMapped(const wchar_t* mapped, DWORD access, DWORD share, DWORD disposition, LPCREATEFILE2_EXTENDED_PARAMETERS ex)
{
    if (NeedsFromApp(mapped)) return OpenSsd(mapped, access, share, disposition, ex);
    return g_CreateFile2(mapped, access, share, disposition, ex);
}

static HANDLE WINAPI HookCreateFile2(LPCWSTR name, DWORD access, DWORD share, DWORD disposition, LPCREATEFILE2_EXTENDED_PARAMETERS ex)
{
    wchar_t mapped[MAX_PATH]{};
    bool writing = IsWrite(access, disposition);
    if (name && MapPath(name, mapped, MAX_PATH, writing))
    {
        HANDLE handle = OpenMapped(mapped, access, share, disposition, ex);
        LogOpen(name, mapped, handle);
        if (handle != INVALID_HANDLE_VALUE) return handle;
    }
    HANDLE original = g_CreateFile2(name, access, share, disposition, ex);
    if (!mapped[0]) LogOpen(name, nullptr, original);
    return original;
}

extern "C" HANDLE WINAPI HookCreateFileW(LPCWSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES security, DWORD disposition, DWORD flags, HANDLE templateFile)
{
    wchar_t mapped[MAX_PATH]{};
    bool writing = IsWrite(access, disposition);
    if (name && MapPath(name, mapped, MAX_PATH, writing))
    {
        CREATEFILE2_EXTENDED_PARAMETERS ex{};
        ex.dwSize = sizeof(ex);
        ex.dwFileAttributes = flags & 0xFFF;
        ex.dwFileFlags = flags & ~0xFFF;
        HANDLE handle = OpenMapped(mapped, access, share, disposition, &ex);
        LogOpen(name, mapped, handle);
        if (handle != INVALID_HANDLE_VALUE) return handle;
    }
    HANDLE original = g_CreateFileW(name, access, share, security, disposition, flags, templateFile);
    if (!mapped[0]) LogOpen(name, nullptr, original);
    return original;
}

extern "C" HANDLE WINAPI HookCreateFileA(LPCSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES security, DWORD disposition, DWORD flags, HANDLE templateFile)
{
    wchar_t wide[MAX_PATH];
    if (name && MultiByteToWideChar(CP_ACP, 0, name, -1, wide, MAX_PATH) > 0)
        return HookCreateFileW(wide, access, share, security, disposition, flags, templateFile);
    return g_CreateFileA(name, access, share, security, disposition, flags, templateFile);
}

extern "C" BOOL WINAPI HookGetFileAttributesExW(LPCWSTR name, GET_FILEEX_INFO_LEVELS info, LPVOID data)
{
    wchar_t mapped[MAX_PATH];
    if (name && MapPath(name, mapped, MAX_PATH, false))
    {
        if (!NeedsFromApp(mapped)) return g_GetFileAttributesExW(mapped, info, data);
        if (g_GetFileAttributesExFromAppW && g_GetFileAttributesExFromAppW(mapped, info, data)) return TRUE;
        WIN32_FILE_ATTRIBUTE_DATA local{};
        HANDLE handle = OpenSsd(mapped, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING, nullptr);
        if (handle == INVALID_HANDLE_VALUE) { SetLastError(ERROR_FILE_NOT_FOUND); return FALSE; }
        BY_HANDLE_FILE_INFORMATION fi{};
        BOOL ok = GetFileInformationByHandle(handle, &fi);
        CloseHandle(handle);
        if (!ok) return FALSE;
        if (info == GetFileExInfoStandard && data)
        {
            auto* out = static_cast<WIN32_FILE_ATTRIBUTE_DATA*>(data);
            out->dwFileAttributes = fi.dwFileAttributes;
            out->ftCreationTime = fi.ftCreationTime;
            out->ftLastAccessTime = fi.ftLastAccessTime;
            out->ftLastWriteTime = fi.ftLastWriteTime;
            out->nFileSizeHigh = fi.nFileSizeHigh;
            out->nFileSizeLow = fi.nFileSizeLow;
        }
        return TRUE;
    }
    return g_GetFileAttributesExW(name, info, data);
}

extern "C" DWORD WINAPI HookGetFileAttributesW(LPCWSTR name)
{
    WIN32_FILE_ATTRIBUTE_DATA data{};
    if (HookGetFileAttributesExW(name, GetFileExInfoStandard, &data)) return data.dwFileAttributes;
    return INVALID_FILE_ATTRIBUTES;
}

extern "C" DWORD WINAPI HookGetFileAttributesA(LPCSTR name)
{
    wchar_t wide[MAX_PATH];
    if (name && MultiByteToWideChar(CP_ACP, 0, name, -1, wide, MAX_PATH) > 0)
        return HookGetFileAttributesW(wide);
    return g_GetFileAttributesA(name);
}

static FARPROC WINAPI HookGetProcAddress(HMODULE module, LPCSTR name)
{
    if (name && !IS_INTRESOURCE(name))
    {
        void* hook = HookNamed(name);
        if (hook) return reinterpret_cast<FARPROC>(hook);
    }
    return g_GetProcAddress(module, name);
}

static void* HookNamed(const char* name)
{
    if (!name) return nullptr;
    if (_stricmp(name, "CreateFile2") == 0) return reinterpret_cast<void*>(&HookCreateFile2);
    if (_stricmp(name, "CreateFileW") == 0) return reinterpret_cast<void*>(&HookCreateFileW);
    if (_stricmp(name, "CreateFileA") == 0) return reinterpret_cast<void*>(&HookCreateFileA);
    if (_stricmp(name, "GetFileAttributesExW") == 0) return reinterpret_cast<void*>(&HookGetFileAttributesExW);
    if (_stricmp(name, "GetFileAttributesW") == 0) return reinterpret_cast<void*>(&HookGetFileAttributesW);
    if (_stricmp(name, "GetFileAttributesA") == 0) return reinterpret_cast<void*>(&HookGetFileAttributesA);
    if (_stricmp(name, "GetProcAddress") == 0) return reinterpret_cast<void*>(&HookGetProcAddress);
    return nullptr;
}

static void PatchThunk(void** slot, void* hook)
{
    DWORD old = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) return;
    *slot = hook;
    VirtualProtect(slot, sizeof(void*), old, &old);
}

static void PatchImports(HMODULE module)
{
    if (!module) return;
    auto* base = reinterpret_cast<uint8_t*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return;
    auto patchDir = [&](DWORD rva)
    {
        if (!rva) return;
        for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + rva); imp->Name; ++imp)
        {
            auto* names = reinterpret_cast<IMAGE_THUNK_DATA*>(base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
            auto* iat = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->FirstThunk);
            for (; names->u1.AddressOfData; ++names, ++iat)
            {
                if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
                auto* ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
                void* hook = HookNamed(reinterpret_cast<const char*>(ibn->Name));
                if (hook) PatchThunk(reinterpret_cast<void**>(&iat->u1.Function), hook);
            }
        }
    };
    patchDir(nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
    DWORD delayRva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT].VirtualAddress;
    if (delayRva)
    {
        for (auto* delay = reinterpret_cast<IMAGE_DELAYLOAD_DESCRIPTOR*>(base + delayRva); delay->DllNameRVA; ++delay)
        {
            auto* names = reinterpret_cast<IMAGE_THUNK_DATA*>(base + delay->ImportNameTableRVA);
            auto* iat = reinterpret_cast<IMAGE_THUNK_DATA*>(base + delay->ImportAddressTableRVA);
            for (; names->u1.AddressOfData; ++names, ++iat)
            {
                if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
                auto* ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
                void* hook = HookNamed(reinterpret_cast<const char*>(ibn->Name));
                if (hook) PatchThunk(reinterpret_cast<void**>(&iat->u1.Function), hook);
            }
        }
    }
}

static HMODULE WINAPI HookLoadLibraryExW(LPCWSTR name, HANDLE file, DWORD flags)
{
    HMODULE module = g_LoadLibraryExW(name, file, flags);
    if (module) PatchImports(module);
    return module;
}

static HMODULE WINAPI HookLoadPackagedLibrary(LPCWSTR name, DWORD reserved)
{
    HMODULE module = g_LoadPackagedLibrary ? g_LoadPackagedLibrary(name, reserved) : nullptr;
    if (module) PatchImports(module);
    return module;
}

static void PatchLoaderHooks(HMODULE module)
{
    if (!module) return;
    auto* base = reinterpret_cast<uint8_t*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!rva) return;
    for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + rva); imp->Name; ++imp)
    {
        auto* names = reinterpret_cast<IMAGE_THUNK_DATA*>(base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        auto* iat = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++iat)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            auto* ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
            const char* name = reinterpret_cast<const char*>(ibn->Name);
            if (_stricmp(name, "LoadLibraryExW") == 0)
                PatchThunk(reinterpret_cast<void**>(&iat->u1.Function), reinterpret_cast<void*>(&HookLoadLibraryExW));
            else if (_stricmp(name, "LoadPackagedLibrary") == 0 && g_LoadPackagedLibrary)
                PatchThunk(reinterpret_cast<void**>(&iat->u1.Function), reinterpret_cast<void*>(&HookLoadPackagedLibrary));
        }
    }
}

static void PatchLoaded()
{
    PatchImports(GetModuleHandleW(nullptr));
    PatchLoaderHooks(GetModuleHandleW(nullptr));
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    using EnumFn = BOOL(WINAPI*)(HANDLE, HMODULE*, DWORD, LPDWORD);
    auto enumerate = reinterpret_cast<EnumFn>(GetProcAddress(kernel, "K32EnumProcessModules"));
    if (!enumerate) return;
    HMODULE modules[512];
    DWORD needed = 0;
    if (!enumerate(GetCurrentProcess(), modules, sizeof(modules), &needed)) return;
    unsigned count = needed / sizeof(HMODULE);
    if (count > 512) count = 512;
    for (unsigned i = 0; i < count; ++i)
    {
        if (modules[i] == g_self) continue;
        PatchImports(modules[i]);
        PatchLoaderHooks(modules[i]);
    }
}

static bool LoadCore()
{
    if (g_core) return true;
    auto packaged = reinterpret_cast<LoadPackagedLibraryFn>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadPackagedLibrary"));
    if (packaged) g_core = packaged(L"StartupContent\\SpShellCore.dll", 0);
    if (g_core) return true;
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_self, path, MAX_PATH)) return false;
    wchar_t* slash = wcsrchr(path, L'\\');
    if (!slash) return false;
    slash[1] = 0;
    wcscat_s(path, L"SpShellCore.dll");
    g_core = g_LoadLibraryExW ? g_LoadLibraryExW(path, nullptr, 0) : LoadLibraryExW(path, nullptr, 0);
    return g_core != nullptr;
}

static void Ensure()
{
    AcquireSRWLockExclusive(&g_lock);
    if (!g_hooked)
    {
        LoadCore();
        PatchLoaded();
        g_hooked = true;
        Log(L"storage bridge ready; archives stay on the SSD");
    }
    ReleaseSRWLockExclusive(&g_lock);
}

extern "C" HRESULT WINAPI InitializeStorageAdapter(void* a, void* b, void* c, void* d)
{
    Ensure();
    using Fn = HRESULT(WINAPI*)(void*, void*, void*, void*);
    auto fn = reinterpret_cast<Fn>(GetProcAddress(g_core, "InitializeStorageAdapter"));
    return fn ? fn(a, b, c, d) : E_FAIL;
}

extern "C" HRESULT WINAPI SHGetFolderPathW(HWND hwnd, int csidl, HANDLE token, DWORD flags, LPWSTR path)
{
    Ensure();
    using Fn = HRESULT(WINAPI*)(HWND, int, HANDLE, DWORD, LPWSTR);
    auto fn = reinterpret_cast<Fn>(GetProcAddress(g_core, "SHGetFolderPathW"));
    return fn ? fn(hwnd, csidl, token, flags, path) : E_FAIL;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    g_self = module;
    DisableThreadLibraryCalls(module);
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    g_GetProcAddress = reinterpret_cast<GetProcAddressFn>(GetProcAddress(kernel, "GetProcAddress"));
    g_CreateFile2 = reinterpret_cast<CreateFile2Fn>(g_GetProcAddress(kernel, "CreateFile2"));
    g_CreateFileW = reinterpret_cast<CreateFileWFn>(g_GetProcAddress(kernel, "CreateFileW"));
    g_CreateFileA = reinterpret_cast<CreateFileAFn>(g_GetProcAddress(kernel, "CreateFileA"));
    g_GetFileAttributesExW = reinterpret_cast<GetFileAttributesExWFn>(g_GetProcAddress(kernel, "GetFileAttributesExW"));
    g_GetFileAttributesW = reinterpret_cast<GetFileAttributesWFn>(g_GetProcAddress(kernel, "GetFileAttributesW"));
    g_GetFileAttributesA = reinterpret_cast<GetFileAttributesAFn>(g_GetProcAddress(kernel, "GetFileAttributesA"));
    g_LoadLibraryExW = reinterpret_cast<LoadLibraryExWFn>(g_GetProcAddress(kernel, "LoadLibraryExW"));
    g_LoadPackagedLibrary = reinterpret_cast<LoadPackagedLibraryFn>(g_GetProcAddress(kernel, "LoadPackagedLibrary"));
    g_CreateFile2FromAppW = reinterpret_cast<CreateFile2FromAppWFn>(g_GetProcAddress(kernel, "CreateFile2FromAppW"));
    g_GetFileAttributesExFromAppW = reinterpret_cast<GetFileAttributesExFromAppWFn>(g_GetProcAddress(kernel, "GetFileAttributesExFromAppW"));
    PatchImports(GetModuleHandleW(nullptr));
    PatchLoaderHooks(GetModuleHandleW(nullptr));
    Log(L"storage bridge attached before asset check; archives stay on the SSD");
    return TRUE;
}
