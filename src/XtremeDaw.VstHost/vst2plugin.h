// One VST2 plugin inside wtvsthost: what Host (main.cpp) needs from it, so the shared memory, the commands and the
// window stay the same for both formats. A parameter's id is its index. One process hosts one plugin, so the host
// callback finds it through Vst2Plugin::current (a plugin calls back before its Effect exists).
#pragma once
#include <windows.h>

#include "shared.h"
#include "vst2.h"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

/// What the plugin tells the host, from any thread.
class Vst2Listener
{
public:
    virtual void Vst2Edited() = 0;
    virtual void Vst2Touched(int32_t index) = 0;
    virtual void Vst2Resized(int width, int height) = 0;
};

class Vst2Plugin
{
public:
    /// Host's saved-state kinds: the plugin's own chunk, or its program and parameters.
    static constexpr uint8_t StateChunk = 1, StateParams = 2;
    /// Longer than the format allows: plenty of plugins write past its limits.
    static constexpr int NameBuffer = 256;

    static inline Vst2Plugin* current = nullptr;

    vst2::Effect* effect = nullptr;
    Vst2Listener* listener = nullptr;
    /// A shell (Waves) makes the class with this id; 0 = the first or only one.
    uint32_t wantedId = 0;
    /// Taken while the state loads: the audio thread skips that block instead of racing the plugin.
    std::mutex stateLock;
    std::atomic<bool> latencyChanged {false};
    vst2::TimeInfo time {};

