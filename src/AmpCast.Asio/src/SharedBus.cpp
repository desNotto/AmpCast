#include "SharedBus.h"

#include <sddl.h>
#include <string.h>
#include <wchar.h>

namespace ampcast {

namespace {

// Everyone in this logon session may open the object, and the low-integrity
// label lets a non-elevated AmpCast talk to an elevated DAW and vice versa.
constexpr wchar_t kSddl[] = L"D:(A;;GA;;;WD)(A;;GA;;;SY)S:(ML;;NW;;;LW)";

bool IsProcessAlive(DWORD pid)
{
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h)
        return GetLastError() == ERROR_ACCESS_DENIED;   // exists, just not ours
    DWORD code = 0;
    const BOOL ok = GetExitCodeProcess(h, &code);
    CloseHandle(h);
    return ok && code == STILL_ACTIVE;
}

} // namespace

bool SharedBus::Open()
{
    if (m_header)
        return true;

    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, FALSE };
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(kSddl, SDDL_REVISION_1, &sd, nullptr))
        sa.lpSecurityDescriptor = sd;

    m_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, sa.lpSecurityDescriptor ? &sa : nullptr,
                                   PAGE_READWRITE, 0, static_cast<DWORD>(kBusTotalSize), kBusName);
    if (sd)
        LocalFree(sd);
    if (!m_mapping)
        return false;

    void* view = MapViewOfFile(m_mapping, FILE_MAP_ALL_ACCESS, 0, 0, kBusTotalSize);
    if (!view) {
        CloseHandle(m_mapping);
        m_mapping = nullptr;
        return false;
    }

    m_header = static_cast<BusHeader*>(view);
    m_data   = reinterpret_cast<float*>(static_cast<uint8_t*>(view) + kBusHeaderSize);

    if (m_header->magic != kBusMagic || m_header->version != kBusVersion) {
        // First user of the mapping (or an old layout): initialise the header.
        // writePos is kept monotonic for the lifetime of the mapping.
        m_header->channels       = kBusChannels;
        m_header->capacityFrames = kBusCapacityFrames;
        m_header->version        = kBusVersion;
        MemoryBarrier();
        m_header->magic          = kBusMagic;
    }
    return true;
}

void SharedBus::Close()
{
    Release();
    if (m_header) {
        UnmapViewOfFile(m_header);
        m_header = nullptr;
        m_data   = nullptr;
    }
    if (m_mapping) {
        CloseHandle(m_mapping);
        m_mapping = nullptr;
    }
}

bool SharedBus::TryAcquire()
{
    if (!m_header)
        return false;
    if (m_owner)
        return true;

    const LONG me   = static_cast<LONG>(GetCurrentProcessId());
    const LONG prev = InterlockedCompareExchange(&m_header->ownerPid, me, 0);
    if (prev == 0 || prev == me) {
        m_owner = true;
    } else if (!IsProcessAlive(static_cast<DWORD>(prev))) {
        // The previous DAW crashed without releasing the bus: take it over.
        m_owner = InterlockedCompareExchange(&m_header->ownerPid, me, prev) == prev;
    }

    if (m_owner) {
        wchar_t path[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        const wchar_t* name = wcsrchr(path, L'\\');
        wcsncpy_s(m_header->hostName, name ? name + 1 : path, _TRUNCATE);
    }
    return m_owner;
}

void SharedBus::Release()
{
    if (!m_header || !m_owner)
        return;
    InterlockedExchange(&m_header->streaming, 0);
    InterlockedCompareExchange(&m_header->ownerPid, 0, static_cast<LONG>(GetCurrentProcessId()));
    m_owner = false;
}

void SharedBus::SetFormat(double sampleRate, long bufferSize)
{
    if (!m_header || !m_owner)
        return;
    m_header->sampleRate = sampleRate;
    m_header->bufferSize = static_cast<int32_t>(bufferSize);
    MemoryBarrier();
}

void SharedBus::SetStreaming(bool streaming)
{
    if (!m_header || !m_owner)
        return;
    InterlockedExchange(&m_header->streaming, streaming ? 1 : 0);
}

void SharedBus::Write(const float* src, uint32_t frames)
{
    if (!m_owner || !m_header || frames == 0)
        return;
    if (frames > kBusCapacityFrames)
        frames = kBusCapacityFrames;

    const LONG64   pos   = m_header->writePos;            // we are the only writer
    const uint32_t mask  = kBusCapacityFrames - 1;
    const uint32_t index = static_cast<uint32_t>(pos) & mask;
    const uint32_t first = (frames < kBusCapacityFrames - index) ? frames : (kBusCapacityFrames - index);

    memcpy(m_data + size_t(index) * kBusChannels, src, size_t(first) * kBusChannels * sizeof(float));
    if (first < frames)
        memcpy(m_data, src + size_t(first) * kBusChannels, size_t(frames - first) * kBusChannels * sizeof(float));

    // Full barrier: the samples become visible before the new write position.
    InterlockedExchange64(&m_header->writePos, pos + frames);
    InterlockedExchange64(&m_header->heartbeatMs, static_cast<LONG64>(GetTickCount64()));
}

} // namespace ampcast
