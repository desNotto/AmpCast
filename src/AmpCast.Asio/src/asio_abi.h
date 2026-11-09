// AmpCast ASIO — binary-compatible declarations of the ASIO 2 driver interface.
//
// ASIO is a trademark of Steinberg Media Technologies GmbH. This header only
// re-declares the ABI (struct layouts, constants and the COM vtable order)
// that every ASIO host and driver on Windows already agrees on, so the
// project builds without shipping SDK sources. Before a public release,
// read the Steinberg ASIO SDK licence terms for drivers.
#pragma once

#include <windows.h>
#include <unknwn.h>

// The SDK packs every ASIO struct to 4 bytes on MSVC. Hosts are compiled
// that way, so we must match it exactly (matters for ASIOTime on x86/x64).
#pragma pack(push, 4)

typedef long   ASIOBool;
typedef long   ASIOError;
typedef long   ASIOSampleType;
typedef double ASIOSampleRate;   // IEEE754_64FLOAT == 1 on Windows

struct ASIOSamples   { unsigned long hi; unsigned long lo; };   // NATIVE_INT64 == 0 on Windows
struct ASIOTimeStamp { unsigned long hi; unsigned long lo; };

enum : long { ASIOFalse = 0, ASIOTrue = 1 };

enum : long {
    ASE_OK               = 0,
    ASE_SUCCESS          = 0x3f4847a0,
    ASE_NotPresent       = -1000,
    ASE_HWMalfunction    = -999,
    ASE_InvalidParameter = -998,
    ASE_InvalidMode      = -997,
    ASE_SPNotAdvancing   = -996,
    ASE_NoClock          = -995,
    ASE_NoMemory         = -994
};

enum : long {
    ASIOSTInt16MSB   = 0,  ASIOSTInt24MSB   = 1,  ASIOSTInt32MSB   = 2,
    ASIOSTFloat32MSB = 3,  ASIOSTFloat64MSB = 4,
    ASIOSTInt32MSB16 = 8,  ASIOSTInt32MSB18 = 9,  ASIOSTInt32MSB20 = 10, ASIOSTInt32MSB24 = 11,
    ASIOSTInt16LSB   = 16, ASIOSTInt24LSB   = 17, ASIOSTInt32LSB   = 18,
    ASIOSTFloat32LSB = 19, ASIOSTFloat64LSB = 20,
    ASIOSTInt32LSB16 = 24, ASIOSTInt32LSB18 = 25, ASIOSTInt32LSB20 = 26, ASIOSTInt32LSB24 = 27
};

// Selectors for ASIOCallbacks::asioMessage
enum : long {
    kAsioSelectorSupported = 1,
    kAsioEngineVersion,
    kAsioResetRequest,
    kAsioBufferSizeChange,
    kAsioResyncRequest,
    kAsioLatenciesChanged,
    kAsioSupportsTimeInfo,
    kAsioSupportsTimeCode,
    kAsioMMCCommand,
    kAsioSupportsInputMonitor,
    kAsioSupportsInputGain,
    kAsioSupportsInputMeter,
    kAsioSupportsOutputGain,
    kAsioSupportsOutputMeter,
    kAsioOverload
};

struct ASIOClockSource {
    long     index;
    long     associatedChannel;
    long     associatedGroup;
    ASIOBool isCurrentSource;
    char     name[32];
};

struct ASIOChannelInfo {
    long           channel;
    ASIOBool       isInput;
    ASIOBool       isActive;
    long           channelGroup;
    ASIOSampleType type;
    char           name[32];
};

struct ASIOBufferInfo {
    ASIOBool isInput;
    long     channelNum;
    void*    buffers[2];
};

struct AsioTimeInfo {
    double         speed;
    ASIOTimeStamp  systemTime;
    ASIOSamples    samplePosition;
    ASIOSampleRate sampleRate;
    unsigned long  flags;
    char           reserved[12];
};

struct ASIOTimeCode {
    double        speed;
    ASIOSamples   timeCodeSamples;
    unsigned long flags;
    char          future[64];
};

struct ASIOTime {
    long         reserved[4];
    AsioTimeInfo timeInfo;
    ASIOTimeCode timeCode;
};

struct ASIOCallbacks {
    void      (__cdecl* bufferSwitch)(long doubleBufferIndex, ASIOBool directProcess);
    void      (__cdecl* sampleRateDidChange)(ASIOSampleRate sRate);
    long      (__cdecl* asioMessage)(long selector, long value, void* message, double* opt);
    ASIOTime* (__cdecl* bufferSwitchTimeInfo)(ASIOTime* params, long doubleBufferIndex, ASIOBool directProcess);
};

#pragma pack(pop)

// IASIO: note the methods have NO explicit calling convention. On x86 this
// means __thiscall (not __stdcall like normal COM) — that is how every ASIO
// host calls drivers, so it must stay exactly like this.
struct IASIO : public IUnknown {
    virtual ASIOBool  init(void* sysHandle) = 0;
    virtual void      getDriverName(char* name) = 0;
    virtual long      getDriverVersion() = 0;
    virtual void      getErrorMessage(char* string) = 0;
    virtual ASIOError start() = 0;
    virtual ASIOError stop() = 0;
    virtual ASIOError getChannels(long* numInputChannels, long* numOutputChannels) = 0;
    virtual ASIOError getLatencies(long* inputLatency, long* outputLatency) = 0;
    virtual ASIOError getBufferSize(long* minSize, long* maxSize, long* preferredSize, long* granularity) = 0;
    virtual ASIOError canSampleRate(ASIOSampleRate sampleRate) = 0;
    virtual ASIOError getSampleRate(ASIOSampleRate* sampleRate) = 0;
    virtual ASIOError setSampleRate(ASIOSampleRate sampleRate) = 0;
    virtual ASIOError getClockSources(ASIOClockSource* clocks, long* numSources) = 0;
    virtual ASIOError setClockSource(long reference) = 0;
    virtual ASIOError getSamplePosition(ASIOSamples* sPos, ASIOTimeStamp* tStamp) = 0;
    virtual ASIOError getChannelInfo(ASIOChannelInfo* info) = 0;
    virtual ASIOError createBuffers(ASIOBufferInfo* bufferInfos, long numChannels, long bufferSize, ASIOCallbacks* callbacks) = 0;
    virtual ASIOError disposeBuffers() = 0;
    virtual ASIOError controlPanel() = 0;
    virtual ASIOError future(long selector, void* opt) = 0;
    virtual ASIOError outputReady() = 0;
};
