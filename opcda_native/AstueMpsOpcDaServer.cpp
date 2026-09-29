#ifndef WINVER
#define WINVER 0x0601
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#ifndef NTDDI_VERSION
#define NTDDI_VERSION 0x06010100 // Windows 7 SP1
#endif
#define _ATL_FREE_THREADED
#define NOMINMAX

#include <windows.h>
#include <atlbase.h>
#include <atlcom.h>
#include <atlctl.h>
#include <comcat.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <wincrypt.h>
#include <opcda.h>
#include <opccomn.h>
#include <opcerror.h>

extern "C" {
#include <opcda_i.c>
#include <opccomn_i.c>
}

#include <algorithm>
#include <atomic>
#include <cwctype>
#include <map>
#include <mutex>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "uuid.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")

class AstueOpcAtlModule : public CAtlExeModuleT<AstueOpcAtlModule> {};
AstueOpcAtlModule _AtlModule;

namespace
{
    // Stable identifiers for this server. Do not change between releases.
    const CLSID CLSID_AstueMpsOpcDa =
        { 0x7b0e174e, 0x31f0, 0x4a2a, { 0x9d, 0x8e, 0x3c, 0x3d, 0x95, 0x2b, 0xd9, 0x01 } };
    const GUID APPID_AstueMpsOpcDa =
        { 0xa4c8b5e6, 0xe3d5, 0x4b2d, { 0x88, 0x21, 0x67, 0xf5, 0xaf, 0x11, 0xe9, 0xa8 } };

    const wchar_t* kProgId = L"Astue.MpsOpcDa.1";
    const wchar_t* kVersionIndependentProgId = L"Astue.MpsOpcDa";
    const wchar_t* kFriendlyName = L"ASTUE MPS Replacement OPC DA Server";
    const wchar_t* kStopEventGlobal = L"Global\\ASTUE_MPS_OPC_DA_STOP";
    const wchar_t* kStopEventLocal = L"ASTUE_MPS_OPC_DA_STOP";
    const wchar_t* kSingleInstanceMutex = L"Global\\ASTUE_MPS_OPC_DA_SINGLE_INSTANCE";

    std::atomic<DWORD> g_nextGroupHandle(1);
    std::atomic<DWORD> g_nextItemHandle(1);
    std::atomic<LONG> g_serverObjectCount(0);
    std::atomic<LONG> g_groupObjectCount(0);
    std::atomic<LONG> g_pendingActivationCount(0);
    std::atomic<LONG> g_serverLockCount(0);
    std::atomic<ULONGLONG> g_lastMainLaunchAttempt(0);
    std::atomic<bool> g_hadClient(false);
    std::atomic<ULONGLONG> g_lastClientGoneTick(0);
    FILETIME g_startTime = {};

    // Windows 7 SP1 compatibility:
    // GetSystemTimePreciseAsFileTime exists only on Windows 8 / Server 2012+.
    // Resolve it dynamically so the executable has no static loader dependency on it.
    // On Windows 7 fall back to GetSystemTimeAsFileTime (available since Windows 2000).
    using GetSystemTimePreciseAsFileTimeFn = VOID (WINAPI*)(LPFILETIME);

    void GetUtcFileTimeCompat(LPFILETIME value)
    {
        if (!value) return;
        static GetSystemTimePreciseAsFileTimeFn precise = []() -> GetSystemTimePreciseAsFileTimeFn
        {
            HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
            if (!kernel32) return nullptr;
            return reinterpret_cast<GetSystemTimePreciseAsFileTimeFn>(
                GetProcAddress(kernel32, "GetSystemTimePreciseAsFileTime"));
        }();

        if (precise) precise(value);
        else ::GetSystemTimeAsFileTime(value);
    }