    bool Open(const std::wstring& path, std::string& error)
    {
        current = this;
        if (!module) module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!module)
        {
            error = "no pude abrir el archivo del plugin (" + std::to_string(GetLastError()) + ")";
            return false;
        }
        auto entry = reinterpret_cast<vst2::EntryFn>(GetProcAddress(module, "VSTPluginMain"));
        if (!entry) entry = reinterpret_cast<vst2::EntryFn>(GetProcAddress(module, "main"));
        if (!entry) entry = reinterpret_cast<vst2::EntryFn>(GetProcAddress(module, "MAIN"));
        if (!entry) { error = "el archivo no es un plugin VST2"; return false; }
        effect = entry(&Vst2Plugin::OnHost);
        if (!effect || effect->magic != vst2::Magic) { effect = nullptr; error = "el archivo no es un plugin VST2"; return false; }
        Do(vst2::OpOpen);
        return true;
    }

    bool Start(int32_t sampleRate, int32_t maxBlock, bool isOffline, std::string& error)
    {
        rate = sampleRate;
        block = maxBlock;
        offline = isOffline;
        if (Category() == vst2::CategoryShell) { error = "no encontré el plugin dentro del archivo"; return false; }
        if (effect->numOutputs < 1) { error = "el plugin no tiene salida de audio"; return false; }
        in.assign(effect->numInputs, std::vector<float>(gcvst::MaxBlock));
        out.assign(effect->numOutputs, std::vector<float>(gcvst::MaxBlock));
        for (auto& c : in) inPtr.push_back(c.data());
        for (auto& c : out) outPtr.push_back(c.data());
        for (int i = 0; i < MaxEvents; i++) eventList.events[i] = &eventData[i];
        time.sampleRate = sampleRate;
        Do(vst2::OpSetSampleRate, 0, 0, nullptr, static_cast<float>(sampleRate));
        Do(vst2::OpSetBlockSize, 0, maxBlock);
        Resume();
        return true;
    }

    void Close()
    {
        if (!effect) return;
        Suspend();
        Do(vst2::OpClose);
        // the library stays loaded: the process is about to end, and plenty of plugins crash on the way out
        effect = nullptr;
    }

    /// The plugin forgets its tails and delay lines.
    void Reset()
    {
        Suspend();
        Resume();
    }

    bool IsSynth() { return effect->flags & vst2::IsSynth || Category() == vst2::CategorySynth; }
    bool TakesNotes()
    {
        return IsSynth() || Do(vst2::OpCanDo, 0, 0, const_cast<char*>("receiveVstMidiEvent")) > 0
            || Do(vst2::OpCanDo, 0, 0, const_cast<char*>("receiveVstEvents")) > 0;
    }
    intptr_t Category() { return Do(vst2::OpGetPlugCategory); }

    std::string Name()
    {
        std::string s = Text(vst2::OpGetEffectName);
        return s.empty() ? Text(vst2::OpGetProductString) : s;
    }
    std::string Vendor() { return Text(vst2::OpGetVendorString); }
    int32_t Version()
    {
        auto v = static_cast<int32_t>(Do(vst2::OpGetVendorVersion));
        return v != 0 ? v : effect->version;
    }

    /// A shell's next class: its id, 0 when there are no more.
    int32_t NextInShell(std::string& name)
    {
        char buffer[NameBuffer] {};
        auto id = static_cast<int32_t>(Do(vst2::OpShellNextPlugin, 0, 0, buffer));
        name = Utf8(buffer);
        return id;
    }

    std::string ParamName(int32_t index)
    {
        char buffer[NameBuffer] {};
        Do(vst2::OpGetParamName, index, 0, buffer);
        return Utf8(buffer);
    }

    // --- audio thread ---

    void ClearNotes() { eventList.count = 0; }

    void AddNote(bool on, int32_t offset, int pitch, float velocity)
    {
        if (eventList.count >= MaxEvents) return;
        auto& e = eventData[eventList.count++];
        e = {};
        e.type = vst2::MidiEventType;
        e.byteSize = sizeof(vst2::MidiEvent);
        e.deltaFrames = offset;
        e.data[0] = on ? 0x90 : 0x80;
        e.data[1] = static_cast<uint8_t>(pitch);
        e.data[2] = on ? static_cast<uint8_t>(std::clamp(static_cast<int>(velocity * 127 + 0.5f), 1, 127)) : 0;
    }

    /// One piece of a block; the notes added since <see cref="ClearNotes"/> go with it. A plugin with one output is heard on both sides.
    void Process(const float* inL, const float* inR, float* outL, float* outR, int32_t frames)
    {
        if (eventList.count > 0) Do(vst2::OpProcessEvents, 0, 0, &eventList);
        for (int32_t c = 0; c < effect->numInputs; c++)
        {
            if (c < 2) std::memcpy(inPtr[c], c == 0 ? inL : inR, frames * sizeof(float));
            else std::memset(inPtr[c], 0, frames * sizeof(float)); // a sidechain hears nothing
        }
        if (effect->flags & vst2::CanReplace && effect->processReplacing)
            effect->processReplacing(effect, inPtr.data(), outPtr.data(), frames);
        else
        {
            for (auto* c : outPtr) std::memset(c, 0, frames * sizeof(float));
            effect->process(effect, inPtr.data(), outPtr.data(), frames);
        }
        std::memcpy(outL, outPtr[0], frames * sizeof(float));
        std::memcpy(outR, outPtr[std::min(1, effect->numOutputs - 1)], frames * sizeof(float));
    }

    // --- state and window (the window's thread) ---

    std::vector<uint8_t> GetState()
    {
        std::vector<uint8_t> o;
        auto put = [&o](uint32_t v) { for (int i = 0; i < 4; i++) o.push_back(static_cast<uint8_t>(v >> (8 * i))); };
        if (effect->flags & vst2::ProgramChunks)
        {
            void* chunk = nullptr;
            intptr_t size = Do(vst2::OpGetChunk, 0, 0, &chunk);
            if (chunk && size > 0)
            {
                o.push_back(StateChunk);
                o.insert(o.end(), static_cast<uint8_t*>(chunk), static_cast<uint8_t*>(chunk) + size);
                return o;
            }
        }
        o.push_back(StateParams);
        put(static_cast<uint32_t>(Do(vst2::OpGetProgram)));
        put(static_cast<uint32_t>(effect->numParams));
        for (int32_t i = 0; i < effect->numParams; i++)
        {
            float v = effect->getParameter(effect, i);
            uint32_t bits;
            std::memcpy(&bits, &v, 4);
            put(bits);
        }
        return o;
    }

    bool SetState(const std::vector<uint8_t>& blob)
    {
        if (blob.empty()) return false;
        std::lock_guard<std::mutex> g(stateLock);
        if (blob[0] == StateChunk)
        {
            auto chunk = blob;
            Do(vst2::OpSetChunk, 0, static_cast<intptr_t>(chunk.size() - 1), chunk.data() + 1);
            return true;
        }
        size_t at = 1;
        auto take = [&blob, &at]() {
            uint32_t v = blob[at] | blob[at + 1] << 8 | blob[at + 2] << 16 | static_cast<uint32_t>(blob[at + 3]) << 24;
            at += 4;
            return v;
        };
        if (blob[0] != StateParams || blob.size() < 9) return false;
        uint32_t program = take(), count = take();
        if (blob.size() < 9 + static_cast<size_t>(count) * 4) return false;
        Do(vst2::OpSetProgram, 0, static_cast<intptr_t>(program));
        for (uint32_t i = 0; i < count; i++)
        {
            uint32_t bits = take();
            float v;
            std::memcpy(&v, &bits, 4);
            if (static_cast<int32_t>(i) < effect->numParams) effect->setParameter(effect, static_cast<int32_t>(i), v);
        }
        return true;
    }

    bool HasEditor() const { return effect->flags & vst2::HasEditor; }
    bool EditorOpen() const { return editorOpen; }

    void OpenEditor(HWND parent)
    {
        Do(vst2::OpEditOpen, 0, 0, parent);
        editorOpen = true;
    }

    void CloseEditor()
    {
        if (!editorOpen) return;
        Do(vst2::OpEditClose);
        editorOpen = false;
    }

    void EditorSize(int& width, int& height)
    {
        vst2::Rect* r = nullptr;
        Do(vst2::OpEditGetRect, 0, 0, &r);
        width = r ? r->right - r->left : 0;
        height = r ? r->bottom - r->top : 0;
    }

    /// What the window's thread owes the plugin every few milliseconds: older editors only draw from this.
    void Idle()
    {
        if (editorOpen) Do(vst2::OpEditIdle);
        if (needsIdle) Do(vst2::OpIdle);
    }

