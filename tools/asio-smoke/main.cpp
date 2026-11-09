// AmpCast ASIO smoke test: behaves like a tiny DAW.
// Loads AmpCastAsio64.dll directly (no registration needed), opens the real
// interface through it, runs for 3 s, writes a quiet 440 Hz tone ONLY to the
// virtual "AmpCast Discord L/R" outputs (headphones get silence) and checks
// that the tone arrives in the shared-memory bus read by AmpCast.exe.
//
//   asio_smoke.exe path\to\AmpCastAsio64.dll
#include "../../src/AmpCast.Asio/src/asio_abi.h"
#include "../../src/AmpCast.Asio/src/SharedBus.h"

#include <objbase.h>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <vector>

namespace {

IASIO* g_asio = nullptr;
std::vector<ASIOBufferInfo> g_buffers;
std::vector<ASIOSampleType> g_types;
long g_bufferSize = 0;
long g_hwOutputs = 0;
double g_phase = 0;
double g_rate = 48000;
std::atomic<long> g_callbacks{ 0 };

void WriteSample(void* buffer, ASIOSampleType type, long i, double v)
{
    switch (type) {
    case ASIOSTFloat32LSB: static_cast<float*>(buffer)[i] = static_cast<float>(v); break;
    case ASIOSTFloat64LSB: static_cast<double*>(buffer)[i] = v; break;
    case ASIOSTInt32LSB:   static_cast<int32_t*>(buffer)[i] = static_cast<int32_t>(v * 2147483647.0); break;
    case ASIOSTInt16LSB:   static_cast<int16_t*>(buffer)[i] = static_cast<int16_t>(v * 32767.0); break;
    case ASIOSTInt24LSB: {
        const int32_t s = static_cast<int32_t>(v * 8388607.0);
        uint8_t* p = static_cast<uint8_t*>(buffer) + i * 3;
        p[0] = uint8_t(s); p[1] = uint8_t(s >> 8); p[2] = uint8_t(s >> 16);
        break;
    }
    default: break;
    }
}

void __cdecl OnBufferSwitch(long index, ASIOBool)
{
    const double step = 2.0 * 3.14159265358979 * 440.0 / g_rate;
    for (size_t c = 0; c < g_buffers.size(); ++c) {
        const ASIOBufferInfo& b = g_buffers[c];
        if (b.isInput) continue;
        const bool virtualOut = b.channelNum >= g_hwOutputs;
        double phase = g_phase;
        for (long i = 0; i < g_bufferSize; ++i) {
            WriteSample(b.buffers[index], g_types[c], i, virtualOut ? 0.1 * std::sin(phase) : 0.0);
            phase += step;
        }
    }
    g_phase = std::fmod(g_phase + step * g_bufferSize, 2.0 * 3.14159265358979);
    ++g_callbacks;
}

ASIOTime* __cdecl OnBufferSwitchTimeInfo(ASIOTime* t, long index, ASIOBool direct)
{
    OnBufferSwitch(index, direct);
    return t;
}

void __cdecl OnRateChange(ASIOSampleRate) {}

long __cdecl OnMessage(long selector, long value, void*, double*)
{
    switch (selector) {
    case kAsioSelectorSupported:
        return value == kAsioEngineVersion || value == kAsioResetRequest ? 1 : 0;
    case kAsioEngineVersion:
        return 2;
    case kAsioResetRequest:
        std::printf("  (driver requested reset)\n");
        return 1;
    default:
        return 0;
    }
}

int Fail(const char* what)
{
    char msg[128] = {};
    if (g_asio) g_asio->getErrorMessage(msg);
    std::printf("FAIL: %s %s\n", what, msg);
    return 1;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2) { std::printf("usage: asio_smoke <AmpCastAsio64.dll>\n"); return 2; }
    CoInitialize(nullptr);