    bool ReadWholeFileBytes(const std::wstring& path, std::string& out)
    {
        out.clear();
        HANDLE h = CreateFileW(path.c_str(), GENERIC_READ,
                               FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;

        LARGE_INTEGER size = {};
        if (!GetFileSizeEx(h, &size) || size.QuadPart < 0 || size.QuadPart > 128LL * 1024LL * 1024LL)
        {
            CloseHandle(h);
            return false;
        }

        try { out.resize(static_cast<size_t>(size.QuadPart)); }
        catch (...) { CloseHandle(h); return false; }

        size_t done = 0;
        while (done < out.size())
        {
            const DWORD chunk = static_cast<DWORD>(std::min<size_t>(out.size() - done, 1024 * 1024));
            DWORD got = 0;
            if (!ReadFile(h, &out[done], chunk, &got, nullptr))
            {
                CloseHandle(h);
                out.clear();
                return false;
            }
            if (got == 0) break;
            done += got;
        }
        CloseHandle(h);
        out.resize(done);
        return true;
    }

    bool WriteWholeFileBytes(const std::wstring& path, const std::string& text)
    {
        HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                               nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;

        size_t done = 0;
        while (done < text.size())
        {
            const DWORD chunk = static_cast<DWORD>(std::min<size_t>(text.size() - done, 1024 * 1024));
            DWORD written = 0;
            if (!WriteFile(h, text.data() + done, chunk, &written, nullptr) || written == 0)
            {
                CloseHandle(h);
                return false;
            }
            done += written;
        }
        CloseHandle(h);
        return true;
    }

    std::wstring GuidToString(REFGUID id)
    {
        wchar_t buf[64] = {};
        StringFromGUID2(id, buf, static_cast<int>(_countof(buf)));
        return buf;
    }

    LPWSTR CoTaskMemString(const std::wstring& s)
    {
        const auto bytes = (s.size() + 1) * sizeof(wchar_t);
        auto p = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
        if (!p) return nullptr;
        memcpy(p, s.c_str(), bytes);
        return p;
    }

    std::wstring Utf8ToWide(const std::string& s)
    {
        if (s.empty()) return std::wstring();
        int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), static_cast<int>(s.size()), nullptr, 0);
        if (n <= 0) return std::wstring();
        std::wstring out(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), static_cast<int>(s.size()), &out[0], n);
        return out;
    }

    std::string DecodeBase64(const std::string& s)
    {
        if (s.empty()) return std::string();
        DWORD bytes = 0;
        if (!CryptStringToBinaryA(s.c_str(), static_cast<DWORD>(s.size()), CRYPT_STRING_BASE64, nullptr, &bytes, nullptr, nullptr))
            return std::string();
        std::string out(bytes, '\0');
        if (!CryptStringToBinaryA(s.c_str(), static_cast<DWORD>(s.size()), CRYPT_STRING_BASE64,
                                  reinterpret_cast<BYTE*>(&out[0]), &bytes, nullptr, nullptr))
            return std::string();
        out.resize(bytes);
        return out;
    }

    std::wstring DecodeBase64Utf8(const std::string& s)
    {
        return Utf8ToWide(DecodeBase64(s));
    }

    std::wstring GetProgramDataDirectory()
    {
        wchar_t buf[MAX_PATH] = {};
        DWORD n = GetEnvironmentVariableW(L"ProgramData", buf, static_cast<DWORD>(_countof(buf)));
        if (n > 0 && n < _countof(buf)) return std::wstring(buf) + L"\\ASTUE_MPS";
        return L"C:\\ProgramData\\ASTUE_MPS";
    }

    std::wstring GetSnapshotPath()
    {
        return GetProgramDataDirectory() + L"\\opc_snapshot.tsv";
    }


    std::wstring ModulePath();

    std::wstring RuntimePath(const wchar_t* name)
    {
        return GetProgramDataDirectory() + L"\\" + name;
    }

    bool FileExists(const std::wstring& path)
    {
        const DWORD a = GetFileAttributesW(path.c_str());
        return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }


    bool FileIsFresh(const std::wstring& path, DWORD maxAgeMs)
    {
        WIN32_FILE_ATTRIBUTE_DATA fad = {};
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) return false;
        FILETIME now = {};
        GetUtcFileTimeCompat(&now);
        ULARGE_INTEGER n = {}, w = {};
        n.LowPart = now.dwLowDateTime; n.HighPart = now.dwHighDateTime;
        w.LowPart = fad.ftLastWriteTime.dwLowDateTime; w.HighPart = fad.ftLastWriteTime.dwHighDateTime;
        const ULONGLONG age100ns = n.QuadPart >= w.QuadPart ? n.QuadPart - w.QuadPart : 0;
        return age100ns <= static_cast<ULONGLONG>(maxAgeMs) * 10000ULL;
    }

    void EnsureRuntimeDirectory()
    {
        CreateDirectoryW(GetProgramDataDirectory().c_str(), nullptr);
    }

    bool ReadPidFromFile(const std::wstring& path, DWORD& pid)
    {
        pid = 0;
        std::string text;
        if (!ReadWholeFileBytes(path, text)) return false;
        const auto eol = text.find_first_of("\r\n");
        if (eol != std::string::npos) text.resize(eol);
        const auto tab = text.find('\t');
        if (tab != std::string::npos) text.resize(tab);
        try
        {
            const auto value = std::stoul(text);
            if (value == 0 || value > MAXDWORD) return false;
            pid = static_cast<DWORD>(value);
            return true;
        }
        catch (...) { return false; }
    }

    bool ProcessAlive(DWORD pid)
    {
        if (!pid) return false;
        HANDLE h = OpenProcess(SYNCHRONIZE, FALSE, pid);
        if (!h) return false;
        const DWORD wait = WaitForSingleObject(h, 0);
        CloseHandle(h);
        return wait == WAIT_TIMEOUT;
    }

    bool IsConfigurationModeActive()
    {
        const auto path = RuntimePath(L"configuration_mode.lock");
        DWORD pid = 0;
        if (!ReadPidFromFile(path, pid))
        {
            if (FileExists(path)) DeleteFileW(path.c_str());
            return false;
        }
        if (ProcessAlive(pid)) return true;
        if (FileIsFresh(path, 5000)) return true;
        DeleteFileW(path.c_str());
        return false;
    }

    bool IsOpcAutoStartBlocked()
    {
        return FileExists(RuntimePath(L"opc_autostart.block"));
    }

    bool IsManualKeepAliveRequested()
    {
        return FileExists(RuntimePath(L"opc_manual.keepalive"));
    }

    bool IsMainApplicationRunning()
    {
        const auto path = RuntimePath(L"main_state.txt");
        DWORD pid = 0;
        if (!ReadPidFromFile(path, pid)) return false;
        if (ProcessAlive(pid)) return true;
        return FileIsFresh(path, 5000);
    }

    void WriteAsciiFile(const std::wstring& path, const std::string& text)
    {
        EnsureRuntimeDirectory();
        WriteWholeFileBytes(path, text);
    }

    LONG ActiveOpcObjectCount()
    {
        return g_serverObjectCount.load() + g_groupObjectCount.load();
    }

    LONG ActiveOpcDemandCount()
    {
        return ActiveOpcObjectCount() + g_pendingActivationCount.load();
    }

    void TouchOpcDemand()
    {
        FILETIME now = {};
        GetUtcFileTimeCompat(&now);
        ULARGE_INTEGER u = {};
        u.LowPart = now.dwLowDateTime;
        u.HighPart = now.dwHighDateTime;
        std::ostringstream ss;
        ss << GetCurrentProcessId() << '\t' << g_serverObjectCount.load() << '\t'
           << g_groupObjectCount.load() << '\t' << g_pendingActivationCount.load()
           << '\t' << u.QuadPart << "\r\n";
        WriteAsciiFile(RuntimePath(L"opc_demand.touch"), ss.str());
    }

    void ClearOpcDemand()
    {
        DeleteFileW(RuntimePath(L"opc_demand.touch").c_str());
    }

    void WriteOpcState(bool manualMode)
    {
        FILETIME now = {};
        GetUtcFileTimeCompat(&now);
        ULARGE_INTEGER u = {};
        u.LowPart = now.dwLowDateTime;
        u.HighPart = now.dwHighDateTime;
        std::ostringstream ss;
        ss << "pid=" << GetCurrentProcessId() << "\r\n";
        ss << "mode=" << (manualMode ? "manual" : "demand") << "\r\n";
        ss << "clients=" << g_serverObjectCount.load() << "\r\n";
        ss << "groups=" << g_groupObjectCount.load() << "\r\n";
        ss << "objects=" << ActiveOpcObjectCount() << "\r\n";
        ss << "pending=" << g_pendingActivationCount.load() << "\r\n";
        ss << "locks=" << g_serverLockCount.load() << "\r\n";
        ss << "heartbeat_filetime=" << u.QuadPart << "\r\n";
        WriteAsciiFile(RuntimePath(L"opc_server_state.txt"), ss.str());
    }

    void ClearOpcState()
    {
        DeleteFileW(RuntimePath(L"opc_server_state.txt").c_str());
    }

    std::wstring ModuleDirectory()
    {
        auto path = ModulePath();
        const auto pos = path.find_last_of(L"\\/");
        return pos == std::wstring::npos ? std::wstring() : path.substr(0, pos);
    }

    bool LaunchMainRuntimeIfNeeded()
    {
        if (IsConfigurationModeActive()) return false;
        if (IsOpcAutoStartBlocked()) return false;
        if (IsMainApplicationRunning()) return true;

        // Avoid spawning several GUI processes while the first one is still loading
        // its project and has not written main_state.txt yet.
        const ULONGLONG now = GetTickCount64();
        const ULONGLONG previous = g_lastMainLaunchAttempt.load();
        if (previous != 0 && now - previous < 10000ULL) return true;
        g_lastMainLaunchAttempt = now;

        const auto dir = ModuleDirectory();
        const auto exe = dir + L"\\AstueMpsReplacement.exe";
        if (!FileExists(exe))
        {
            WriteAsciiFile(RuntimePath(L"opc_main_launch_error.txt"), "AstueMpsReplacement.exe not found\r\n");
            return false;
        }

        std::wstring command = L"\"" + exe + L"\" --runtime --opc-demand";
        STARTUPINFOW si = {};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi = {};
        std::vector<wchar_t> cmd(command.begin(), command.end());
        cmd.push_back(L'\0');
        const BOOL ok = CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE,
                                       CREATE_NEW_PROCESS_GROUP, nullptr,
                                       dir.empty() ? nullptr : dir.c_str(), &si, &pi);
        if (!ok)
        {
            const DWORD err = GetLastError();
            std::ostringstream ss;
            ss << "CreateProcessW failed, GetLastError=" << err << "\r\n";
            WriteAsciiFile(RuntimePath(L"opc_main_launch_error.txt"), ss.str());
            g_lastMainLaunchAttempt = 0;
            return false;
        }
        DeleteFileW(RuntimePath(L"opc_main_launch_error.txt").c_str());
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }
    bool SnapshotIsFresh(DWORD maxAgeMs)
    {
        return FileIsFresh(GetSnapshotPath(), maxAgeMs);
    }

    void WaitForRuntimeSnapshot(DWORD maxWaitMs)
    {
        const ULONGLONG start = GetTickCount64();
        while (GetTickCount64() - start < maxWaitMs)
        {
            if (IsConfigurationModeActive()) return;
            if (SnapshotIsFresh(5000)) return;
            Sleep(250);
        }
    }

    ULONGLONG FileTimeToUll(const FILETIME& ft)
    {
        ULARGE_INTEGER u = {};
        u.LowPart = ft.dwLowDateTime;
        u.HighPart = ft.dwHighDateTime;
        return u.QuadPart;
    }

    FILETIME UllToFileTime(ULONGLONG v)
    {
        ULARGE_INTEGER u = {};
        u.QuadPart = v;
        FILETIME ft = {};
        ft.dwLowDateTime = u.LowPart;
        ft.dwHighDateTime = u.HighPart;
        return ft;
    }

    bool StartsWithI(const std::wstring& value, const std::wstring& prefix)
    {
        if (prefix.size() > value.size()) return false;
        return _wcsnicmp(value.c_str(), prefix.c_str(), prefix.size()) == 0;
    }

    bool WildMatchImpl(const wchar_t* text, const wchar_t* pat)
    {
        while (*pat)
        {
            if (*pat == L'*')
            {
                ++pat;
                if (!*pat) return true;
                while (*text)
                {
                    if (WildMatchImpl(text, pat)) return true;
                    ++text;
                }
                return false;
            }
            if (!*text) return false;
            if (*pat != L'?' && towlower(*pat) != towlower(*text)) return false;
            ++pat;
            ++text;
        }
        return *text == 0;
    }

    bool WildMatch(const std::wstring& text, LPCWSTR pattern)
    {
        if (!pattern || !*pattern) return true;
        return WildMatchImpl(text.c_str(), pattern);
    }

    VARTYPE TypeFromText(const std::string& s)
    {
        if (_stricmp(s.c_str(), "bool") == 0) return VT_BOOL;
        if (_stricmp(s.c_str(), "int32") == 0) return VT_I4;
        if (_stricmp(s.c_str(), "uint32") == 0) return VT_UI4;
        if (_stricmp(s.c_str(), "float") == 0) return VT_R4;
        if (_stricmp(s.c_str(), "double") == 0) return VT_R8;
        return VT_BSTR;
    }

    bool IsSupportedRequestedType(VARTYPE vt)
    {
        switch (vt)
        {
        case VT_EMPTY:
        case VT_BOOL:
        case VT_I1:
        case VT_UI1:
        case VT_I2:
        case VT_UI2:
        case VT_I4:
        case VT_UI4:
        case VT_R4:
        case VT_R8:
        case VT_BSTR:
            return true;
        default:
            return false;
        }
    }

    struct SnapshotItem
    {
        std::wstring id;
        VARTYPE vt = VT_EMPTY;
        WORD quality = 0;
        FILETIME timestamp = {};
        std::wstring value;
        bool hasValue = false;
    };

    class SnapshotStore
    {
    public:
        static SnapshotStore& Instance()
        {
            static SnapshotStore store;
            return store;
        }

        bool Find(const std::wstring& id, SnapshotItem& item)
        {
            EnsureLoaded();
            std::lock_guard<std::mutex> lock(_mutex);
            auto it = _items.find(id);
            if (it == _items.end()) return false;
            item = it->second;
            ApplyStaleQuality(item);
            return true;
        }

        std::vector<SnapshotItem> All()
        {
            EnsureLoaded();
            std::lock_guard<std::mutex> lock(_mutex);
            std::vector<SnapshotItem> out;
            out.reserve(_items.size());
            for (auto& kv : _items)
            {
                auto item = kv.second;
                ApplyStaleQuality(item);
                out.push_back(item);
            }
            return out;
        }

        FILETIME LastUpdateFileTime()
        {
            EnsureLoaded();
            std::lock_guard<std::mutex> lock(_mutex);
            return UllToFileTime(_generatedFileTime);
        }

        bool IsStale()
        {
            EnsureLoaded();
            std::lock_guard<std::mutex> lock(_mutex);
            return IsStaleLocked();
        }

    private:
        std::mutex _mutex;
        std::map<std::wstring, SnapshotItem, std::less<>> _items;
        ULONGLONG _sourceWriteTime = 0;
        ULONGLONG _generatedFileTime = 0;
        ULONGLONG _lastCheckTick = 0;

        SnapshotStore() = default;

        bool IsStaleLocked() const
        {
            if (_generatedFileTime == 0) return true;
            FILETIME nowFt = {};
            GetUtcFileTimeCompat(&nowFt);
            const auto now = FileTimeToUll(nowFt);
            const ULONGLONG fifteenSeconds = 15ULL * 10000000ULL;
            return now > _generatedFileTime && (now - _generatedFileTime) > fifteenSeconds;
        }

        void ApplyStaleQuality(SnapshotItem& item) const
        {
            if (IsStaleLocked()) item.quality = 0x18; // BAD / COMM FAILURE
        }

        void EnsureLoaded()
        {
            const ULONGLONG tick = GetTickCount64();
            {
                std::lock_guard<std::mutex> lock(_mutex);
                if (tick - _lastCheckTick < 250) return;
                _lastCheckTick = tick;
            }

            const auto path = GetSnapshotPath();
            WIN32_FILE_ATTRIBUTE_DATA fad = {};
            if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) return;
            const auto writeTime = FileTimeToUll(fad.ftLastWriteTime);

            {
                std::lock_guard<std::mutex> lock(_mutex);
                if (writeTime == _sourceWriteTime) return;
            }

            std::string snapshotText;
            if (!ReadWholeFileBytes(path, snapshotText)) return;
            std::istringstream f(snapshotText);

            std::string header;
            if (!std::getline(f, header)) return;
            if (!header.empty() && header.back() == '\r') header.pop_back();

            std::vector<std::string> h;
            SplitTabs(header, h);
            if (h.size() < 3 || h[0] != "ASTUE_OPC_SNAPSHOT_V1") return;

            ULONGLONG generated = 0;
            try { generated = std::stoull(h[1]); }
            catch (...) { return; }

            std::map<std::wstring, SnapshotItem, std::less<>> next;
            std::string line;
            while (std::getline(f, line))
            {
                if (!line.empty() && line.back() == '\r') line.pop_back();
                std::vector<std::string> p;
                SplitTabs(line, p);
                if (p.size() < 5) continue;

                SnapshotItem item;
                item.id = DecodeBase64Utf8(p[0]);
                if (item.id.empty()) continue;
                item.vt = TypeFromText(p[1]);
                try { item.quality = static_cast<WORD>(std::stoul(p[2])); }
                catch (...) { item.quality = 0; }
                try { item.timestamp = UllToFileTime(std::stoull(p[3])); }
                catch (...) { item.timestamp = {}; }
                item.value = DecodeBase64Utf8(p[4]);
                item.hasValue = !p[4].empty();
                next[item.id] = item;
            }

            std::lock_guard<std::mutex> lock(_mutex);
            _items.swap(next);
            _sourceWriteTime = writeTime;
            _generatedFileTime = generated;
        }

        static void SplitTabs(const std::string& s, std::vector<std::string>& out)
        {
            size_t start = 0;
            for (;;)
            {
                size_t pos = s.find('\t', start);
                if (pos == std::string::npos)
                {
                    out.emplace_back(s.substr(start));
                    return;
                }
                out.emplace_back(s.substr(start, pos - start));
                start = pos + 1;
            }
        }
    };

    HRESULT SnapshotToVariant(const SnapshotItem& item, VARTYPE requested, VARIANT* out)
    {
        if (!out) return E_POINTER;
        VariantInit(out);
        if (!item.hasValue) return S_OK;

        VARIANT canonical;
        VariantInit(&canonical);
        canonical.vt = item.vt;
        try
        {
            switch (item.vt)
            {
            case VT_BOOL:
                canonical.boolVal = (item.value == L"1" || _wcsicmp(item.value.c_str(), L"true") == 0) ? VARIANT_TRUE : VARIANT_FALSE;
                break;
            case VT_I4:
                canonical.lVal = std::stol(item.value);
                break;
            case VT_UI4:
                canonical.ulVal = std::stoul(item.value);
                break;
            case VT_R4:
                canonical.fltVal = std::stof(item.value);
                break;
            case VT_R8:
                canonical.dblVal = std::stod(item.value);
                break;
            case VT_BSTR:
            default:
                canonical.vt = VT_BSTR;
                canonical.bstrVal = SysAllocString(item.value.c_str());
                if (!canonical.bstrVal) return E_OUTOFMEMORY;
                break;
            }
        }
        catch (...)
        {
            VariantClear(&canonical);
            return OPC_E_BADTYPE;
        }

        HRESULT hr;
        if (requested == VT_EMPTY || requested == canonical.vt)
            hr = VariantCopy(out, &canonical);
        else
            hr = VariantChangeType(out, &canonical, 0, requested);
        VariantClear(&canonical);
        return hr;
    }

    class ATL_NO_VTABLE StringEnum :
        public CComObjectRootEx<CComMultiThreadModel>,
        public IEnumString
    {
    public:
        BEGIN_COM_MAP(StringEnum)
            COM_INTERFACE_ENTRY(IEnumString)
        END_COM_MAP()

        void Init(const std::vector<std::wstring>& values, ULONG index = 0)
        {
            _values = values;
            _index = index;
        }

        STDMETHOD(Next)(ULONG celt, LPOLESTR* rgelt, ULONG* pceltFetched) override
        {
            if (!rgelt) return E_POINTER;
            if (!pceltFetched && celt != 1) return E_POINTER;
            if (pceltFetched) *pceltFetched = 0;

            ULONG fetched = 0;
            while (fetched < celt && _index < _values.size())
            {
                rgelt[fetched] = CoTaskMemString(_values[_index]);
                if (!rgelt[fetched])
                {
                    for (ULONG i = 0; i < fetched; ++i) CoTaskMemFree(rgelt[i]);
                    return E_OUTOFMEMORY;
                }
                ++fetched;
                ++_index;
            }
            if (pceltFetched) *pceltFetched = fetched;
            return fetched == celt ? S_OK : S_FALSE;
        }

        STDMETHOD(Skip)(ULONG celt) override
        {
            const auto remaining = static_cast<ULONG>(_values.size() - std::min<size_t>(_index, _values.size()));
            const auto skipped = std::min(celt, remaining);
            _index += skipped;
            return skipped == celt ? S_OK : S_FALSE;
        }

        STDMETHOD(Reset)() override
        {
            _index = 0;
            return S_OK;
        }

        STDMETHOD(Clone)(IEnumString** ppEnum) override
        {
            if (!ppEnum) return E_POINTER;
            *ppEnum = nullptr;
            CComObject<StringEnum>* obj = nullptr;
            HRESULT hr = CComObject<StringEnum>::CreateInstance(&obj);
            if (FAILED(hr)) return hr;
            obj->AddRef();
            obj->Init(_values, _index);
            hr = obj->QueryInterface(IID_IEnumString, reinterpret_cast<void**>(ppEnum));
            obj->Release();
            return hr;
        }

    private:
        std::vector<std::wstring> _values;
        size_t _index = 0;
    };

    HRESULT CreateStringEnum(const std::vector<std::wstring>& values, IEnumString** ppEnum)
    {
        if (!ppEnum) return E_POINTER;
        *ppEnum = nullptr;
        CComObject<StringEnum>* obj = nullptr;
        HRESULT hr = CComObject<StringEnum>::CreateInstance(&obj);
        if (FAILED(hr)) return hr;
        obj->AddRef();
        obj->Init(values);
        hr = obj->QueryInterface(IID_IEnumString, reinterpret_cast<void**>(ppEnum));
        obj->Release();
        return hr;
    }

    struct GroupItem
    {
        OPCHANDLE serverHandle = 0;
        OPCHANDLE clientHandle = 0;
        std::wstring itemId;
        bool active = true;
        VARTYPE canonicalType = VT_EMPTY;
        VARTYPE requestedType = VT_EMPTY;
    };

    class ATL_NO_VTABLE OpcGroup :
        public CComObjectRootEx<CComMultiThreadModel>,
        public IOPCGroupStateMgt,
        public IOPCItemMgt,
        public IOPCSyncIO,
        public IOPCAsyncIO2,
        public IConnectionPointContainerImpl<OpcGroup>,
        public IConnectionPointImpl<OpcGroup, &IID_IOPCDataCallback, CComDynamicUnkArray>
    {
    public:
        BEGIN_COM_MAP(OpcGroup)
            COM_INTERFACE_ENTRY(IOPCGroupStateMgt)
            COM_INTERFACE_ENTRY(IOPCItemMgt)
            COM_INTERFACE_ENTRY(IOPCSyncIO)
            COM_INTERFACE_ENTRY(IOPCAsyncIO2)
            COM_INTERFACE_ENTRY(IConnectionPointContainer)
        END_COM_MAP()

        BEGIN_CONNECTION_POINT_MAP(OpcGroup)
            CONNECTION_POINT_ENTRY(IID_IOPCDataCallback)
        END_CONNECTION_POINT_MAP()

        OpcGroup()
        {
            g_groupObjectCount.fetch_add(1);
            g_hadClient = true;
            TouchOpcDemand();
        }

        ~OpcGroup()
        {
            const LONG groupsLeft = g_groupObjectCount.fetch_sub(1) - 1;
            if (groupsLeft <= 0 && g_serverObjectCount.load() <= 0 && g_pendingActivationCount.load() <= 0)
            {
                g_lastClientGoneTick = GetTickCount64();
                ClearOpcDemand();
            }
        }

        void Init(const std::wstring& name, OPCHANDLE serverHandle, OPCHANDLE clientHandle,
                  BOOL active, DWORD updateRate, DWORD lcid, LONG timeBias, FLOAT deadband)
        {
            _name = name;
            _serverGroup = serverHandle;
            _clientGroup = clientHandle;
            _active = active != FALSE;
            _updateRate = std::max<DWORD>(100, updateRate);
            _lcid = lcid;
            _timeBias = timeBias;
            _deadband = deadband;
            _stop = false;
            _worker = std::thread([this]() { WorkerLoop(); });
        }

        void FinalRelease()
        {
            _stop = true;
            if (_worker.joinable())
            {
                if (_worker.get_id() == std::this_thread::get_id()) _worker.detach();
                else _worker.join();
            }
        }

        STDMETHOD(GetState)(DWORD* pUpdateRate, BOOL* pActive, LPWSTR* ppName, LONG* pTimeBias,
                            FLOAT* pPercentDeadband, DWORD* pLCID, OPCHANDLE* phClientGroup,
                            OPCHANDLE* phServerGroup) override
        {
            if (!pUpdateRate || !pActive || !ppName || !pTimeBias || !pPercentDeadband ||
                !pLCID || !phClientGroup || !phServerGroup) return E_POINTER;
            *pUpdateRate = _updateRate.load();
            *pActive = _active.load() ? TRUE : FALSE;
            *ppName = CoTaskMemString(_name);
            if (!*ppName) return E_OUTOFMEMORY;
            *pTimeBias = _timeBias;
            *pPercentDeadband = _deadband;
            *pLCID = _lcid;
            *phClientGroup = _clientGroup;
            *phServerGroup = _serverGroup;
            return S_OK;
        }

        STDMETHOD(SetState)(DWORD* pRequestedUpdateRate, DWORD* pRevisedUpdateRate, BOOL* pActive,
                            LONG* pTimeBias, FLOAT* pPercentDeadband, DWORD* pLCID,
                            OPCHANDLE* phClientGroup) override
        {
            if (!pRevisedUpdateRate) return E_POINTER;
            if (pRequestedUpdateRate) _updateRate = std::max<DWORD>(100, *pRequestedUpdateRate);
            if (pActive) _active = *pActive != FALSE;
            if (pTimeBias) _timeBias = *pTimeBias;
            if (pPercentDeadband) _deadband = *pPercentDeadband;
            if (pLCID) _lcid = *pLCID;
            if (phClientGroup) _clientGroup = *phClientGroup;
            *pRevisedUpdateRate = _updateRate.load();
            return S_OK;
        }

        STDMETHOD(SetName)(LPCWSTR szName) override
        {
            if (!szName) return E_INVALIDARG;
            _name = szName;
            return S_OK;
        }

        STDMETHOD(CloneGroup)(LPCWSTR, REFIID, LPUNKNOWN*) override
        {
            return E_NOTIMPL;
        }

        STDMETHOD(AddItems)(DWORD dwCount, OPCITEMDEF* pItemArray, OPCITEMRESULT** ppAddResults,
                            HRESULT** ppErrors) override
        {
            return AddOrValidateItems(dwCount, pItemArray, true, ppAddResults, ppErrors);
        }

        STDMETHOD(ValidateItems)(DWORD dwCount, OPCITEMDEF* pItemArray, BOOL,
                                 OPCITEMRESULT** ppValidationResults, HRESULT** ppErrors) override
        {
            return AddOrValidateItems(dwCount, pItemArray, false, ppValidationResults, ppErrors);
        }

        STDMETHOD(RemoveItems)(DWORD dwCount, OPCHANDLE* phServer, HRESULT** ppErrors) override
        {
            if (!phServer || !ppErrors) return E_POINTER;
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppErrors) return E_OUTOFMEMORY;
            bool anyError = false;
            std::lock_guard<std::mutex> lock(_itemsMutex);
            for (DWORD i = 0; i < dwCount; ++i)
            {
                auto it = std::find_if(_items.begin(), _items.end(), [&](const GroupItem& x) { return x.serverHandle == phServer[i]; });
                if (it == _items.end())
                {
                    (*ppErrors)[i] = OPC_E_INVALIDHANDLE;
                    anyError = true;
                }
                else
                {
                    _items.erase(it);
                    (*ppErrors)[i] = S_OK;
                }
            }
            return anyError ? S_FALSE : S_OK;
        }

        STDMETHOD(SetActiveState)(DWORD dwCount, OPCHANDLE* phServer, BOOL bActive, HRESULT** ppErrors) override
        {
            return UpdateItems(dwCount, phServer, ppErrors, [&](GroupItem& x, DWORD) { x.active = bActive != FALSE; });
        }

        STDMETHOD(SetClientHandles)(DWORD dwCount, OPCHANDLE* phServer, OPCHANDLE* phClient,
                                    HRESULT** ppErrors) override
        {
            if (!phClient) return E_POINTER;
            return UpdateItems(dwCount, phServer, ppErrors, [&](GroupItem& x, DWORD i) { x.clientHandle = phClient[i]; });
        }

        STDMETHOD(SetDatatypes)(DWORD dwCount, OPCHANDLE* phServer, VARTYPE* pRequestedDatatypes,
                                HRESULT** ppErrors) override
        {
            if (!pRequestedDatatypes) return E_POINTER;
            if (!phServer || !ppErrors) return E_POINTER;
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppErrors) return E_OUTOFMEMORY;
            bool anyError = false;
            std::lock_guard<std::mutex> lock(_itemsMutex);
            for (DWORD i = 0; i < dwCount; ++i)
            {
                auto it = FindItemUnlocked(phServer[i]);
                if (it == _items.end())
                {
                    (*ppErrors)[i] = OPC_E_INVALIDHANDLE;
                    anyError = true;
                }
                else if (!IsSupportedRequestedType(pRequestedDatatypes[i]))
                {
                    (*ppErrors)[i] = OPC_E_BADTYPE;
                    anyError = true;
                }
                else
                {
                    it->requestedType = pRequestedDatatypes[i];
                    (*ppErrors)[i] = S_OK;
                }
            }
            return anyError ? S_FALSE : S_OK;
        }

        STDMETHOD(CreateEnumerator)(REFIID, LPUNKNOWN* ppUnk) override
        {
            if (ppUnk) *ppUnk = nullptr;
            return E_NOTIMPL;
        }

        STDMETHOD(Read)(OPCDATASOURCE, DWORD dwCount, OPCHANDLE* phServer,
                        OPCITEMSTATE** ppItemValues, HRESULT** ppErrors) override
        {
            if (!phServer || !ppItemValues || !ppErrors) return E_POINTER;
            *ppItemValues = static_cast<OPCITEMSTATE*>(CoTaskMemAlloc(sizeof(OPCITEMSTATE) * dwCount));
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppItemValues || !*ppErrors)
            {
                if (*ppItemValues) CoTaskMemFree(*ppItemValues);
                if (*ppErrors) CoTaskMemFree(*ppErrors);
                *ppItemValues = nullptr;
                *ppErrors = nullptr;
                return E_OUTOFMEMORY;
            }
            ZeroMemory(*ppItemValues, sizeof(OPCITEMSTATE) * dwCount);
            bool anyError = false;
            for (DWORD i = 0; i < dwCount; ++i)
            {
                VariantInit(&(*ppItemValues)[i].vDataValue);
                HRESULT hr = ReadOne(phServer[i], (*ppItemValues)[i]);
                (*ppErrors)[i] = hr;
                if (FAILED(hr)) anyError = true;
            }
            return anyError ? S_FALSE : S_OK;
        }

        STDMETHOD(Write)(DWORD dwCount, OPCHANDLE*, VARIANT*, HRESULT** ppErrors) override
        {
            if (!ppErrors) return E_POINTER;
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppErrors) return E_OUTOFMEMORY;
            for (DWORD i = 0; i < dwCount; ++i) (*ppErrors)[i] = OPC_E_BADRIGHTS;
            return S_FALSE;
        }

        // IOPCAsyncIO2
        STDMETHOD(Read)(DWORD dwCount, OPCHANDLE* phServer, DWORD dwTransactionID,
                        DWORD* pdwCancelID, HRESULT** ppErrors) override
        {
            if (!phServer || !pdwCancelID || !ppErrors) return E_POINTER;
            std::vector<OPCHANDLE> handles(phServer, phServer + dwCount);
            bool anyError = false;
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppErrors) return E_OUTOFMEMORY;
            {
                std::lock_guard<std::mutex> lock(_itemsMutex);
                for (DWORD i = 0; i < dwCount; ++i)
                {
                    (*ppErrors)[i] = FindItemUnlocked(phServer[i]) == _items.end() ? OPC_E_INVALIDHANDLE : S_OK;
                    if (FAILED((*ppErrors)[i])) anyError = true;
                }
            }
            *pdwCancelID = ++_nextCancelId;
            QueueCallback(true, dwTransactionID, handles);
            return anyError ? S_FALSE : S_OK;
        }

        STDMETHOD(Write)(DWORD dwCount, OPCHANDLE*, VARIANT*, DWORD dwTransactionID,
                         DWORD* pdwCancelID, HRESULT** ppErrors) override
        {
            if (!pdwCancelID || !ppErrors) return E_POINTER;
            *pdwCancelID = ++_nextCancelId;
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppErrors) return E_OUTOFMEMORY;
            for (DWORD i = 0; i < dwCount; ++i) (*ppErrors)[i] = OPC_E_BADRIGHTS;
            QueueWriteComplete(dwTransactionID, dwCount, *ppErrors);
            return S_FALSE;
        }

        STDMETHOD(Refresh2)(OPCDATASOURCE, DWORD dwTransactionID, DWORD* pdwCancelID) override
        {
            if (!pdwCancelID) return E_POINTER;
            *pdwCancelID = ++_nextCancelId;
            QueueCallback(false, dwTransactionID, std::vector<OPCHANDLE>());
            return S_OK;
        }

        STDMETHOD(Cancel2)(DWORD) override
        {
            return S_OK;
        }

        STDMETHOD(SetEnable)(BOOL bEnable) override
        {
            _callbacksEnabled = bEnable != FALSE;
            return S_OK;
        }

        STDMETHOD(GetEnable)(BOOL* pbEnable) override
        {
            if (!pbEnable) return E_POINTER;
            *pbEnable = _callbacksEnabled.load() ? TRUE : FALSE;
            return S_OK;
        }

    private:
        std::wstring _name;
        OPCHANDLE _serverGroup = 0;
        OPCHANDLE _clientGroup = 0;
        std::atomic<bool> _active{ true };
        std::atomic<DWORD> _updateRate{ 1000 };
        DWORD _lcid = LOCALE_SYSTEM_DEFAULT;
        LONG _timeBias = 0;
        FLOAT _deadband = 0;
        std::mutex _itemsMutex;
        std::vector<GroupItem> _items;
        std::atomic<bool> _stop{ false };
        std::atomic<bool> _callbacksEnabled{ true };
        std::atomic<DWORD> _nextCancelId{ 0 };
        std::thread _worker;

        std::vector<GroupItem>::iterator FindItemUnlocked(OPCHANDLE handle)
        {
            return std::find_if(_items.begin(), _items.end(), [&](const GroupItem& x) { return x.serverHandle == handle; });
        }

        template<typename F>
        HRESULT UpdateItems(DWORD dwCount, OPCHANDLE* phServer, HRESULT** ppErrors, F update)
        {
            if (!phServer || !ppErrors) return E_POINTER;
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppErrors) return E_OUTOFMEMORY;
            bool anyError = false;
            std::lock_guard<std::mutex> lock(_itemsMutex);
            for (DWORD i = 0; i < dwCount; ++i)
            {
                auto it = FindItemUnlocked(phServer[i]);
                if (it == _items.end())
                {
                    (*ppErrors)[i] = OPC_E_INVALIDHANDLE;
                    anyError = true;
                }
                else
                {
                    update(*it, i);
                    (*ppErrors)[i] = S_OK;
                }
            }
            return anyError ? S_FALSE : S_OK;
        }

        HRESULT AddOrValidateItems(DWORD dwCount, OPCITEMDEF* pItemArray, bool add,
                                   OPCITEMRESULT** ppResults, HRESULT** ppErrors)
        {
            if (!pItemArray || !ppResults || !ppErrors) return E_POINTER;
            *ppResults = static_cast<OPCITEMRESULT*>(CoTaskMemAlloc(sizeof(OPCITEMRESULT) * dwCount));
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppResults || !*ppErrors)
            {
                if (*ppResults) CoTaskMemFree(*ppResults);
                if (*ppErrors) CoTaskMemFree(*ppErrors);
                *ppResults = nullptr;
                *ppErrors = nullptr;
                return E_OUTOFMEMORY;
            }
            ZeroMemory(*ppResults, sizeof(OPCITEMRESULT) * dwCount);

            bool anyError = false;
            std::lock_guard<std::mutex> lock(_itemsMutex);
            for (DWORD i = 0; i < dwCount; ++i)
            {
                SnapshotItem snap;
                const std::wstring id = pItemArray[i].szItemID ? pItemArray[i].szItemID : L"";
                HRESULT err = S_OK;
                if (id.empty() || !SnapshotStore::Instance().Find(id, snap)) err = OPC_E_UNKNOWNITEMID;
                else if (!IsSupportedRequestedType(pItemArray[i].vtRequestedDataType)) err = OPC_E_BADTYPE;

                (*ppErrors)[i] = err;
                if (FAILED(err))
                {
                    anyError = true;
                    continue;
                }

                OPCHANDLE handle = g_nextItemHandle.fetch_add(1);
                (*ppResults)[i].hServer = handle;
                (*ppResults)[i].vtCanonicalDataType = snap.vt;
                (*ppResults)[i].dwAccessRights = OPC_READABLE;
                (*ppResults)[i].dwBlobSize = 0;
                (*ppResults)[i].pBlob = nullptr;

                if (add)
                {
                    GroupItem gi;
                    gi.serverHandle = handle;
                    gi.clientHandle = pItemArray[i].hClient;
                    gi.itemId = id;
                    gi.active = pItemArray[i].bActive != FALSE;
                    gi.canonicalType = snap.vt;
                    gi.requestedType = pItemArray[i].vtRequestedDataType;
                    _items.push_back(gi);
                }
            }
            return anyError ? S_FALSE : S_OK;
        }

        HRESULT ReadOne(OPCHANDLE serverHandle, OPCITEMSTATE& state)
        {
            GroupItem gi;
            {
                std::lock_guard<std::mutex> lock(_itemsMutex);
                auto it = FindItemUnlocked(serverHandle);
                if (it == _items.end()) return OPC_E_INVALIDHANDLE;
                gi = *it;
            }

            SnapshotItem snap;
            if (!SnapshotStore::Instance().Find(gi.itemId, snap)) return OPC_E_UNKNOWNITEMID;
            state.hClient = gi.clientHandle;
            state.ftTimeStamp = snap.timestamp;
            state.wQuality = snap.quality;
            return SnapshotToVariant(snap, gi.requestedType, &state.vDataValue);
        }

        void WorkerLoop()
        {
            while (!_stop.load())
            {
                DWORD wait = _updateRate.load();
                DWORD elapsed = 0;
                while (elapsed < wait && !_stop.load())
                {
                    DWORD step = std::min<DWORD>(100, wait - elapsed);
                    Sleep(step);
                    elapsed += step;
                }
                if (_stop.load()) break;
                if (_active.load() && _callbacksEnabled.load())
                    FireCallback(false, 0, std::vector<OPCHANDLE>());
            }
        }

        void QueueCallback(bool readComplete, DWORD transactionId, const std::vector<OPCHANDLE>& handles)
        {
            AddRef();
            std::thread([this, readComplete, transactionId, handles]()
            {
                Sleep(1);
                FireCallback(readComplete, transactionId, handles);
                Release();
            }).detach();
        }

        void QueueWriteComplete(DWORD transactionId, DWORD count, HRESULT* errors)
        {
            std::vector<HRESULT> copy(errors, errors + count);
            AddRef();
            std::thread([this, transactionId, copy]()
            {
                Sleep(1);
                std::vector<OPCHANDLE> clientHandles(copy.size(), 0);
                std::vector<GroupItem> items;
                {
                    std::lock_guard<std::mutex> lock(_itemsMutex);
                    items = _items;
                }
                for (size_t i = 0; i < clientHandles.size() && i < items.size(); ++i)
                    clientHandles[i] = items[i].clientHandle;

                FireToCallbacks([&](IOPCDataCallback* cb)
                {
                    cb->OnWriteComplete(transactionId, _clientGroup, S_FALSE,
                        static_cast<DWORD>(copy.size()), clientHandles.data(), const_cast<HRESULT*>(copy.data()));
                });
                Release();
            }).detach();
        }

        template<typename F>
        void FireToCallbacks(F fire)
        {
            if (!_callbacksEnabled.load()) return;
            const int count = m_vec.GetSize();
            for (int i = 0; i < count; ++i)
            {
                CComPtr<IUnknown> unk = m_vec.GetAt(i);
                if (!unk) continue;
                CComQIPtr<IOPCDataCallback> cb(unk.p);
                if (cb) fire(cb);
            }
        }

        void FireCallback(bool readComplete, DWORD transactionId, const std::vector<OPCHANDLE>& requestedHandles)
        {
            std::vector<GroupItem> selected;
            {
                std::lock_guard<std::mutex> lock(_itemsMutex);
                if (requestedHandles.empty())
                {
                    for (const auto& x : _items) if (x.active) selected.push_back(x);
                }
                else
                {
                    for (auto h : requestedHandles)
                    {
                        auto it = FindItemUnlocked(h);
                        if (it != _items.end()) selected.push_back(*it);
                    }
                }
            }
            if (selected.empty()) return;

            const DWORD n = static_cast<DWORD>(selected.size());
            std::vector<OPCHANDLE> clients(n);
            std::vector<VARIANT> values(n);
            std::vector<WORD> qualities(n);
            std::vector<FILETIME> timestamps(n);
            std::vector<HRESULT> errors(n);
            bool anyBad = false;
            for (DWORD i = 0; i < n; ++i)
            {
                VariantInit(&values[i]);
                clients[i] = selected[i].clientHandle;
                SnapshotItem snap;
                if (!SnapshotStore::Instance().Find(selected[i].itemId, snap))
                {
                    errors[i] = OPC_E_UNKNOWNITEMID;
                    qualities[i] = 0x18;
                    timestamps[i] = {};
                    anyBad = true;
                    continue;
                }
                qualities[i] = snap.quality;
                timestamps[i] = snap.timestamp;
                errors[i] = SnapshotToVariant(snap, selected[i].requestedType, &values[i]);
                if (FAILED(errors[i]) || (qualities[i] & 0xC0) != 0xC0) anyBad = true;
            }

            const HRESULT master = anyBad ? S_FALSE : S_OK;
            FireToCallbacks([&](IOPCDataCallback* cb)
            {
                if (readComplete)
                    cb->OnReadComplete(transactionId, _clientGroup, master, master, n,
                        clients.data(), values.data(), qualities.data(), timestamps.data(), errors.data());
                else
                    cb->OnDataChange(transactionId, _clientGroup, master, master, n,
                        clients.data(), values.data(), qualities.data(), timestamps.data(), errors.data());
            });

            for (auto& v : values) VariantClear(&v);
        }
    };

    class ATL_NO_VTABLE OpcServer :
        public CComObjectRootEx<CComMultiThreadModel>,
        public IOPCServer,
        public IOPCCommon,
        public IOPCBrowseServerAddressSpace,
        public IOPCItemProperties
    {
    public:
        OpcServer()
        {
            g_serverObjectCount.fetch_add(1);
            g_hadClient = true;
            TouchOpcDemand();
        }

        ~OpcServer()
        {
            const LONG left = g_serverObjectCount.fetch_sub(1) - 1;
            if (left <= 0 && g_groupObjectCount.load() <= 0 && g_pendingActivationCount.load() <= 0)
            {
                g_lastClientGoneTick = GetTickCount64();
                ClearOpcDemand();
            }
        }

        BEGIN_COM_MAP(OpcServer)
            COM_INTERFACE_ENTRY(IOPCServer)
            COM_INTERFACE_ENTRY(IOPCCommon)
            COM_INTERFACE_ENTRY(IOPCBrowseServerAddressSpace)
            COM_INTERFACE_ENTRY(IOPCItemProperties)
        END_COM_MAP()

        STDMETHOD(AddGroup)(LPCWSTR szName, BOOL bActive, DWORD dwRequestedUpdateRate,
                            OPCHANDLE hClientGroup, LONG* pTimeBias, FLOAT* pPercentDeadband,
                            DWORD dwLCID, OPCHANDLE* phServerGroup, DWORD* pRevisedUpdateRate,
                            REFIID riid, LPUNKNOWN* ppUnk) override
        {
            if (!phServerGroup || !pRevisedUpdateRate || !ppUnk) return E_POINTER;
            *ppUnk = nullptr;
            std::wstring name = szName ? szName : L"";
            const OPCHANDLE handle = g_nextGroupHandle.fetch_add(1);
            if (name.empty()) name = L"Group" + std::to_wstring(handle);

            {
                std::lock_guard<std::mutex> lock(_groupsMutex);
                for (const auto& g : _groups)
                    if (_wcsicmp(g.name.c_str(), name.c_str()) == 0) return OPC_E_DUPLICATENAME;
            }

            CComObject<OpcGroup>* group = nullptr;
            HRESULT hr = CComObject<OpcGroup>::CreateInstance(&group);
            if (FAILED(hr)) return hr;
            group->AddRef();
            group->Init(name, handle, hClientGroup, bActive, dwRequestedUpdateRate,
                        dwLCID, pTimeBias ? *pTimeBias : 0, pPercentDeadband ? *pPercentDeadband : 0.0f);

            hr = group->QueryInterface(riid, reinterpret_cast<void**>(ppUnk));
            if (SUCCEEDED(hr))
            {
                IUnknown* rawKeeper = nullptr;
                group->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&rawKeeper));
                CComPtr<IUnknown> keeper;
                keeper.Attach(rawKeeper);
                std::lock_guard<std::mutex> lock(_groupsMutex);
                _groups.push_back({ handle, name, keeper });
                *phServerGroup = handle;
                *pRevisedUpdateRate = std::max<DWORD>(100, dwRequestedUpdateRate);
            }
            group->Release();
            return hr;
        }

        STDMETHOD(GetErrorString)(HRESULT dwError, LCID, LPWSTR* ppString) override
        {
            return ErrorString(dwError, ppString);
        }

        STDMETHOD(GetGroupByName)(LPCWSTR szName, REFIID riid, LPUNKNOWN* ppUnk) override
        {
            if (!szName || !ppUnk) return E_POINTER;
            *ppUnk = nullptr;
            std::lock_guard<std::mutex> lock(_groupsMutex);
            for (auto& g : _groups)
            {
                if (_wcsicmp(g.name.c_str(), szName) == 0)
                    return g.unk->QueryInterface(riid, reinterpret_cast<void**>(ppUnk));
            }
            return E_INVALIDARG;
        }

        STDMETHOD(GetStatus)(OPCSERVERSTATUS** ppServerStatus) override
        {
            if (!ppServerStatus) return E_POINTER;
            *ppServerStatus = static_cast<OPCSERVERSTATUS*>(CoTaskMemAlloc(sizeof(OPCSERVERSTATUS)));
            if (!*ppServerStatus) return E_OUTOFMEMORY;
            ZeroMemory(*ppServerStatus, sizeof(OPCSERVERSTATUS));
            auto s = *ppServerStatus;
            s->ftStartTime = g_startTime;
            GetUtcFileTimeCompat(&s->ftCurrentTime);
            s->ftLastUpdateTime = SnapshotStore::Instance().LastUpdateFileTime();
            s->dwServerState = SnapshotStore::Instance().IsStale() ? OPC_STATUS_COMM_FAULT : OPC_STATUS_RUNNING;
            {
                std::lock_guard<std::mutex> lock(_groupsMutex);
                s->dwGroupCount = static_cast<DWORD>(_groups.size());
            }
            s->dwBandWidth = 0xFFFFFFFF;
            s->wMajorVersion = 0;
            s->wMinorVersion = 3;
            s->wBuildNumber = 1;
            s->szVendorInfo = CoTaskMemString(L"ASTUE MPS Replacement OPC DA 2.05a");
            if (!s->szVendorInfo)
            {
                CoTaskMemFree(s);
                *ppServerStatus = nullptr;
                return E_OUTOFMEMORY;
            }
            return S_OK;
        }

        STDMETHOD(RemoveGroup)(OPCHANDLE hServerGroup, BOOL) override
        {
            std::lock_guard<std::mutex> lock(_groupsMutex);
            auto it = std::find_if(_groups.begin(), _groups.end(), [&](const GroupEntry& x) { return x.handle == hServerGroup; });
            if (it == _groups.end()) return E_INVALIDARG;
            _groups.erase(it);
            return S_OK;
        }

        STDMETHOD(CreateGroupEnumerator)(OPCENUMSCOPE, REFIID, LPUNKNOWN* ppUnk) override
        {
            if (ppUnk) *ppUnk = nullptr;
            return E_NOTIMPL;
        }

        // IOPCCommon
        STDMETHOD(SetLocaleID)(LCID dwLcid) override { _lcid = dwLcid; return S_OK; }
        STDMETHOD(GetLocaleID)(LCID* pdwLcid) override { if (!pdwLcid) return E_POINTER; *pdwLcid = _lcid; return S_OK; }

        STDMETHOD(QueryAvailableLocaleIDs)(DWORD* pdwCount, LCID** pdwLcid) override
        {
            if (!pdwCount || !pdwLcid) return E_POINTER;
            *pdwCount = 2;
            *pdwLcid = static_cast<LCID*>(CoTaskMemAlloc(sizeof(LCID) * 2));
            if (!*pdwLcid) return E_OUTOFMEMORY;
            (*pdwLcid)[0] = 0x0409;
            (*pdwLcid)[1] = 0x0419;
            return S_OK;
        }

        STDMETHOD(GetErrorString)(HRESULT dwError, LPWSTR* ppString) override
        {
            return ErrorString(dwError, ppString);
        }

        STDMETHOD(SetClientName)(LPCWSTR szName) override
        {
            _clientName = szName ? szName : L"";
            return S_OK;
        }

        // IOPCBrowseServerAddressSpace
        STDMETHOD(QueryOrganization)(OPCNAMESPACETYPE* pNameSpaceType) override
        {
            if (!pNameSpaceType) return E_POINTER;
            *pNameSpaceType = OPC_NS_HIERARCHIAL;
            return S_OK;
        }

        STDMETHOD(ChangeBrowsePosition)(OPCBROWSEDIRECTION direction, LPCWSTR szString) override
        {
            std::wstring target;
            if (direction == OPC_BROWSE_UP)
            {
                if (_browsePosition.empty()) return E_FAIL;
                auto pos = _browsePosition.find_last_of(L'.');
                _browsePosition = pos == std::wstring::npos ? L"" : _browsePosition.substr(0, pos);
                return S_OK;
            }
            if (direction == OPC_BROWSE_DOWN)
            {
                if (!szString || !*szString) return E_INVALIDARG;
                target = _browsePosition.empty() ? szString : _browsePosition + L"." + szString;
            }
            else if (direction == OPC_BROWSE_TO)
            {
                target = szString ? szString : L"";
            }
            else return E_INVALIDARG;

            if (!target.empty() && !BranchExists(target)) return E_INVALIDARG;
            _browsePosition = target;
            return S_OK;
        }

        STDMETHOD(BrowseOPCItemIDs)(OPCBROWSETYPE filterType, LPCWSTR szFilterCriteria,
                                    VARTYPE vtDataTypeFilter, DWORD dwAccessRightsFilter,
                                    LPENUMSTRING* ppIEnumString) override
        {
            if (!ppIEnumString) return E_POINTER;
            *ppIEnumString = nullptr;
            std::vector<std::wstring> values;
            auto all = SnapshotStore::Instance().All();

            if (filterType == OPC_FLAT)
            {
                for (const auto& item : all)
                {
                    if (!_browsePosition.empty() && !StartsWithI(item.id, _browsePosition + L".")) continue;
                    if (vtDataTypeFilter != VT_EMPTY && item.vt != vtDataTypeFilter) continue;
                    if (dwAccessRightsFilter && (dwAccessRightsFilter & OPC_READABLE) == 0) continue;
                    if (!WildMatch(item.id, szFilterCriteria)) continue;
                    values.push_back(item.id);
                }
            }
            else
            {
                std::set<std::wstring, std::less<>> unique;
                const std::wstring prefix = _browsePosition.empty() ? L"" : _browsePosition + L".";
                for (const auto& item : all)
                {
                    if (!prefix.empty() && !StartsWithI(item.id, prefix)) continue;
                    std::wstring rest = prefix.empty() ? item.id : item.id.substr(prefix.size());
                    if (rest.empty()) continue;
                    auto dot = rest.find(L'.');
                    if (filterType == OPC_BRANCH)
                    {
                        if (dot == std::wstring::npos) continue;
                        auto name = rest.substr(0, dot);
                        if (WildMatch(name, szFilterCriteria)) unique.insert(name);
                    }
                    else if (filterType == OPC_LEAF)
                    {
                        if (dot != std::wstring::npos) continue;
                        if (vtDataTypeFilter != VT_EMPTY && item.vt != vtDataTypeFilter) continue;
                        if (dwAccessRightsFilter && (dwAccessRightsFilter & OPC_READABLE) == 0) continue;
                        if (WildMatch(rest, szFilterCriteria)) unique.insert(rest);
                    }
                }
                values.assign(unique.begin(), unique.end());
            }
            return CreateStringEnum(values, ppIEnumString);
        }

        STDMETHOD(GetItemID)(LPWSTR szItemDataID, LPWSTR* szItemID) override
        {
            if (!szItemDataID || !szItemID) return E_POINTER;
            const std::wstring full = _browsePosition.empty() ? szItemDataID : _browsePosition + L"." + szItemDataID;
            SnapshotItem item;
            if (!SnapshotStore::Instance().Find(full, item)) return OPC_E_UNKNOWNITEMID;
            *szItemID = CoTaskMemString(full);
            return *szItemID ? S_OK : E_OUTOFMEMORY;
        }

        STDMETHOD(BrowseAccessPaths)(LPCWSTR, LPENUMSTRING* ppIEnumString) override
        {
            return CreateStringEnum(std::vector<std::wstring>(), ppIEnumString);
        }

        // IOPCItemProperties
        STDMETHOD(QueryAvailableProperties)(LPWSTR szItemID, DWORD* pdwCount, DWORD** ppPropertyIDs,
                                             LPWSTR** ppDescriptions, VARTYPE** ppvtDataTypes) override
        {
            if (!szItemID || !pdwCount || !ppPropertyIDs || !ppDescriptions || !ppvtDataTypes) return E_POINTER;
            SnapshotItem item;
            if (!SnapshotStore::Instance().Find(szItemID, item)) return OPC_E_UNKNOWNITEMID;

            const DWORD count = 5;
            *pdwCount = count;
            *ppPropertyIDs = static_cast<DWORD*>(CoTaskMemAlloc(sizeof(DWORD) * count));
            *ppDescriptions = static_cast<LPWSTR*>(CoTaskMemAlloc(sizeof(LPWSTR) * count));
            *ppvtDataTypes = static_cast<VARTYPE*>(CoTaskMemAlloc(sizeof(VARTYPE) * count));
            if (!*ppPropertyIDs || !*ppDescriptions || !*ppvtDataTypes) return E_OUTOFMEMORY;

            const wchar_t* descriptions[count] = { L"Data Type", L"Value", L"Quality", L"Timestamp", L"Access Rights" };
            const DWORD ids[count] = { 1, 2, 3, 4, 5 };
            const VARTYPE types[count] = { VT_I2, item.vt, VT_I2, VT_DATE, VT_I4 };
            for (DWORD i = 0; i < count; ++i)
            {
                (*ppPropertyIDs)[i] = ids[i];
                (*ppvtDataTypes)[i] = types[i];
                (*ppDescriptions)[i] = CoTaskMemString(descriptions[i]);
            }
            return S_OK;
        }

        STDMETHOD(GetItemProperties)(LPWSTR szItemID, DWORD dwCount, DWORD* pdwPropertyIDs,
                                     VARIANT** ppvData, HRESULT** ppErrors) override
        {
            if (!szItemID || !pdwPropertyIDs || !ppvData || !ppErrors) return E_POINTER;
            SnapshotItem item;
            if (!SnapshotStore::Instance().Find(szItemID, item)) return OPC_E_UNKNOWNITEMID;

            *ppvData = static_cast<VARIANT*>(CoTaskMemAlloc(sizeof(VARIANT) * dwCount));
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!*ppvData || !*ppErrors) return E_OUTOFMEMORY;
            bool anyError = false;
            for (DWORD i = 0; i < dwCount; ++i)
            {
                VariantInit(&(*ppvData)[i]);
                HRESULT hr = S_OK;
                switch (pdwPropertyIDs[i])
                {
                case 1:
                    (*ppvData)[i].vt = VT_I2;
                    (*ppvData)[i].iVal = static_cast<SHORT>(item.vt);
                    break;
                case 2:
                    hr = SnapshotToVariant(item, VT_EMPTY, &(*ppvData)[i]);
                    break;
                case 3:
                    (*ppvData)[i].vt = VT_I2;
                    (*ppvData)[i].iVal = static_cast<SHORT>(item.quality);
                    break;
                case 4:
                {
                    SYSTEMTIME st = {};
                    double date = 0;
                    if (FileTimeToSystemTime(&item.timestamp, &st) && SystemTimeToVariantTime(&st, &date))
                    {
                        (*ppvData)[i].vt = VT_DATE;
                        (*ppvData)[i].date = date;
                    }
                    else hr = E_FAIL;
                    break;
                }
                case 5:
                    (*ppvData)[i].vt = VT_I4;
                    (*ppvData)[i].lVal = OPC_READABLE;
                    break;
                default:
                    hr = OPC_E_INVALID_PID;
                    break;
                }
                (*ppErrors)[i] = hr;
                if (FAILED(hr)) anyError = true;
            }
            return anyError ? S_FALSE : S_OK;
        }

        STDMETHOD(LookupItemIDs)(LPWSTR szItemID, DWORD dwCount, DWORD* pdwPropertyIDs,
                                 LPWSTR** ppszNewItemIDs, HRESULT** ppErrors) override
        {
            if (!szItemID || !pdwPropertyIDs || !ppszNewItemIDs || !ppErrors) return E_POINTER;
            SnapshotItem item;
            if (!SnapshotStore::Instance().Find(szItemID, item)) return OPC_E_UNKNOWNITEMID;
            *ppszNewItemIDs = static_cast<LPWSTR*>(CoTaskMemAlloc(sizeof(LPWSTR) * dwCount));
            // The generated MIDL signature is LPWSTR**: allocate an array of LPWSTR.
            auto arr = *ppszNewItemIDs;
            *ppErrors = static_cast<HRESULT*>(CoTaskMemAlloc(sizeof(HRESULT) * dwCount));
            if (!arr || !*ppErrors) return E_OUTOFMEMORY;
            for (DWORD i = 0; i < dwCount; ++i)
            {
                arr[i] = nullptr;
                (*ppErrors)[i] = OPC_E_INVALID_PID;
            }
            return S_FALSE;
        }

    private:
        struct GroupEntry
        {
            OPCHANDLE handle;
            std::wstring name;
            CComPtr<IUnknown> unk;
        };

        LCID _lcid = LOCALE_SYSTEM_DEFAULT;
        std::wstring _clientName;
        std::wstring _browsePosition;
        std::mutex _groupsMutex;
        std::vector<GroupEntry> _groups;

        static HRESULT ErrorString(HRESULT error, LPWSTR* ppString)
        {
            if (!ppString) return E_POINTER;
            std::wstring text;
            switch (error)
            {
            case S_OK: text = L"РЈСЃРїРµС€РЅРѕ"; break;
            case OPC_E_UNKNOWNITEMID: text = L"РќРµРёР·РІРµСЃС‚РЅС‹Р№ OPC ItemID"; break;
            case OPC_E_INVALIDHANDLE: text = L"РќРµРґРѕРїСѓСЃС‚РёРјС‹Р№ OPC handle"; break;
            case OPC_E_BADTYPE: text = L"РќРµРїРѕРґРґРµСЂР¶РёРІР°РµРјС‹Р№ С‚РёРї РґР°РЅРЅС‹С…"; break;
            case OPC_E_BADRIGHTS: text = L"Р—Р°РїРёСЃСЊ Р·Р°РїСЂРµС‰РµРЅР°: СЃРµСЂРІРµСЂ СЂР°Р±РѕС‚Р°РµС‚ РІ СЂРµР¶РёРјРµ ReadOnly"; break;
            default:
            {
                wchar_t buf[64] = {};
                swprintf_s(buf, L"HRESULT 0x%08X", static_cast<unsigned>(error));
                text = buf;
                break;
            }
            }
            *ppString = CoTaskMemString(text);
            return *ppString ? S_OK : E_OUTOFMEMORY;
        }

        bool BranchExists(const std::wstring& branch)
        {
            const std::wstring prefix = branch + L".";
            auto all = SnapshotStore::Instance().All();
            for (const auto& item : all)
                if (StartsWithI(item.id, prefix)) return true;
            return false;
        }
    };

    class ClassFactory : public IClassFactory
    {
    public:
        ClassFactory() : _ref(1) {}

        STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            *ppv = nullptr;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }

        STDMETHOD_(ULONG, AddRef)() override { return ++_ref; }
        STDMETHOD_(ULONG, Release)() override
        {
            ULONG r = --_ref;
            if (!r) delete this;
            return r;
        }

        STDMETHOD(CreateInstance)(IUnknown* pUnkOuter, REFIID riid, void** ppvObject) override
        {
            if (!ppvObject) return E_POINTER;
            *ppvObject = nullptr;
            if (pUnkOuter) return CLASS_E_NOAGGREGATION;
            if (IsConfigurationModeActive()) return CO_E_SERVER_EXEC_FAILURE;

            g_pendingActivationCount.fetch_add(1);
            TouchOpcDemand();
            if (!LaunchMainRuntimeIfNeeded())
            {
                g_pendingActivationCount.fetch_sub(1);
                if (ActiveOpcDemandCount() <= 0) ClearOpcDemand();
                return CO_E_SERVER_EXEC_FAILURE;
            }
            WaitForRuntimeSnapshot(30000);

            CComObject<OpcServer>* server = nullptr;
            HRESULT hr = CComObject<OpcServer>::CreateInstance(&server);
            if (FAILED(hr))
            {
                g_pendingActivationCount.fetch_sub(1);
                if (ActiveOpcDemandCount() <= 0) ClearOpcDemand();
                return hr;
            }
            server->AddRef();
            hr = server->QueryInterface(riid, ppvObject);
            server->Release();
            g_pendingActivationCount.fetch_sub(1);
            if (ActiveOpcDemandCount() <= 0) ClearOpcDemand();
            return hr;
        }

        STDMETHOD(LockServer)(BOOL fLock) override
        {
            if (fLock) g_serverLockCount.fetch_add(1);
            else
            {
                LONG current = g_serverLockCount.load();
                while (current > 0 && !g_serverLockCount.compare_exchange_weak(current, current - 1)) {}
            }
            return S_OK;
        }

    private:
        std::atomic<ULONG> _ref;
    };

    HRESULT SetRegistryString(HKEY root, const std::wstring& subkey, const wchar_t* valueName, const std::wstring& value)
    {
        HKEY key = nullptr;
        LONG rc = RegCreateKeyExW(root, subkey.c_str(), 0, nullptr, REG_OPTION_NON_VOLATILE,
                                  KEY_SET_VALUE, nullptr, &key, nullptr);
        if (rc != ERROR_SUCCESS) return HRESULT_FROM_WIN32(rc);
        rc = RegSetValueExW(key, valueName, 0, REG_SZ,
                            reinterpret_cast<const BYTE*>(value.c_str()),
                            static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
        RegCloseKey(key);
        return HRESULT_FROM_WIN32(rc);
    }

    std::wstring ModulePath()
    {
        std::vector<wchar_t> buf(32768);
        DWORD n = GetModuleFileNameW(nullptr, buf.data(), static_cast<DWORD>(buf.size()));
        return std::wstring(buf.data(), n);
    }

    HRESULT RegisterServer()
    {
        const auto clsid = GuidToString(CLSID_AstueMpsOpcDa);
        const auto appid = GuidToString(APPID_AstueMpsOpcDa);
        const auto catid = GuidToString(CATID_OPCDAServer20);
        const auto exe = ModulePath();

        HRESULT hr = SetRegistryString(HKEY_CLASSES_ROOT, L"CLSID\\" + clsid, nullptr, kFriendlyName);
        if (FAILED(hr)) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, L"CLSID\\" + clsid + L"\\LocalServer32", nullptr, L"\"" + exe + L"\""))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, L"CLSID\\" + clsid + L"\\ProgID", nullptr, kProgId))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, L"CLSID\\" + clsid + L"\\VersionIndependentProgID", nullptr, kVersionIndependentProgId))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, L"CLSID\\" + clsid, L"AppID", appid))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, L"CLSID\\" + clsid + L"\\Implemented Categories\\" + catid, nullptr, L""))) return hr;

        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, std::wstring(kProgId), nullptr, kFriendlyName))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, std::wstring(kProgId) + L"\\CLSID", nullptr, clsid))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, std::wstring(kVersionIndependentProgId), nullptr, kFriendlyName))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, std::wstring(kVersionIndependentProgId) + L"\\CLSID", nullptr, clsid))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, std::wstring(kVersionIndependentProgId) + L"\\CurVer", nullptr, kProgId))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, L"AppID\\" + appid, nullptr, kFriendlyName))) return hr;
        if (FAILED(hr = SetRegistryString(HKEY_CLASSES_ROOT, L"AppID\\AstueMpsOpcDaServer.exe", L"AppID", appid))) return hr;

        CComPtr<ICatRegister> cat;
        hr = cat.CoCreateInstance(CLSID_StdComponentCategoriesMgr, nullptr, CLSCTX_INPROC_SERVER);
        if (SUCCEEDED(hr))
        {
            CATID id = CATID_OPCDAServer20;
            cat->RegisterClassImplCategories(CLSID_AstueMpsOpcDa, 1, &id);
        }
        return S_OK;
    }

    HRESULT UnregisterServer()
    {
        CComPtr<ICatRegister> cat;
        if (SUCCEEDED(cat.CoCreateInstance(CLSID_StdComponentCategoriesMgr, nullptr, CLSCTX_INPROC_SERVER)))
        {
            CATID id = CATID_OPCDAServer20;
            cat->UnRegisterClassImplCategories(CLSID_AstueMpsOpcDa, 1, &id);
        }

        const auto clsid = GuidToString(CLSID_AstueMpsOpcDa);
        const auto appid = GuidToString(APPID_AstueMpsOpcDa);
        RegDeleteTreeW(HKEY_CLASSES_ROOT, (L"CLSID\\" + clsid).c_str());
        RegDeleteTreeW(HKEY_CLASSES_ROOT, kProgId);
        RegDeleteTreeW(HKEY_CLASSES_ROOT, kVersionIndependentProgId);
        RegDeleteTreeW(HKEY_CLASSES_ROOT, (L"AppID\\" + appid).c_str());
        RegDeleteTreeW(HKEY_CLASSES_ROOT, L"AppID\\AstueMpsOpcDaServer.exe");
        return S_OK;
    }

    bool ArgEquals(LPCWSTR arg, LPCWSTR expected)
    {
        return arg && expected && _wcsicmp(arg, expected) == 0;
    }

    HANDLE OpenOrCreateStopEvent()
    {
        HANDLE h = CreateEventW(nullptr, TRUE, FALSE, kStopEventGlobal);
        if (!h) h = CreateEventW(nullptr, TRUE, FALSE, kStopEventLocal);
        return h;
    }

    int SignalStop()
    {
        HANDLE h = OpenEventW(EVENT_MODIFY_STATE, FALSE, kStopEventGlobal);
        if (!h) h = OpenEventW(EVENT_MODIFY_STATE, FALSE, kStopEventLocal);
        if (!h) return 2;
        SetEvent(h);
        CloseHandle(h);
        return 0;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return static_cast<int>(hr);

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    bool manualMode = false;

    for (int i = 1; i < argc; ++i)
    {
        if (ArgEquals(argv[i], L"--register") || ArgEquals(argv[i], L"/RegServer") || ArgEquals(argv[i], L"-RegServer"))
        {
            hr = RegisterServer();
            if (argv) LocalFree(argv);
            CoUninitialize();
            return SUCCEEDED(hr) ? 0 : static_cast<int>(hr);
        }
        if (ArgEquals(argv[i], L"--unregister") || ArgEquals(argv[i], L"/UnregServer") || ArgEquals(argv[i], L"-UnregServer"))
        {
            hr = UnregisterServer();
            if (argv) LocalFree(argv);
            CoUninitialize();
            return SUCCEEDED(hr) ? 0 : static_cast<int>(hr);
        }
        if (ArgEquals(argv[i], L"--stop"))
        {
            const int rc = SignalStop();
            if (argv) LocalFree(argv);
            CoUninitialize();
            return rc;
        }
        if (ArgEquals(argv[i], L"--manual")) manualMode = true;
    }
    if (argv) LocalFree(argv);
    if (IsManualKeepAliveRequested()) manualMode = true;

    // В режиме конфигурирования OPC DA не должен работать вообще.
    if (IsConfigurationModeActive())
    {
        CoUninitialize();
        return static_cast<int>(CO_E_SERVER_EXEC_FAILURE);
    }

    // Ручной Stop в GUI создаёт блокировку. COM/DCOM может запустить EXE,
    // но demand-экземпляр сразу завершится и не зарегистрирует class object.
    if (!manualMode && IsOpcAutoStartBlocked())
    {
        CoUninitialize();
        return static_cast<int>(CO_E_SERVER_EXEC_FAILURE);
    }

    HANDLE mutex = CreateMutexW(nullptr, FALSE, kSingleInstanceMutex);
    if (mutex && GetLastError() == ERROR_ALREADY_EXISTS)
    {
        CloseHandle(mutex);
        CoUninitialize();
        return 0;
    }

    GetUtcFileTimeCompat(&g_startTime);
    SnapshotStore::Instance().All();

    ClassFactory* factory = new ClassFactory();
    DWORD cookie = 0;
    hr = CoRegisterClassObject(CLSID_AstueMpsOpcDa, factory, CLSCTX_LOCAL_SERVER,
                               REGCLS_MULTIPLEUSE, &cookie);
    factory->Release();
    if (FAILED(hr))
    {
        if (mutex) CloseHandle(mutex);
        CoUninitialize();
        return static_cast<int>(hr);
    }

    CoResumeClassObjects();
    HANDLE stopEvent = OpenOrCreateStopEvent();
    const ULONGLONG startTick = GetTickCount64();
    WriteOpcState(manualMode);

    for (;;)
    {
        const DWORD wait = stopEvent ? WaitForSingleObject(stopEvent, 1000) : WAIT_TIMEOUT;
        if (wait == WAIT_OBJECT_0) break;
        if (IsConfigurationModeActive()) break;

        const LONG activeDemand = ActiveOpcDemandCount();
        if (activeDemand > 0)
        {
            TouchOpcDemand();
            // Keep the polling/runtime application alive for as long as an OPC client
            // is actually connected. This also recovers if the GUI was closed or crashed
            // while the COM object itself remained alive.
            if (!IsMainApplicationRunning() && !IsOpcAutoStartBlocked())
                LaunchMainRuntimeIfNeeded();
        }
        else ClearOpcDemand();
        const bool effectiveManualMode = manualMode || IsManualKeepAliveRequested();
        WriteOpcState(effectiveManualMode);

        if (!effectiveManualMode && activeDemand <= 0 && g_serverLockCount.load() <= 0)
        {
            const ULONGLONG now = GetTickCount64();
            if (g_hadClient.load())
            {
                const ULONGLONG gone = g_lastClientGoneTick.load();
                if (gone != 0 && now - gone >= 15000ULL) break;
            }
            else if (now - startTick >= 30000ULL)
            {
                // COM запустил EXE, но объект так и не был создан.
                break;
            }
        }
    }

    ClearOpcDemand();
    ClearOpcState();
    CoRevokeClassObject(cookie);
    if (stopEvent) CloseHandle(stopEvent);
    if (mutex) CloseHandle(mutex);
    CoUninitialize();
    return 0;
}
