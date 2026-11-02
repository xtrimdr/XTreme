// The binary interface of VST2 plugins, as far as a host needs it: the structures and numbers a compiled plugin
// expects to find in memory, written here by hand because Steinberg no longer licenses its VST2 SDK (the user's call,
// 2026-11-02). Nothing here comes from that SDK. The names are this project's.
#pragma once
#include <cstdint>

namespace vst2 {

constexpr int32_t Magic = 0x56737450;
constexpr int32_t MidiEventType = 1;

// what the host asks the plugin
enum Op : int32_t
{
    OpOpen = 0, OpClose = 1, OpSetProgram = 2, OpGetProgram = 3, OpGetParamName = 8, OpSetSampleRate = 10, OpSetBlockSize = 11,
    OpMainsChanged = 12, OpEditGetRect = 13, OpEditOpen = 14, OpEditClose = 15, OpEditIdle = 19, OpGetChunk = 23, OpSetChunk = 24,
    OpProcessEvents = 25, OpGetPlugCategory = 35, OpGetEffectName = 45, OpGetVendorString = 47, OpGetProductString = 48,
    OpGetVendorVersion = 49, OpCanDo = 51, OpIdle = 53, OpShellNextPlugin = 70, OpStartProcess = 71, OpStopProcess = 72,
};

// what the plugin asks the host
enum HostOp : int32_t
{
    HostAutomate = 0, HostVersion = 1, HostCurrentId = 2, HostIdle = 3, HostGetTime = 7, HostIoChanged = 13, HostNeedIdle = 14,
    HostSizeWindow = 15, HostGetSampleRate = 16, HostGetBlockSize = 17, HostGetProcessLevel = 23, HostGetVendorString = 32,
    HostGetProductString = 33, HostGetVendorVersion = 34, HostCanDo = 37, HostUpdateDisplay = 42, HostBeginEdit = 43, HostEndEdit = 44,
};

enum Flags : int32_t { HasEditor = 1, CanReplace = 1 << 4, ProgramChunks = 1 << 5, IsSynth = 1 << 8 };
enum Category : int32_t { CategorySynth = 2, CategoryShell = 10 };
enum ProcessLevel : int32_t { LevelUser = 1, LevelRealtime = 2, LevelOffline = 4 };
enum TimeFlags : int32_t { TimePlaying = 1 << 1, TimePpqValid = 1 << 9, TimeTempoValid = 1 << 10, TimeBarsValid = 1 << 11, TimeSigValid = 1 << 13 };

struct Effect;
using HostCallback = intptr_t(__cdecl*)(Effect*, int32_t op, int32_t index, intptr_t value, void* ptr, float opt);
using Dispatcher = intptr_t(__cdecl*)(Effect*, int32_t op, int32_t index, intptr_t value, void* ptr, float opt);
using ProcessFn = void(__cdecl*)(Effect*, float** in, float** out, int32_t frames);
using SetParamFn = void(__cdecl*)(Effect*, int32_t index, float value);
using GetParamFn = float(__cdecl*)(Effect*, int32_t index);
using EntryFn = Effect*(__cdecl*)(HostCallback);

#pragma pack(push, 8)
struct Effect
{
    int32_t magic;
    Dispatcher dispatcher;
    ProcessFn process;        // adds to its output: only plugins older than processReplacing need it
    SetParamFn setParameter;
    GetParamFn getParameter;
    int32_t numPrograms, numParams, numInputs, numOutputs, flags;
    intptr_t reserved1, reserved2;
    int32_t initialDelay;     // its latency in frames
    int32_t realQualities, offQualities;
    float ioRatio;
    void* object;
    void* user;
    int32_t uniqueId, version;
    ProcessFn processReplacing;
    void* processDoubleReplacing;
    char future[56];
};

struct Rect { int16_t top, left, bottom, right; };

struct MidiEvent
{
    int32_t type, byteSize, deltaFrames, flags, noteLength, noteOffset;
    uint8_t data[4];
    int8_t detune;
    uint8_t noteOffVelocity, reserved1, reserved2;
};

template <int N>
struct Events
{
    int32_t count;
    intptr_t reserved;
    MidiEvent* events[N];
};

struct TimeInfo
{
    double samplePos, sampleRate, nanoSeconds, ppqPos, tempo, barStartPos, cycleStartPos, cycleEndPos;
    int32_t timeSigNumerator, timeSigDenominator, smpteOffset, smpteFrameRate, samplesToNextClock, flags;
};
#pragma pack(pop)

static_assert(sizeof(Effect) == (sizeof(void*) == 8 ? 192 : 144));
static_assert(sizeof(MidiEvent) == 32);
static_assert(sizeof(TimeInfo) == 88);

} // namespace vst2