private:
    static constexpr int MaxEvents = gcvst::MaxEvents + 128;

    HMODULE module = nullptr;
    int32_t rate = 48000, block = gcvst::MaxBlock;
    bool offline = false, editorOpen = false, running = false;
    std::atomic<bool> needsIdle {false};
    std::vector<std::vector<float>> in, out;
    std::vector<float*> inPtr, outPtr;
    vst2::MidiEvent eventData[MaxEvents] {};
    vst2::Events<MaxEvents> eventList {};

    intptr_t Do(int32_t op, int32_t index = 0, intptr_t value = 0, void* ptr = nullptr, float opt = 0)
    {
        return effect->dispatcher(effect, op, index, value, ptr, opt);
    }

    void Resume()
    {
        Do(vst2::OpMainsChanged, 0, 1);
        Do(vst2::OpStartProcess);
        running = true;
    }

    void Suspend()
    {
        if (!running) return;
        Do(vst2::OpStopProcess);
        Do(vst2::OpMainsChanged, 0, 0);
        running = false;
    }

    std::string Text(int32_t op)
    {
        char buffer[NameBuffer] {};
        Do(op, 0, 0, buffer);
        return Utf8(buffer);
    }

    /// VST2 strings are in the system's code page.
    static std::string Utf8(const char* s)
    {
        int n = MultiByteToWideChar(CP_ACP, 0, s, -1, nullptr, 0);
        std::wstring w(n > 0 ? n - 1 : 0, L'\0');
        if (n > 1) MultiByteToWideChar(CP_ACP, 0, s, -1, w.data(), n);
        n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string o(n > 0 ? n - 1 : 0, '\0');
        if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, o.data(), n, nullptr, nullptr);
        return o;
    }

    static bool CanDo(const char* what)
    {
        for (auto* yes : {"sendVstEvents", "sendVstMidiEvent", "sendVstTimeInfo", "sizeWindow", "supplyIdle", "startStopProcess"})
            if (std::strcmp(what, yes) == 0) return true;
        return false;
    }

    static intptr_t __cdecl OnHost(vst2::Effect* e, int32_t op, int32_t index, intptr_t value, void* ptr, float)
    {
        auto* self = current;
        switch (op)
        {
        case vst2::HostAutomate:
            if (self && self->listener) self->listener->Vst2Edited();
            return 0;
        case vst2::HostBeginEdit:
            if (self && self->listener) self->listener->Vst2Touched(index);
            return 1;
        case vst2::HostEndEdit:
            if (self && self->listener) self->listener->Vst2Edited();
            return 1;
        case vst2::HostVersion: return 2400;
        case vst2::HostCurrentId: return self && self->wantedId ? static_cast<int32_t>(self->wantedId) : e ? e->uniqueId : 0;
        case vst2::HostIdle: return 0;
        case vst2::HostGetTime: return self ? reinterpret_cast<intptr_t>(&self->time) : 0;
        case vst2::HostIoChanged:
            if (self) self->latencyChanged = true;
            return 1;
        case vst2::HostNeedIdle:
            if (self) self->needsIdle = true;
            return 1;
        case vst2::HostSizeWindow:
            if (self && self->listener) self->listener->Vst2Resized(index, static_cast<int>(value));
            return 1;
        case vst2::HostGetSampleRate: return self ? self->rate : 48000;
        case vst2::HostGetBlockSize: return self ? self->block : gcvst::MaxBlock;
        case vst2::HostGetProcessLevel: return self && self->offline ? vst2::LevelOffline : vst2::LevelRealtime;
        case vst2::HostGetVendorString:
            if (ptr) strncpy_s(static_cast<char*>(ptr), 64, "XTreme", _TRUNCATE);
            return 1;
        case vst2::HostGetProductString:
            if (ptr) strncpy_s(static_cast<char*>(ptr), 64, "XTreme", _TRUNCATE);
            return 1;
        case vst2::HostGetVendorVersion: return 1000;
        case vst2::HostCanDo: return ptr && CanDo(static_cast<const char*>(ptr)) ? 1 : 0;
        case vst2::HostUpdateDisplay: return 1;
        default: return 0;
        }
    }
};