    HMODULE dll = LoadLibraryW(argv[1]);
    if (!dll) return Fail("LoadLibrary");
    using GetClassObject = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);
    auto getClassObject = reinterpret_cast<GetClassObject>(GetProcAddress(dll, "DllGetClassObject"));
    if (!getClassObject) return Fail("DllGetClassObject export missing");

    const CLSID clsid = { 0x7c3b1a52, 0x4e9d, 0x4f7b, { 0x9a, 0x1e, 0xac, 0x0a, 0x5c, 0xa5, 0x70, 0x01 } };
    IClassFactory* factory = nullptr;
    if (FAILED(getClassObject(clsid, IID_IClassFactory, reinterpret_cast<void**>(&factory)))) return Fail("class factory");
    if (FAILED(factory->CreateInstance(nullptr, clsid, reinterpret_cast<void**>(&g_asio)))) return Fail("CreateInstance");
    factory->Release();

    if (!g_asio->init(GetConsoleWindow())) return Fail("init:");

    char name[32] = {};
    g_asio->getDriverName(name);
    long ins = 0, outs = 0, minB = 0, maxB = 0, pref = 0, gran = 0;
    g_asio->getChannels(&ins, &outs);
    g_asio->getBufferSize(&minB, &maxB, &pref, &gran);
    g_asio->getSampleRate(&g_rate);
    g_hwOutputs = outs - 2;
    std::printf("driver: %s | in %ld, out %ld (incl. 2 virtual) | buffer %ld (min %ld, max %ld) | %.0f Hz\n",
                name, ins, outs, pref, minB, maxB, g_rate);
    std::printf("canSampleRate 44100: %ld (expect -995 = locked to 48k)\n", g_asio->canSampleRate(44100.0));

    for (long c = 0; c < outs; ++c) {
        ASIOChannelInfo ci{}; ci.channel = c; ci.isInput = ASIOFalse;
        g_asio->getChannelInfo(&ci);
        if (c < 2 || c >= g_hwOutputs) std::printf("  out %ld: %s (type %ld)\n", c, ci.name, ci.type);
    }

    // inputs 1-2, hardware outputs 1-2, virtual Discord L/R
    for (long c = 0; c < ins && c < 2; ++c) g_buffers.push_back({ ASIOTrue, c, { nullptr, nullptr } });
    for (long c = 0; c < g_hwOutputs && c < 2; ++c) g_buffers.push_back({ ASIOFalse, c, { nullptr, nullptr } });
    g_buffers.push_back({ ASIOFalse, g_hwOutputs, { nullptr, nullptr } });
    g_buffers.push_back({ ASIOFalse, g_hwOutputs + 1, { nullptr, nullptr } });
    for (const auto& b : g_buffers) {
        ASIOChannelInfo ci{}; ci.channel = b.channelNum; ci.isInput = b.isInput;
        g_asio->getChannelInfo(&ci);
        g_types.push_back(ci.type);
    }

    static ASIOCallbacks callbacks{ &OnBufferSwitch, &OnRateChange, &OnMessage, &OnBufferSwitchTimeInfo };
    g_bufferSize = pref;
    if (g_asio->createBuffers(g_buffers.data(), static_cast<long>(g_buffers.size()), pref, &callbacks) != ASE_OK)
        return Fail("createBuffers:");

    HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, ampcast::kBusName);
    auto* hdr = map ? static_cast<ampcast::BusHeader*>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, ampcast::kBusTotalSize)) : nullptr;
    const LONG64 startPos = hdr ? hdr->writePos : 0;

    if (g_asio->start() != ASE_OK) return Fail("start:");
    Sleep(3000);
    g_asio->stop();

    int result = 0;
    std::printf("callbacks in 3 s: %ld (expect ~%.0f)\n", g_callbacks.load(), 3.0 * g_rate / pref);
    if (g_callbacks.load() < 10) result = Fail("hardware is not calling back");

    if (!hdr) {
        result = Fail("shared bus not found");
    } else {
        const LONG64 written = hdr->writePos - startPos;
        const float* data = reinterpret_cast<const float*>(reinterpret_cast<const uint8_t*>(hdr) + ampcast::kBusHeaderSize);
        float peak = 0;
        for (LONG64 f = hdr->writePos - 4800; f < hdr->writePos; ++f) {
            const uint32_t i = static_cast<uint32_t>(f) & (ampcast::kBusCapacityFrames - 1);
            peak = std::fmax(peak, std::fabs(data[i * 2]));
        }
        std::printf("bus: magic %08X, %.0f Hz, buffer %d, host %ls, frames written %lld, tone peak %.3f (expect ~0.100)\n",
                    hdr->magic, hdr->sampleRate, hdr->bufferSize, hdr->hostName, written, peak);
        if (written < g_rate) result = Fail("bus did not receive audio");
        else if (std::fabs(peak - 0.1f) > 0.01f) result = Fail("tone level wrong (sample conversion?)");
    }

    g_asio->disposeBuffers();
    g_asio->Release();
    std::printf(result == 0 ? "PASS\n" : "\n");
    CoUninitialize();
    return result;
}
