// COM plumbing: class factory + self-registration (regsvr32 AmpCastAsio64.dll).
// The installer registers through AmpCast.exe --register-asio instead
// (same keys, see src/AmpCast/Setup/AsioRegistration.cs).
#include "AmpCastAsio.h"

#include <new>
#include <objbase.h>
#include <wchar.h>

LONG g_objectCount = 0;
static LONG    g_lockCount = 0;
static HMODULE g_module = nullptr;

namespace {

class ClassFactory final : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory)) {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override  { return 2; }   // static instance
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        *ppv = nullptr;
        if (outer)
            return CLASS_E_NOAGGREGATION;
        AmpCastAsio* driver = new (std::nothrow) AmpCastAsio();
        if (!driver)
            return E_OUTOFMEMORY;
        const HRESULT hr = driver->QueryInterface(riid, ppv);
        driver->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_lockCount);
        else      InterlockedDecrement(&g_lockCount);
        return S_OK;
    }
};

ClassFactory g_factory;

void ClsidString(wchar_t* out, int chars)
{
    StringFromGUID2(CLSID_AmpCastAsio, out, chars);
}

LSTATUS SetString(HKEY root, const wchar_t* subKey, const wchar_t* name, const wchar_t* value)
{
    return RegSetKeyValueW(root, subKey, name, REG_SZ, value,
                           static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!IsEqualCLSID(rclsid, CLSID_AmpCastAsio))
        return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return (g_objectCount == 0 && g_lockCount == 0) ? S_OK : S_FALSE;
}

extern "C" HRESULT __stdcall DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH))
        return E_FAIL;

    wchar_t clsid[64];
    ClsidString(clsid, 64);

    wchar_t clsidKey[128];
    wsprintfW(clsidKey, L"SOFTWARE\\Classes\\CLSID\\%s", clsid);
    wchar_t inprocKey[160];
    wsprintfW(inprocKey, L"%s\\InprocServer32", clsidKey);
    wchar_t asioKey[128];
    wsprintfW(asioKey, L"SOFTWARE\\ASIO\\%s", kDriverNameW);

    if (SetString(HKEY_LOCAL_MACHINE, clsidKey, nullptr, L"AmpCast ASIO Driver") != ERROR_SUCCESS ||
        SetString(HKEY_LOCAL_MACHINE, inprocKey, nullptr, path) != ERROR_SUCCESS ||
        SetString(HKEY_LOCAL_MACHINE, inprocKey, L"ThreadingModel", L"Apartment") != ERROR_SUCCESS ||
        SetString(HKEY_LOCAL_MACHINE, asioKey, L"CLSID", clsid) != ERROR_SUCCESS ||
        SetString(HKEY_LOCAL_MACHINE, asioKey, L"Description", kDriverNameW) != ERROR_SUCCESS)
        return E_ACCESSDENIED;   // run as administrator
    return S_OK;
}

extern "C" HRESULT __stdcall DllUnregisterServer()
{
    wchar_t clsid[64];
    ClsidString(clsid, 64);
    wchar_t clsidKey[128];
    wsprintfW(clsidKey, L"SOFTWARE\\Classes\\CLSID\\%s", clsid);
    wchar_t asioKey[128];
    wsprintfW(asioKey, L"SOFTWARE\\ASIO\\%s", kDriverNameW);

    RegDeleteTreeW(HKEY_LOCAL_MACHINE, clsidKey);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, asioKey);
    return S_OK;
}
