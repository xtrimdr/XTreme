// xtvsthost.exe: hosts one VST3 or VST2 plugin for XTreme in its own process, so a plugin that hangs or crashes never
// takes a recording down with it (the user's call). The app starts one per plugin instance. This same source builds
// xtvsthost32.exe, which the app starts for a 32-bit plugin; a .dll is a VST2 (vst2plugin.h), anything else a VST3.
//
//   xtvsthost --scan <file>
//       prints one JSON line per audio class of the file (with its main audio and event inputs, to tell instruments
//       from effects) and exits (scanning runs out of process too).
//   xtvsthost --host <file> --cid <class id> --shm <name> --sr 48000 [--block <frames>] [--offline] [--parent <pid>]
//       loads that class and processes blocks through the shared memory <name> (shared.h), woken by the event
//       <name>.req and answering on <name>.done. --block is the largest block the plugin is told about (the driver's):
//       plugins size their internals by it (the partitions of a convolution reverb, say), so telling it more than it
//       will get makes it spend more on every block; a larger block still works, in pieces. Commands come on stdin, one
//       per line:
//         state                 -> "state <base64>" (the component's and the controller's state)
//         load-state <base64>   -> "loaded" or "error <message>"
//         editor [owner hwnd]   opens (or brings up) the plugin's window
//         editor-close          closes it
//         look <panel> <raised> <fg> <dim> <accent> <line>   the window's colors, as rrggbb (the app's theme)
//         title <text>          the window's title
//         toolbar id=icon:text|-|id=:text...   the buttons of the bar above the plugin, "-" between groups; nothing sent = no bar
//         toolbar-track <rrggbb>|<track>|<plugin>   who it plays for, on the left of the bar (no color = the accent)
//         toolbar-state id=0 id=1...   which buttons are lit
//         keys-panel <height>   room at the bottom of the window for a panel of the app, at 100 % (this scales it to the
//                               window's DPI); 0 = none. It answers "editor-panel <hwnd>"
//         key-route plugin|app  where the window's keys go: all to the plugin, or all to the app (the default) but the
//                               modifiers and the system's (Alt+Tab, Alt+F4...)
//         snapshot <png>        waits for the open window to settle and saves the plugin's picture there; it answers
//                               "snapshot-done" or "snapshot-failed" (nothing but a blank window for 12 s)
//         snapshot-now <png>    the same, of what the window shows right now
//         stats                 -> "stats <blocks> <frames> <wall us> <worst wall us> <cpu us>" since the last stats
//         params                -> "param <id> <value> <steps> <title>" per automatable parameter, then "params-end"
//         quit
//       and it says on stdout: "ready <latency> <outputs> <instrument>", "error <message>", "dirty" (a parameter
//       changed in its window), "touched <id>" (the parameter grabbed in its window), "latency <frames>" (the plugin
//       changed its latency), "editor-open", "editor-closed",
//       "editor-rect <left> <top> <right> <bottom>" (where its window is now; no numbers = minimized), "editor-active",
//       "editor-inactive" (it took or lost the keyboard), "toolbar <id>" (a button of the bar was clicked),
//       "editor-panel <hwnd>" (where the app puts its panel, as a child window), "app-key <vk> <mods> <scan>" (with
//       key-route app, any key that went down; mods: 1 Ctrl, 2 Shift, 4 Alt; scan 0 for an extended key).
//   It exits when stdin closes or the app (--parent) goes away.

#include <windows.h>
#include <dwmapi.h>
#include <shellapi.h>

#include "shared.h"
#include "vst2plugin.h"

#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <cstdio>
#include <iostream>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

// gdiplus.h wants min and max, which NOMINMAX takes away
namespace Gdiplus { using std::min; using std::max; }
#include <gdiplus.h>

#pragma comment(lib, "dwmapi")
#pragma comment(lib, "gdi32")
#pragma comment(lib, "gdiplus")
#pragma comment(lib, "shell32")

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

// --- output to the app: whole lines, flushed, from any thread --------------------------------------------------------

std::mutex outLock;

void Say(const std::string& line)
{
    std::lock_guard<std::mutex> g(outLock);
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

std::string Utf8(const wchar_t* w)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring Wide(const std::string& s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    std::wstring w(n > 0 ? n - 1 : 0, L'\0');
    if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), n);
    return w;
}

std::string Json(const std::string& s)
{
    std::string o = "\"";
    for (unsigned char c : s)
    {
        if (c == '"' || c == '\\') { o += '\\'; o += static_cast<char>(c); }
        else if (c < 0x20) { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", c); o += b; }
        else o += static_cast<char>(c);
    }
    return o + "\"";
}

// --- base64, for the plugin's state on the command line ---------------------------------------------------------------

const char* B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

std::string ToBase64(const std::vector<uint8_t>& d)
{
    std::string o;
    o.reserve((d.size() + 2) / 3 * 4);
    for (size_t i = 0; i < d.size(); i += 3)
    {
        uint32_t v = d[i] << 16 | (i + 1 < d.size() ? d[i + 1] << 8 : 0) | (i + 2 < d.size() ? d[i + 2] : 0);
        o += B64[v >> 18 & 63];
        o += B64[v >> 12 & 63];
        o += i + 1 < d.size() ? B64[v >> 6 & 63] : '=';
        o += i + 2 < d.size() ? B64[v & 63] : '=';
    }
    return o;
}

std::vector<uint8_t> FromBase64(const std::string& s)
{
    std::vector<uint8_t> o;
    uint32_t v = 0;
    int bits = 0;
    for (char c : s)
    {
        const char* p = std::strchr(B64, c);
        if (c == '=' || !p || !c) continue;
        v = v << 6 | static_cast<uint32_t>(p - B64);
        bits += 6;
        if (bits >= 8) { bits -= 8; o.push_back(static_cast<uint8_t>(v >> bits & 0xff)); }
    }
    return o;
}

// --- scan --------------------------------------------------------------------------------------------------------------

/// What a class takes, to tell an instrument from an effect without trusting its sub-categories alone: its main audio
/// inputs (a sidechain doesn't count) and its event (note) inputs, from a component made just to ask. Empty if it
/// can't be made: the app then goes by the sub-categories.
std::string Buses(const VST3::Hosting::PluginFactory& factory, const VST3::Hosting::ClassInfo& info, FUnknown* context)
{
    auto component = factory.createInstance<IComponent>(info.ID());
    if (!component || component->initialize(context) != kResultOk) return "";
    int32 mainIns = 0;
    for (int32 i = 0, n = component->getBusCount(kAudio, kInput); i < n; i++)
    {
        BusInfo bus {};
        if (component->getBusInfo(kAudio, kInput, i, bus) == kResultOk && bus.busType == kMain) mainIns++;
    }
    int32 eventIns = component->getBusCount(kEvent, kInput);
    component->terminate();
    return ",\"ins\":" + std::to_string(mainIns) + ",\"events\":" + std::to_string(eventIns);
}

bool IsVst2(const std::string& path)
{
    return path.size() > 4 && _stricmp(path.c_str() + path.size() - 4, ".dll") == 0;
}

/// One VST2 class's line. Its id is the shell's, when it comes from one.
void SayVst2(Vst2Plugin& p, int32_t id, const std::string& shellName)
{
    char cid[16];
    std::snprintf(cid, sizeof cid, "%08X", static_cast<uint32_t>(id));
    bool notes = p.TakesNotes();
    Say("{\"cid\":" + Json(cid) + ",\"name\":" + Json(shellName.empty() ? p.Name() : shellName) + ",\"vendor\":" + Json(p.Vendor()) +
        ",\"sub\":" + Json(p.IsSynth() ? "Instrument" : "Fx") + ",\"version\":" + Json(std::to_string(p.Version())) +
        ",\"ins\":" + std::to_string(p.effect->numInputs > 0 ? 1 : 0) + ",\"events\":" + std::to_string(notes ? 1 : 0) + "}");
}

int ScanVst2(const std::string& path)
{
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Vst2Plugin plugin;
    std::string error;
    if (!plugin.Open(Wide(path), error)) { Say("error " + error); return 1; }
    if (plugin.Category() != vst2::CategoryShell)
    {
        SayVst2(plugin, plugin.effect->uniqueId, "");
        return 0;
    }
    // a shell holds many plugins: each one is asked for by its id
    std::vector<std::pair<int32_t, std::string>> classes;
    std::string name;
    while (int32_t id = plugin.NextInShell(name)) classes.emplace_back(id, name);
    for (auto& [id, className] : classes)
    {
        Vst2Plugin one;
        one.wantedId = static_cast<uint32_t>(id);
        if (one.Open(Wide(path), error)) SayVst2(one, id, className);
    }
    return 0;
}

int Scan(const std::string& path)
{
    if (IsVst2(path)) return ScanVst2(path);
    std::string error;
    auto module = VST3::Hosting::Module::create(path, error);
    if (!module) { Say("error " + error); return 1; }
    static HostApplication hostApp;
    PluginContextFactory::instance().setPluginContext(&hostApp);
    auto factory = module->getFactory();
    factory.setHostContext(&hostApp);
    std::string factoryVendor = factory.info().vendor();
    for (auto& info : factory.classInfos())
    {
        if (info.category() != kVstAudioEffectClass) continue;
        std::string vendor = info.vendor().empty() ? factoryVendor : info.vendor();
        Say("{\"cid\":" + Json(info.ID().toString(false)) + ",\"name\":" + Json(info.name()) + ",\"vendor\":" + Json(vendor) +
            ",\"sub\":" + Json(info.subCategoriesString()) + ",\"version\":" + Json(info.version()) + Buses(factory, info, &hostApp) + "}");
    }
    PluginContextFactory::instance().setPluginContext(nullptr);
    return 0;
}

// --- host ---------------------------------------------------------------------------------------------------------------

constexpr UINT WM_COMMAND_LINE = WM_APP + 1;

/// What the plugin's window changes reaches the processor through here (performEdit -> the next process call), and
/// the app hears "dirty" so it saves the new state.
class ComponentHandler : public IComponentHandler
{
public:
    std::mutex lock;
    std::vector<std::pair<ParamID, ParamValue>> pending;
    /// Lanes' values the plugin's window hasn't shown yet: its knobs follow them (same lock).
    std::vector<std::pair<ParamID, ParamValue>> toShow;
    std::atomic<ULONGLONG> lastDirty {0};
    /// The plugin said its latency changed: read again before the next block.
    std::atomic<bool> latencyChanged {false};

    tresult PLUGIN_API beginEdit(ParamID id) override
    {
        // every gesture, also on the same parameter: the app's listening button waits for the next one
        Say("touched " + std::to_string(id));
        return kResultOk;
    }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override
    {
        {
            std::lock_guard<std::mutex> g(lock);
            pending.emplace_back(id, value);
        }
        Dirty();
        return kResultOk;
    }
    tresult PLUGIN_API endEdit(ParamID) override { Dirty(); return kResultOk; }
    tresult PLUGIN_API restartComponent(int32 flags) override
    {
        if (flags & kLatencyChanged) latencyChanged = true;
        Dirty();
        return kResultOk;
    }

    /// At most one "dirty" every half second: a knob being turned sends hundreds of edits.
    void Dirty()
    {
        ULONGLONG now = GetTickCount64();
        if (now - lastDirty.load() < 500) return;
        lastDirty = now;
        Say("dirty");
    }

    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual(iid, IComponentHandler::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
        {
            *obj = this;
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

/// Where the keys of the plugin's window go: every one to the plugin, or every one to the app (the default), but the
/// modifiers and the system's. The user picks it on the bar, for every plugin window at once.
std::atomic<bool> keysToPlugin {false};

/// Keys that stay where Windows sends them even when every key goes to the app: the modifiers (both sides read them as
/// they are) and the system's own (the Windows key, Alt+Tab, Alt+F4, Alt+Esc, Ctrl+Esc).
bool SystemKey(int vk, int mods)
{
    if (vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || (vk >= VK_LSHIFT && vk <= VK_RMENU) || vk == VK_LWIN || vk == VK_RWIN) return true;
    if (GetAsyncKeyState(VK_LWIN) < 0 || GetAsyncKeyState(VK_RWIN) < 0) return true;
    return ((mods & 4) && (vk == VK_TAB || vk == VK_F4 || vk == VK_ESCAPE)) || ((mods & 1) && vk == VK_ESCAPE);
}

/// "app-key <vk> <mods> <scan>": a key of the plugin's window for the app (scan 0 for an extended key).
void SayAppKey(int vk, int mods, int scan)
{
    Say("app-key " + std::to_string(vk) + " " + std::to_string(mods) + " " + std::to_string(scan));
}

/// The app's keys while the keyboard is in a window of another process inside the plugin's, or of a thread the plugin
/// made: a WebView2 or CEF interface (or a plugin's own UI thread) has its own message loop, so those keys never come
/// through this one and Host::TakeKey can't see them. Only a low-level hook can, and it has to answer at once whatever
/// the plugin's window is doing, so it gets a thread of its own, for as long as the window is open.
class ForeignKeys
{
public:
    /// The app's own process: its panel lives in the window, and the keys it gets are already the app's.
    DWORD parent = 0;

    void Start(HWND editor)
    {
        window = editor;
        if (hooker.joinable()) return;
        hooker = std::thread(RunHook);
    }

    void Stop()
    {
        window = nullptr;
        if (!hooker.joinable()) return;
        // it fails until the thread has its queue
        while (!PostThreadMessageW(GetThreadId(hooker.native_handle()), WM_QUIT, 0, 0)) Sleep(1);
        hooker.join();
    }

private:
    std::atomic<HWND> window {nullptr};
    std::thread hooker;
    /// Taken on the way down, so the release and the repeats stay away from the plugin too. Only the hook's thread.
    bool held[256] = {};

    /// The keyboard is in the plugin's window, on a thread whose keys skip Host::TakeKey (another process, or a thread of
    /// the plugin's own in this one), not the app's.
    bool InForeignWindow() const
    {
        HWND w = window;
        if (!w || GetForegroundWindow() != w) return false;
        DWORD main = GetWindowThreadProcessId(w, nullptr);
        GUITHREADINFO gui {sizeof gui};
        if (!GetGUIThreadInfo(main, &gui) || !gui.hwndFocus) return false;
        DWORD process = 0, thread = GetWindowThreadProcessId(gui.hwndFocus, &process);
        return thread != main && process != parent;
    }

    bool Take(WPARAM message, const KBDLLHOOKSTRUCT& key)
    {
        if (key.vkCode >= 256) return false;
        if (message == WM_KEYUP || message == WM_SYSKEYUP)
        {
            bool taken = held[key.vkCode];
            held[key.vkCode] = false;
            return taken;
        }
        if (held[key.vkCode]) return true;
        if (keysToPlugin) return false;
        int mods = (GetAsyncKeyState(VK_CONTROL) < 0 ? 1 : 0) | (GetAsyncKeyState(VK_SHIFT) < 0 ? 2 : 0) | (GetAsyncKeyState(VK_MENU) < 0 ? 4 : 0);
        if (SystemKey(static_cast<int>(key.vkCode), mods) || !InForeignWindow()) return false;
        SayAppKey(static_cast<int>(key.vkCode), mods, key.flags & LLKHF_EXTENDED ? 0 : static_cast<int>(key.scanCode));
        held[key.vkCode] = true;
        return true;
    }

    static LRESULT CALLBACK OnKey(int code, WPARAM message, LPARAM data);

    static void RunHook()
    {
        HHOOK hook = SetWindowsHookExW(WH_KEYBOARD_LL, OnKey, GetModuleHandleW(nullptr), 0);
        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0) > 0) DispatchMessageW(&msg);
        if (hook) UnhookWindowsHookEx(hook);
    }
};

ForeignKeys foreignKeys;

LRESULT CALLBACK ForeignKeys::OnKey(int code, WPARAM message, LPARAM data)
{
    if (code == HC_ACTION && foreignKeys.Take(message, *reinterpret_cast<const KBDLLHOOKSTRUCT*>(data))) return 1;
    return CallNextHookEx(nullptr, code, message, data);
}

struct Host;

/// The timer of the wait for a picture, and how often it looks.
constexpr UINT_PTR ShotTimer = 2;
constexpr UINT ShotEveryMs = 500;
/// 12 s at most, and never before 2 s: a loading screen can stand still for a moment too.
constexpr int ShotMaxLooks = 24, ShotMinLooks = 4;
/// The picture's width: enough for the app's largest view of it.
constexpr int ShotMaxWidth = 640;
/// Fewer colors than this is a window still empty (a real interface has at least the edges of its letters).
constexpr size_t ShotMinColors = 16;
/// PW_RENDERFULLCONTENT, which older SDK headers don't have.
constexpr UINT ShotFlags = 2;

/// What the plugin's window showed once, and whether there is something on it.
struct Shot
{
    int width = 0, height = 0;
    std::vector<uint32_t> pixels;
    uint64_t hash = 0;
    size_t colors = 0;

    bool Usable() const { return colors >= ShotMinColors; }

    void Measure()
    {
        constexpr int Step = 7; // a sample is enough to count colors; the hash reads every pixel
        std::vector<uint32_t> seen;
        hash = 14695981039346656037ull;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint32_t rgb = pixels[static_cast<size_t>(y) * width + x] & 0xffffff;
                hash = (hash ^ rgb) * 1099511628211ull;
                if (x % Step == 0 && y % Step == 0) seen.push_back(rgb);
            }
        std::sort(seen.begin(), seen.end());
        colors = std::unique(seen.begin(), seen.end()) - seen.begin();
    }
};

/// A button of the bar above the plugin.
struct ToolButton
{
    std::string id;
    std::wstring icon, text;
    RECT rect {};
    bool on = false;
};

/// The plugin's window: a top-level window of this process (owned by XTreme's, so it stays above it), with the
/// app's colors and icon, a bar the app fills on top, and the plugin's own view in a child window under the bar.
class Editor : public IPlugFrame
{
public:
    static constexpr int BarHeight = 46;

    Host* host = nullptr;
    HWND hwnd = nullptr, plug = nullptr, panelHost = nullptr;
    /// The room for the app's panel at the bottom of the window, at 100 %.
    int panelUnits = 0;
    /// The window's DPI: this process is per-monitor aware, so everything here is in real pixels.
    int dpi = USER_DEFAULT_SCREEN_DPI;
    IPtr<IPlugView> view;
    std::vector<ToolButton> buttons;
    std::wstring title, trackName, pluginName;
    int hover = -1;
    /// The picture being waited for (Host::StartSnapshot): where it goes, the looks left and what the last ones saw.
    std::wstring shotFile;
    int shotLooks = 0;
    uint64_t shotHash = 0;
    Shot shotLast;
    COLORREF panel = RGB(0x13, 0x13, 0x16), raised = RGB(0x1c, 0x1c, 0x20), fg = RGB(0xec, 0xec, 0xef), dim = RGB(0xa1, 0xa1, 0xaa),
        accent = RGB(0x22, 0xd3, 0xee), line = RGB(0x27, 0x27, 0x2c), trackColor = accent;

    int Scaled(int units) const { return MulDiv(units, dpi, USER_DEFAULT_SCREEN_DPI); }
    int Bar() const { return buttons.empty() ? 0 : Scaled(BarHeight); }
    int Panel() const { return Scaled(panelUnits); }

    /// The window's outer size for a plugin of that size, with the bar and the panel.
    SIZE WindowFor(int width, int height, DWORD style, DWORD exStyle) const
    {
        RECT wr {0, 0, width, height + Bar() + Panel()};
        AdjustWindowRectExForDpi(&wr, style, FALSE, exStyle, dpi);
        return {wr.right - wr.left, wr.bottom - wr.top};
    }

    tresult PLUGIN_API resizeView(IPlugView* v, ViewRect* r) override
    {
        if (!hwnd || !r) return kInvalidArgument;
        auto size = WindowFor(r->getWidth(), r->getHeight(), GetWindowLong(hwnd, GWL_STYLE), GetWindowLong(hwnd, GWL_EXSTYLE));
        SetWindowPos(hwnd, nullptr, 0, 0, size.cx, size.cy, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        v->onSize(r);
        return kResultOk;
    }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual(iid, IPlugFrame::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
        {
            *obj = this;
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

// the bar above the plugin, in pixels at 100 % and with the app's token for a mute that is on
constexpr int BarMargin = 12, ChipHeight = 30, ChipGap = 4, GroupGap = 17, LabelGap = 10, IconSize = 15, MinIdentity = 200;
constexpr float ChipRadius = 6;
constexpr COLORREF Amber = RGB(0xf5, 0x9e, 0x0b);

struct Host : Vst2Listener
{
    /// The plugin, when it is a VST2: then nothing of VST3 below is set.
    std::unique_ptr<Vst2Plugin> vst2;
    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IEditController> controller;
    FUnknownPtr<IAudioProcessor> processor;
    std::string name;

    gcvst::Shared* shared = nullptr;
    HANDLE request = nullptr, done = nullptr;
    HostProcessData data;
    EventList events {gcvst::MaxEvents + 128};
    static constexpr int32 MaxQueues = 256;
    ParameterChanges params {MaxQueues};
    /// VST3 has no MIDI controllers: a plugin maps each to a parameter of its own (IMidiMapping). One entry per Control event's controller.
    ParamID midiParam[gcvst::ControlBend + 1] {};
    bool midiMapped[gcvst::ControlBend + 1] {};
    ProcessContext context {};
    ComponentHandler handler;
    Editor editor;
    bool active[128] {};
    std::atomic<bool> quit {false};
    bool debug = GetEnvironmentVariableW(L"GCVST_DEBUG", nullptr, 0) > 0;
    int64_t blocks = 0;
    DWORD mainThread = 0;
    /// The largest block the plugin was told about: a larger one goes to it in pieces.
    int32 setupBlock = gcvst::MaxBlock;
    // what the audio thread spends, for the "stats" command: per block, on the clock and on the CPU
    std::atomic<int64_t> statBlocks {0}, statFrames {0}, statWall {0}, statWorst {0}, statCycles {0};
    HWND messages = nullptr;

    bool LoadVst2(const std::string& path, const std::string& cid, int32 sampleRate, bool offline, int32 maxBlock)
    {
        vst2 = std::make_unique<Vst2Plugin>();
        vst2->wantedId = static_cast<uint32_t>(std::strtoul(cid.c_str(), nullptr, 16));
        vst2->listener = this;
        setupBlock = std::clamp(maxBlock, 1, gcvst::MaxBlock);
        std::string error;
        if (!vst2->Open(Wide(path), error) || !vst2->Start(sampleRate, setupBlock, offline, error)) return Fail(error);
        name = vst2->Name();
        int outputs = vst2->effect->numOutputs, instrument = vst2->TakesNotes() ? 1 : 0;
        shared->latency = vst2->effect->initialDelay;
        shared->outputs = outputs;
        shared->instrument = instrument;
        shared->status = gcvst::Ready;
        SetTimer(messages, Vst2IdleTimer, Vst2IdleMs, nullptr);
        Say("ready " + std::to_string(shared->latency) + " " + std::to_string(outputs) + " " + std::to_string(instrument));
        return true;
    }

    void Vst2Edited() override { handler.Dirty(); }

    void Vst2Touched(int32_t index) override { Say("touched " + std::to_string(index)); }

    void Vst2Resized(int width, int height) override
    {
        if (editor.hwnd) FitEditor(width, height);
    }

    bool Load(const std::string& path, const std::string& cid, int32 sampleRate, bool offline, int32 maxBlock)
    {
        if (IsVst2(path)) return LoadVst2(path, cid, sampleRate, offline, maxBlock);
        std::string error;
        module = VST3::Hosting::Module::create(path, error);
        if (!module) return Fail(error.empty() ? "no pude abrir el archivo del plugin" : error);
        auto uid = VST3::UID::fromString(cid, false);
        auto factory = module->getFactory();
        for (auto& info : factory.classInfos())
        {
            if (info.category() != kVstAudioEffectClass || (uid && info.ID() != *uid)) continue;
            provider = owned(new PlugProvider(factory, info, true));
            name = info.name();
            break;
        }
        if (!provider || !provider->initialize()) return Fail("no pude crear el plugin");
        component = provider->getComponentPtr();
        controller = provider->getControllerPtr();
        processor = FUnknownPtr<IAudioProcessor>(component);
        if (!component || !processor) return Fail("el plugin no procesa audio");

        // the main stereo output (and input, for an effect) and the first event input, if it takes notes
        int32 audioIns = component->getBusCount(kAudio, kInput), audioOuts = component->getBusCount(kAudio, kOutput);
        int32 eventIns = component->getBusCount(kEvent, kInput);
        if (audioOuts < 1) return Fail("el plugin no tiene salida de audio");
        std::vector<SpeakerArrangement> ins(audioIns, SpeakerArr::kStereo), outs(audioOuts, SpeakerArr::kStereo);
        processor->setBusArrangements(ins.empty() ? nullptr : ins.data(), audioIns, outs.data(), audioOuts);
        component->activateBus(kAudio, kOutput, 0, true);
        if (audioIns > 0) component->activateBus(kAudio, kInput, 0, true);
        if (eventIns > 0) component->activateBus(kEvent, kInput, 0, true);

        setupBlock = std::clamp(maxBlock, 1, gcvst::MaxBlock);
        ProcessSetup setup {offline ? kOffline : kRealtime, kSample32, setupBlock, static_cast<SampleRate>(sampleRate)};
        if (processor->setupProcessing(setup) != kResultOk) return Fail("el plugin no acepta 48 kHz en 32 bits");
        data.prepare(*component, gcvst::MaxBlock, kSample32);
        data.inputEvents = &events;
        data.inputParameterChanges = &params;
        data.processContext = &context;
        if (component->setActive(true) != kResultOk) return Fail("el plugin no se pudo activar");
        processor->setProcessing(true);
        if (controller) controller->setComponentHandler(&handler);
        MapControllers();

        BusInfo bus {};
        component->getBusInfo(kAudio, kOutput, 0, bus);
        if (debug)
            for (int32 i = 0; i < audioOuts; i++)
            {
                BusInfo b {};
                component->getBusInfo(kAudio, kOutput, i, b);
                std::fprintf(stderr, "out bus %d: %d ch, type %d, flags %u\n", i, b.channelCount, b.busType, b.flags);
            }
        shared->latency = static_cast<int32_t>(processor->getLatencySamples());
        shared->outputs = bus.channelCount;
        shared->instrument = eventIns > 0 ? 1 : 0;
        shared->status = gcvst::Ready;
        context.sampleRate = sampleRate;
        Say("ready " + std::to_string(shared->latency) + " " + std::to_string(bus.channelCount) + " " + std::to_string(shared->instrument));
        return true;
    }

    /// Which parameter each controller moves, asked once. A plugin without a mapping ignores them. A parameter's queue
    /// keeps its memory when cleared: grown here to all a block can bring, the audio thread never has to.
    void MapControllers()
    {
        FUnknownPtr<IMidiMapping> mapping(controller);
        if (!mapping) return;
        bool any = false;
        for (int32 c = 0; c <= gcvst::ControlBend; c++)
        {
            CtrlNumber number = static_cast<CtrlNumber>(c == gcvst::ControlBend ? ControllerNumbers::kPitchBend : c);
            midiMapped[c] = mapping->getMidiControllerAssignment(0, 0, number, midiParam[c]) == kResultOk;
            any |= midiMapped[c];
        }
        if (!any) return;
        for (int32 q = 0; q < MaxQueues; q++)
        {
            int32 index = 0;
            auto* queue = params.addParameterData(static_cast<ParamID>(q), index);
            for (int32 i = 0; queue && i < gcvst::MaxEvents; i++) queue->addPoint(i, 0, index);
        }
        params.clearQueue();
    }

    bool Fail(const std::string& message)
    {
        if (shared)
        {
            strncpy_s(shared->error, message.c_str(), _TRUNCATE);
            shared->status = gcvst::Failed;
        }
        Say("error " + message);
        return false;
    }

    /// The audio thread: every request the app published, in order, as soon as it is signaled.
    void Run()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        while (!quit)
        {
            WaitForSingleObject(request, 100);
            while (!quit && shared->done < shared->requested)
            {
                int64_t seq = shared->done + 1;
                LARGE_INTEGER t0, t1;
                ULONG64 c0 = 0, c1 = 0;
                QueryPerformanceCounter(&t0);
                QueryThreadCycleTime(GetCurrentThread(), &c0);
                Process(shared->slots[seq & 1]);
                QueryThreadCycleTime(GetCurrentThread(), &c1);
                QueryPerformanceCounter(&t1);
                int64_t wall = t1.QuadPart - t0.QuadPart;
                statBlocks++;
                statFrames += shared->slots[seq & 1].frames;
                statWall += wall;
                statCycles += static_cast<int64_t>(c1 - c0);
                if (wall > statWorst.load()) statWorst = wall;
                InterlockedExchange64(&shared->done, seq);
                SetEvent(done);
            }
        }
    }

    /// A VST2's block, in the same order as <see cref="Process"/>. Its window already moves with setParameter.
    void ProcessVst2(gcvst::Slot& s)
    {
        int32 n = std::max(1, std::min<int32>(s.frames, gcvst::MaxBlock));
        std::unique_lock<std::mutex> gate(vst2->stateLock, std::try_to_lock);
        if (!gate.owns_lock())
        {
            std::memset(s.out[0], 0, n * sizeof(float));
            std::memset(s.out[1], 0, n * sizeof(float));
            return;
        }
        vst2->ClearNotes();
        if (s.flags & gcvst::Reset) vst2->Reset();
        if (vst2->latencyChanged.exchange(false) && vst2->effect->initialDelay != shared->latency)
        {
            shared->latency = vst2->effect->initialDelay;
            Say("latency " + std::to_string(shared->latency));
        }
        if (s.flags & gcvst::AllNotesOff)
            for (int p = 0; p < 128; p++)
                if (active[p]) { vst2->AddNote(false, 0, p, 0); active[p] = false; }
        for (int32 i = 0, automated = std::min(s.numParams, gcvst::MaxParams); i < automated; i++)
            if (static_cast<int32>(s.params[i].id) < vst2->effect->numParams)
                vst2->effect->setParameter(vst2->effect, static_cast<int32>(s.params[i].id), s.params[i].value);

        auto& t = vst2->time;
        t.tempo = s.tempo > 0 ? s.tempo : 120;
        t.timeSigNumerator = 4;
        t.timeSigDenominator = 4;
        t.flags = vst2::TimeTempoValid | vst2::TimePpqValid | vst2::TimeBarsValid | vst2::TimeSigValid | ((s.flags & gcvst::Playing) ? vst2::TimePlaying : 0);

        int32 count = std::min(s.numEvents, gcvst::MaxEvents), next = 0;
        for (int32 at = 0; at < n;)
        {
            int32 len = std::min(n - at, setupBlock);
            bool last = at + len >= n;
            for (; next < count && (last || s.events[next].offset < at + len); next++)
            {
                auto& e = s.events[next];
                if (e.type == gcvst::Control)
                {
                    if (e.pitch >= 0 && e.pitch <= gcvst::ControlBend) vst2->AddControl(std::clamp(e.offset - at, 0, len - 1), e.pitch, e.velocity);
                    continue;
                }
                if (e.pitch < 0 || e.pitch > 127) continue;
                bool on = e.type == gcvst::NoteOn;
                vst2->AddNote(on, std::clamp(e.offset - at, 0, len - 1), e.pitch, e.velocity);
                active[e.pitch] = on;
            }
            t.samplePos = static_cast<double>(s.timeSamples + at);
            t.ppqPos = t.samplePos / t.sampleRate * t.tempo / 60.0;
            t.barStartPos = std::floor(t.ppqPos / 4) * 4;
            vst2->Process(s.in[0] + at, s.in[1] + at, s.out[0] + at, s.out[1] + at, len);
            vst2->ClearNotes();
            at += len;
        }
    }

    void Process(gcvst::Slot& s)
    {
        if (vst2) { ProcessVst2(s); return; }
        int32 n = std::max(1, std::min<int32>(s.frames, gcvst::MaxBlock));
        events.clear();
        params.clearQueue();
        // the transport starts: an effect forgets its tails and delay lines (what VST3 hosts do to reset a processor)
        if (s.flags & gcvst::Reset)
        {
            processor->setProcessing(false);
            processor->setProcessing(true);
        }
        if (handler.latencyChanged.exchange(false))
        {
            int32_t latency = static_cast<int32_t>(processor->getLatencySamples());
            if (latency != shared->latency)
            {
                shared->latency = latency;
                Say("latency " + std::to_string(latency));
            }
        }
        // a jump or a stop: every note it holds goes off first
        if (s.flags & gcvst::AllNotesOff)
            for (int16 p = 0; p < 128; p++)
                if (active[p]) { AddNote(false, 0, p, 0); active[p] = false; }
        // what the plugin's window changed since the last block, without waiting for the window's thread
        if (handler.lock.try_lock())
        {
            for (auto& [id, value] : handler.pending)
            {
                int32 index = 0;
                if (auto* queue = params.addParameterData(id, index)) queue->addPoint(0, value, index);
            }
            handler.pending.clear();
            handler.lock.unlock();
        }
        // what the lanes moved: the processor hears it now, the window sees it soon
        int32 automated = std::min(s.numParams, gcvst::MaxParams);
        if (automated > 0)
        {
            bool show = handler.lock.try_lock();
            for (int32 i = 0; i < automated; i++)
            {
                auto& p = s.params[i];
                int32 index = 0;
                if (auto* queue = params.addParameterData(p.id, index)) queue->addPoint(0, p.value, index);
                if (show) handler.toShow.emplace_back(p.id, p.value);
            }
            if (show)
            {
                handler.lock.unlock();
                ULONGLONG now = GetTickCount64();
                if (editor.hwnd && now - lastShown >= ShowEveryMs)
                {
                    lastShown = now;
                    PostMessageW(messages, WM_COMMAND_LINE, 0, reinterpret_cast<LPARAM>(new std::string("show-params")));
                }
            }
        }
        context.state = ProcessContext::kTempoValid | ProcessContext::kProjectTimeMusicValid | ProcessContext::kTimeSigValid |
            ((s.flags & gcvst::Playing) ? ProcessContext::kPlaying : 0);
        context.tempo = s.tempo > 0 ? s.tempo : 120;
        context.timeSigNumerator = 4;
        context.timeSigDenominator = 4;

        // the events are in time order: each piece takes the ones that fall in it
        int32 count = std::min(s.numEvents, gcvst::MaxEvents), next = 0;
        for (int32 at = 0; at < n;)
        {
            int32 len = std::min(n - at, setupBlock);
            bool last = at + len >= n;
            for (; next < count && (last || s.events[next].offset < at + len); next++)
            {
                auto& e = s.events[next];
                if (e.type == gcvst::Control)
                {
                    if (e.pitch >= 0 && e.pitch <= gcvst::ControlBend && midiMapped[e.pitch])
                    {
                        int32 index = 0;
                        if (auto* queue = params.addParameterData(midiParam[e.pitch], index))
                            queue->addPoint(std::clamp(e.offset - at, 0, len - 1), e.velocity, index);
                    }
                    continue;
                }
                if (e.pitch < 0 || e.pitch > 127) continue;
                bool on = e.type == gcvst::NoteOn;
                AddNote(on, std::clamp(e.offset - at, 0, len - 1), static_cast<int16>(e.pitch), e.velocity);
                active[e.pitch] = on;
            }
            context.projectTimeSamples = s.timeSamples + at;
            context.projectTimeMusic = context.projectTimeSamples / context.sampleRate * context.tempo / 60.0;
            // an effect gets the block's input
            if (data.numInputs > 0)
                for (int32 c = 0; c < data.inputs[0].numChannels; c++)
                    std::memcpy(data.inputs[0].channelBuffers32[c], s.in[std::min(c, 1)] + at, len * sizeof(float));
            data.numSamples = len;
            tresult result = processor->process(data);
            if (debug && (blocks++ % 50 == 0 || s.numEvents > 0))
            {
                float peak = 0;
                for (int32 c = 0; c < data.outputs[0].numChannels; c++)
                    for (int32 i = 0; i < len; i++) peak = std::max(peak, std::abs(data.outputs[0].channelBuffers32[c][i]));
                std::fprintf(stderr, "block %lld: %d frames, %d events, process %d, peak %g, silence %llx\n", (long long)s.seq, len, s.numEvents, result, peak,
                    (unsigned long long)data.outputs[0].silenceFlags);
            }
            // the main output to the app; a mono plugin is heard on both sides
            auto& out = data.outputs[0];
            for (int32 c = 0; c < 2; c++)
            {
                if (out.numChannels > 0) std::memcpy(s.out[c] + at, out.channelBuffers32[std::min(c, out.numChannels - 1)], len * sizeof(float));
                else std::memset(s.out[c] + at, 0, len * sizeof(float));
            }
            at += len;
            if (!last)
            {
                // what this piece carried is delivered: the next one starts empty
                events.clear();
                params.clearQueue();
            }
        }
    }

    /// How often the plugin's window follows its lanes.
    static constexpr ULONGLONG ShowEveryMs = 33;
    ULONGLONG lastShown = 0;

    /// What the lanes moved reaches the plugin's window: its controller is the window thread's, like everything in it.
    void ShowParams()
    {
        std::vector<std::pair<ParamID, ParamValue>> list;
        {
            std::lock_guard<std::mutex> g(handler.lock);
            list.swap(handler.toShow);
        }
        if (controller) for (auto& [id, value] : list) controller->setParamNormalized(id, value);
    }

    /// Lists what a host can automate: the plugin's own flags say which, and hidden ones stay hidden.
    void SayParams()
    {
        if (vst2)
            for (int32 i = 0; i < vst2->effect->numParams; i++)
            {
                char head[96];
                std::snprintf(head, sizeof head, "param %d %.6f 0 ", i, vst2->effect->getParameter(vst2->effect, i));
                Say(head + vst2->ParamName(i));
            }
        if (controller)
            for (int32 i = 0, n = controller->getParameterCount(); i < n; i++)
            {
                ParameterInfo p {};
                if (controller->getParameterInfo(i, p) != kResultOk) continue;
                if (!(p.flags & ParameterInfo::kCanAutomate) || (p.flags & ParameterInfo::kIsHidden)) continue;
                std::wstring title(reinterpret_cast<const wchar_t*>(p.title));
                char head[96];
                std::snprintf(head, sizeof head, "param %u %.6f %d ", static_cast<unsigned>(p.id), controller->getParamNormalized(p.id), p.stepCount);
                Say(head + Utf8(title.c_str()));
            }
        Say("params-end");
    }

    void AddNote(bool on, int32 offset, int16 pitch, float velocity)
    {
        Event e {};
        e.busIndex = 0;
        e.sampleOffset = offset;
        if (on)
        {
            e.type = Event::kNoteOnEvent;
            e.noteOn.channel = 0;
            e.noteOn.pitch = pitch;
            e.noteOn.velocity = velocity;
            e.noteOn.noteId = -1;
        }
        else
        {
            e.type = Event::kNoteOffEvent;
            e.noteOff.channel = 0;
            e.noteOff.pitch = pitch;
            e.noteOff.velocity = velocity;
            e.noteOff.noteId = -1;
        }
        events.addEvent(e);
    }

    /// The "stats" line.
    std::string Stats()
    {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        double us = 1e6 / f.QuadPart;
        int64_t b = statBlocks.exchange(0), n = statFrames.exchange(0), w = statWall.exchange(0), worst = statWorst.exchange(0), c = statCycles.exchange(0);
        return "stats " + std::to_string(b) + " " + std::to_string(n) + " " + std::to_string(static_cast<int64_t>(w * us)) + " " +
            std::to_string(static_cast<int64_t>(worst * us)) + " " + std::to_string(static_cast<int64_t>(c / CyclesPerMicrosecond()));
    }

    /// The speed of the thread cycle counter, measured once (it counts at the CPU's base clock, which Windows doesn't say).
    static double CyclesPerMicrosecond()
    {
        static double mhz = [] {
            ULONG64 c0 = 0, c1 = 0;
            LARGE_INTEGER f, t0, t1;
            QueryPerformanceFrequency(&f);
            QueryPerformanceCounter(&t0);
            QueryThreadCycleTime(GetCurrentThread(), &c0);
            do QueryPerformanceCounter(&t1); while ((t1.QuadPart - t0.QuadPart) * 50 < f.QuadPart);
            QueryThreadCycleTime(GetCurrentThread(), &c1);
            return (c1 - c0) * (f.QuadPart / 1e6) / (t1.QuadPart - t0.QuadPart);
        }();
        return mhz;
    }

    // --- state (the window's thread) ---

    std::string GetState()
    {
        if (vst2) return ToBase64(vst2->GetState());
        MemoryStream a, b;
        component->getState(&a);
        if (controller) controller->getState(&b);
        std::vector<uint8_t> blob;
        auto put = [&blob](MemoryStream& m) {
            uint32_t len = static_cast<uint32_t>(m.getSize());
            for (int i = 0; i < 4; i++) blob.push_back(static_cast<uint8_t>(len >> (8 * i)));
            blob.insert(blob.end(), m.getData(), m.getData() + len);
        };
        put(a);
        put(b);
        return ToBase64(blob);
    }

    bool SetState(const std::string& b64)
    {
        auto blob = FromBase64(b64);
        if (vst2) return vst2->SetState(blob);
        size_t at = 0;
        auto take = [&blob, &at](MemoryStream& m) {
            if (at + 4 > blob.size()) return false;
            uint32_t len = blob[at] | blob[at + 1] << 8 | blob[at + 2] << 16 | blob[at + 3] << 24;
            at += 4;
            if (at + len > blob.size()) return false;
            int32 written = 0;
            m.write(blob.data() + at, static_cast<int32>(len), &written);
            m.seek(0, IBStream::kIBSeekSet, nullptr);
            at += len;
            return true;
        };
        MemoryStream a, b;
        if (!take(a) || !take(b)) return false;
        if (component->setState(&a) != kResultOk) return false;
        if (controller)
        {
            a.seek(0, IBStream::kIBSeekSet, nullptr);
            controller->setComponentState(&a);
            if (b.getSize() > 0) controller->setState(&b);
        }
        return true;
    }

    // --- the plugin's window ---

    /// How often a VST2 gets its idle call, from the window's thread.
    static constexpr UINT_PTR Vst2IdleTimer = 1;
    static constexpr UINT Vst2IdleMs = 30;

    /// The window around a plugin of that size.
    void FitEditor(int width, int height)
    {
        auto size = editor.WindowFor(width, height, GetWindowLong(editor.hwnd, GWL_STYLE), GetWindowLong(editor.hwnd, GWL_EXSTYLE));
        SetWindowPos(editor.hwnd, nullptr, 0, 0, size.cx, size.cy, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// A VST2 only says its size for sure once it is open, and it can't be resized from here.
    void OpenEditorVst2(HWND owner)
    {
        if (!vst2->HasEditor()) { Say("error el plugin no tiene ventana propia"); return; }
        DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
        std::wstring title = !editor.title.empty() ? editor.title : Wide(name);
        editor.hwnd = CreateWindowExW(0, L"XtVstEditor", title.c_str(), style, CW_USEDEFAULT, CW_USEDEFAULT,
            640, 480, owner, nullptr, GetModuleHandleW(nullptr), this);
        editor.host = this;
        editor.dpi = GetDpiForWindow(editor.hwnd);
        DressWindow();
        editor.plug = CreateWindowExW(0, L"XtVstPlug", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, editor.Bar(), 640, 480,
            editor.hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (editor.panelUnits > 0) MakePanelHost();
        vst2->OpenEditor(editor.plug);
        int width = 0, height = 0;
        vst2->EditorSize(width, height);
        FitEditor(std::max(width, 100), std::max(height, 60));
        ShowWindow(editor.hwnd, SW_SHOWNORMAL);
        SetForegroundWindow(editor.hwnd);
        foreignKeys.Start(editor.hwnd);
        Say("editor-open");
    }

    void OpenEditor(HWND owner)
    {
        if (editor.hwnd)
        {
            ShowWindow(editor.hwnd, SW_SHOWNORMAL);
            SetForegroundWindow(editor.hwnd);
            return;
        }
        if (vst2) { OpenEditorVst2(owner); return; }
        if (!controller) { Say("error el plugin no tiene ventana"); return; }
        editor.view = owned(controller->createView(ViewType::kEditor));
        if (!editor.view || editor.view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue)
        {
            editor.view = nullptr;
            Say("error el plugin no tiene ventana propia");
            return;
        }
        bool resizable = editor.view->canResize() == kResultTrue;
        DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN | (resizable ? WS_THICKFRAME | WS_MAXIMIZEBOX : 0);
        std::wstring title = !editor.title.empty() ? editor.title : Wide(name);
        // the size comes after the window: what the plugin measures depends on the scale of the screen it landed on
        editor.hwnd = CreateWindowExW(0, L"XtVstEditor", title.c_str(), style, CW_USEDEFAULT, CW_USEDEFAULT,
            640, 480, owner, nullptr, GetModuleHandleW(nullptr), this);
        editor.host = this;
        editor.dpi = GetDpiForWindow(editor.hwnd);
        TellScale();
        ViewRect r {};
        editor.view->getSize(&r);
        int width = std::max<int32>(r.getWidth(), 100), height = std::max<int32>(r.getHeight(), 60);
        auto size = editor.WindowFor(width, height, style, 0);
        SetWindowPos(editor.hwnd, nullptr, 0, 0, size.cx, size.cy, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        DressWindow();
        editor.plug = CreateWindowExW(0, L"XtVstPlug", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, editor.Bar(), width, height,
            editor.hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (editor.panelUnits > 0) MakePanelHost();
        editor.view->setFrame(&editor);
        if (editor.view->attached(editor.plug, kPlatformTypeHWND) != kResultOk)
        {
            DestroyWindow(editor.hwnd);
            Say("error no pude abrir la ventana del plugin");
            return;
        }
        // some plugins only take the scale once they have a window; those answer with resizeView
        TellScale();
        ShowWindow(editor.hwnd, SW_SHOWNORMAL);
        SetForegroundWindow(editor.hwnd);
        foreignKeys.Start(editor.hwnd);
        Say("editor-open");
    }

    /// The screen's scale to the plugin, which then draws and measures in real pixels. One that doesn't take it keeps its size.
    void TellScale()
    {
        FUnknownPtr<IPlugViewContentScaleSupport> scale(editor.view);
        if (scale) scale->setContentScaleFactor(static_cast<float>(editor.dpi) / USER_DEFAULT_SCREEN_DPI);
    }

    /// The window went to a screen with another scale: the plugin rescales (or not) and the window fits it around.
    void DpiChanged(int dpi, const RECT& suggested)
    {
        editor.dpi = dpi;
        int width = 0, height = 0;
        if (vst2) vst2->EditorSize(width, height);
        else
        {
            TellScale();
            ViewRect r {};
            editor.view->getSize(&r);
            width = r.getWidth();
            height = r.getHeight();
        }
        auto size = editor.WindowFor(width, height, GetWindowLong(editor.hwnd, GWL_STYLE), GetWindowLong(editor.hwnd, GWL_EXSTYLE));
        SetWindowPos(editor.hwnd, nullptr, suggested.left, suggested.top, size.cx, size.cy, SWP_NOZORDER | SWP_NOACTIVATE);
        InvalidateRect(editor.hwnd, nullptr, FALSE);
    }

    void MakePanelHost()
    {
        RECT client {};
        GetClientRect(editor.hwnd, &client);
        int panel = editor.Panel();
        editor.panelHost = CreateWindowExW(0, L"XtVstPlug", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, client.bottom - panel,
            client.right, panel, editor.hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);
        Say("editor-panel " + std::to_string(reinterpret_cast<intptr_t>(editor.panelHost)));
    }

    /// The window grows or shrinks by the panel: the plugin keeps its size.
    void SetPanel(int height)
    {
        height = std::max(0, height);
        if (height == editor.panelUnits) return;
        if (!editor.hwnd)
        {
            editor.panelUnits = height;
            return;
        }
        if (height == 0 && editor.panelHost)
        {
            DestroyWindow(editor.panelHost);
            editor.panelHost = nullptr;
        }
        int grow = editor.Scaled(height) - editor.Panel();
        editor.panelUnits = height;
        RECT r {};
        GetWindowRect(editor.hwnd, &r);
        SetWindowPos(editor.hwnd, nullptr, 0, 0, r.right - r.left, r.bottom - r.top + grow, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (height > 0 && !editor.panelHost) MakePanelHost();
    }

    /// The title bar in the app's colors (Windows 11; older ones keep theirs) and the app's icon, from the exe next to this one.
    void DressWindow()
    {
        BOOL dark = TRUE;
        DwmSetWindowAttribute(editor.hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof dark);
        DwmSetWindowAttribute(editor.hwnd, DWMWA_CAPTION_COLOR, &editor.panel, sizeof editor.panel);
        DwmSetWindowAttribute(editor.hwnd, DWMWA_TEXT_COLOR, &editor.fg, sizeof editor.fg);
        DwmSetWindowAttribute(editor.hwnd, DWMWA_BORDER_COLOR, &editor.line, sizeof editor.line);
        wchar_t path[MAX_PATH];
        DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) return;
        std::wstring exe(path);
        exe = exe.substr(0, exe.find_last_of(L'\\') + 1) + L"XTreme.exe";
        HICON bigIcon = nullptr, smallIcon = nullptr;
        if (ExtractIconExW(exe.c_str(), 0, &bigIcon, &smallIcon, 1) == 0) return;
        if (bigIcon) SendMessageW(editor.hwnd, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(bigIcon));
        if (smallIcon) SendMessageW(editor.hwnd, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(smallIcon));
    }

    static COLORREF Color(const std::string& hex, COLORREF fallback)
    {
        if (hex.size() != 6) return fallback;
        unsigned long v = std::strtoul(hex.c_str(), nullptr, 16);
        return RGB(v >> 16 & 0xff, v >> 8 & 0xff, v & 0xff);
    }

    void SetLook(const std::string& arg)
    {
        std::vector<std::string> c;
        for (size_t at = 0; at < arg.size();)
        {
            size_t end = std::min(arg.find(' ', at), arg.size());
            c.push_back(arg.substr(at, end - at));
            at = end + 1;
        }
        if (c.size() != 6) return;
        editor.panel = Color(c[0], editor.panel);
        editor.raised = Color(c[1], editor.raised);
        editor.fg = Color(c[2], editor.fg);
        editor.dim = Color(c[3], editor.dim);
        editor.accent = Color(c[4], editor.accent);
        editor.line = Color(c[5], editor.line);
        if (editor.hwnd) DressWindow();
        RedrawBar();
    }

    void SetButtons(const std::string& arg)
    {
        if (editor.hwnd) return; // the bar's height is settled when the window opens
        editor.buttons.clear();
        for (size_t at = 0; at < arg.size();)
        {
            size_t end = std::min(arg.find('|', at), arg.size()), eq = arg.find('=', at), colon = arg.find(':', at);
            if (arg.compare(at, end - at, "-") == 0) editor.buttons.push_back({"-"});
            else if (eq < end && colon < end) editor.buttons.push_back({arg.substr(at, eq - at), Wide(arg.substr(eq + 1, colon - eq - 1)), Wide(arg.substr(colon + 1, end - colon - 1))});
            at = end + 1;
        }
    }

    void SetTrack(const std::string& arg)
    {
        size_t first = arg.find('|'), second = first == std::string::npos ? first : arg.find('|', first + 1);
        if (second == std::string::npos) return;
        editor.trackColor = Color(arg.substr(0, first), editor.accent);
        editor.trackName = Wide(arg.substr(first + 1, second - first - 1));
        editor.pluginName = Wide(arg.substr(second + 1));
        RedrawBar();
    }

    /// Taken on the way down, so the release stays away from the plugin too.
    bool keyHeld[256] = {};

    /// A key on its way to the plugin's window. True when the app gets it instead: every key but the system's, unless the
    /// user sends them to the plugin. The window is another process's, so the app's shortcuts stop there. Before the
    /// plugin's window, or a plugin that eats every key would keep them.
    bool TakeKey(const MSG& m)
    {
        bool down = m.message == WM_KEYDOWN || m.message == WM_SYSKEYDOWN, up = m.message == WM_KEYUP || m.message == WM_SYSKEYUP;
        if ((!down && !up) || m.wParam >= 256 || !editor.hwnd || (m.hwnd != editor.hwnd && !IsChild(editor.hwnd, m.hwnd))) return false;
        if (up)
        {
            bool taken = keyHeld[m.wParam];
            keyHeld[m.wParam] = false;
            return taken;
        }
        if (keysToPlugin) return false;
        int mods = (GetKeyState(VK_CONTROL) < 0 ? 1 : 0) | (GetKeyState(VK_SHIFT) < 0 ? 2 : 0) | (GetKeyState(VK_MENU) < 0 ? 4 : 0);
        bool repeat = (m.lParam & (1 << 30)) != 0, extended = (m.lParam & (1 << 24)) != 0;
        int scan = extended ? 0 : static_cast<int>((m.lParam >> 16) & 0xFF);
        if (SystemKey(static_cast<int>(m.wParam), mods)) return false;
        if (!repeat) SayAppKey(static_cast<int>(m.wParam), mods, scan);
        keyHeld[m.wParam] = true;
        return true;
    }

    void SetButtonStates(const std::string& arg)
    {
        for (auto& b : editor.buttons)
        {
            size_t at = arg.find(b.id + "=");
            if (at != std::string::npos && at + b.id.size() + 1 < arg.size()) b.on = arg[at + b.id.size() + 1] == '1';
        }
        RedrawBar();
    }

    void RedrawBar()
    {
        if (!editor.hwnd) return;
        RECT r {};
        GetClientRect(editor.hwnd, &r);
        r.bottom = editor.Bar();
        InvalidateRect(editor.hwnd, &r, FALSE);
    }

    static Gdiplus::Color Tint(COLORREF c, BYTE alpha = 255) { return Gdiplus::Color(alpha, GetRValue(c), GetGValue(c), GetBValue(c)); }

    static void Rounded(Gdiplus::GraphicsPath& path, const RECT& r, float radius)
    {
        float x = static_cast<float>(r.left), y = static_cast<float>(r.top), w = static_cast<float>(r.right - r.left - 1),
            h = static_cast<float>(r.bottom - r.top - 1), d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
    }

    /// A button's box, as the app's chips: outlined; lit, tinted with its color.
    static void DrawChip(Gdiplus::Graphics& g, const RECT& r, float radius, COLORREF border, BYTE borderAlpha, COLORREF fill, BYTE fillAlpha)
    {
        Gdiplus::GraphicsPath path;
        Rounded(path, r, radius);
        if (fillAlpha > 0)
        {
            Gdiplus::SolidBrush brush(Tint(fill, fillAlpha));
            g.FillPath(&brush, &path);
        }
        Gdiplus::Pen pen(Tint(border, borderAlpha));
        g.DrawPath(&pen, &path);
    }

    static HFONT Font(int size, int weight, const wchar_t* face) { return CreateFontW(-size, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, face); }

    /// Windows 11's icon font, or the one Windows 10 has with the same glyphs.
    static HFONT IconFont(HDC dc, int size)
    {
        HFONT font = Font(size, FW_NORMAL, L"Segoe Fluent Icons");
        auto old = SelectObject(dc, font);
        wchar_t face[LF_FACESIZE] {};
        GetTextFaceW(dc, LF_FACESIZE, face);
        SelectObject(dc, old);
        if (wcscmp(face, L"Segoe Fluent Icons") == 0) return font;
        DeleteObject(font);
        return Font(size, FW_NORMAL, L"Segoe MDL2 Assets");
    }

    static int TextWidth(HDC dc, HFONT font, const std::wstring& text)
    {
        auto old = SelectObject(dc, font);
        SIZE size {};
        GetTextExtentPoint32W(dc, text.c_str(), static_cast<int>(text.size()), &size);
        SelectObject(dc, old);
        return size.cx;
    }

    /// The bar, drawn off screen first so it never flickers: who it plays for on the left, in the track's color, and
    /// the buttons on the right, in groups. Short of room, the buttons keep only their icons.
    void PaintBar(HDC target, int width)
    {
        int height = editor.Bar();
        if (height == 0 || width <= 0) return;
        auto s = [this](int units) { return editor.Scaled(units); };
        int margin = s(BarMargin), chip = s(ChipHeight), chipGap = s(ChipGap), groupGap = s(GroupGap), labelGap = s(LabelGap);
        float radius = ChipRadius * editor.dpi / USER_DEFAULT_SCREEN_DPI;
        HDC dc = CreateCompatibleDC(target);
        HBITMAP bitmap = CreateCompatibleBitmap(target, width, height);
        auto oldBitmap = SelectObject(dc, bitmap);
        HFONT label = Font(s(12), FW_NORMAL, L"Segoe UI"), strong = Font(s(13), FW_SEMIBOLD, L"Segoe UI"), icons = IconFont(dc, s(IconSize));
        auto oldFont = SelectObject(dc, label);
        SetBkMode(dc, TRANSPARENT);
        RECT all {0, 0, width, height}, under {0, height - 1, width, height};
        HBRUSH back = CreateSolidBrush(editor.panel), lineBrush = CreateSolidBrush(editor.line);
        FillRect(dc, &all, back);
        FillRect(dc, &under, lineBrush);
        DeleteObject(back);
        DeleteObject(lineBrush);

        int total = 0, labels = 0;
        for (auto& b : editor.buttons)
        {
            if (b.id == "-") total += groupGap;
            else if (b.icon.empty()) total += chip + chipGap;
            else
            {
                total += chip + chipGap;
                labels += TextWidth(dc, label, b.text) + labelGap;
            }
        }
        bool room = width - total - labels >= s(MinIdentity);
        if (room) total += labels;

        Gdiplus::Graphics g(dc);
        g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        int top = (height - 1 - chip) / 2, x = width - margin - total + chipGap;
        for (size_t i = 0; i < editor.buttons.size(); i++)
        {
            auto& b = editor.buttons[i];
            if (b.id == "-")
            {
                RECT sep {x + (groupGap - chipGap) / 2, top + s(4), x + (groupGap - chipGap) / 2 + 1, top + chip - s(4)};
                Gdiplus::SolidBrush brush(Tint(editor.line));
                g.FillRectangle(&brush, static_cast<int>(sep.left), static_cast<int>(sep.top), 1, static_cast<int>(sep.bottom - sep.top));
                x += groupGap;
                b.rect = {};
                continue;
            }
            bool hover = static_cast<int>(i) == editor.hover, wide = room && !b.icon.empty();
            int w = chip + (wide ? TextWidth(dc, label, b.text) + labelGap : 0);
            b.rect = {x, top, x + w, top + chip};
            COLORREF lit = b.id == "mute" ? Amber : editor.accent;
            if (b.on) DrawChip(g, b.rect, radius, lit, 0x66, lit, 0x24);
            else DrawChip(g, b.rect, radius, editor.line, 255, editor.line, hover ? 255 : 0);
            COLORREF ink = b.on ? lit : hover ? editor.fg : editor.dim;
            SetTextColor(dc, ink);
            g.Flush(Gdiplus::FlushIntentionSync);
            RECT r = b.rect;
            if (b.icon.empty())
            {
                SelectObject(dc, strong);
                DrawTextW(dc, b.text.c_str(), -1, &r, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            }
            else
            {
                SelectObject(dc, icons);
                RECT ir {r.left, r.top, r.left + chip, r.bottom};
                DrawTextW(dc, b.icon.c_str(), -1, &ir, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                if (wide)
                {
                    SelectObject(dc, label);
                    RECT tr {r.left + chip - s(4), r.top, r.right - s(8), r.bottom};
                    DrawTextW(dc, b.text.c_str(), -1, &tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
                }
            }
            x += w + chipGap;
        }

        // who it plays for: the track's color, its name, and the plugin after it
        int limit = width - margin - total - s(12);
        Gdiplus::SolidBrush stripe(Tint(editor.trackColor));
        Gdiplus::GraphicsPath path;
        RECT bar {margin, top + s(3), margin + s(4), top + chip - s(3)};
        Rounded(path, bar, radius / 4);
        g.FillPath(&stripe, &path);
        g.Flush(Gdiplus::FlushIntentionSync);
        int text = margin + s(13);
        RECT name {text, 0, std::max(text, limit), height - 1};
        SelectObject(dc, strong);
        SetTextColor(dc, editor.fg);
        DrawTextW(dc, editor.trackName.c_str(), -1, &name, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        int after = text + TextWidth(dc, strong, editor.trackName) + s(10);
        if (after + s(40) < limit)
        {
            RECT plugin {after, 0, limit, height - 1};
            SelectObject(dc, label);
            SetTextColor(dc, editor.dim);
            DrawTextW(dc, editor.pluginName.c_str(), -1, &plugin, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
        }

        BitBlt(target, 0, 0, width, height, dc, 0, 0, SRCCOPY);
        SelectObject(dc, oldFont);
        SelectObject(dc, oldBitmap);
        DeleteObject(label);
        DeleteObject(strong);
        DeleteObject(icons);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }

    int ButtonAt(int x, int y)
    {
        POINT p {x, y};
        for (size_t i = 0; i < editor.buttons.size(); i++)
            if (PtInRect(&editor.buttons[i].rect, p)) return static_cast<int>(i);
        return -1;
    }

    void CloseEditor()
    {
        if (!editor.hwnd) return;
        DestroyWindow(editor.hwnd); // WM_DESTROY detaches the view
    }

    /// The plugin as the window shows it, without the bar or the panel. PW_RENDERFULLCONTENT is what brings what it draws
    /// with OpenGL, Direct3D or a browser: without it those come out black.
    Shot TakeShot()
    {
        Shot shot;
        RECT window {}, plug {};
        GetWindowRect(editor.hwnd, &window);
        GetWindowRect(editor.plug, &plug);
        int outerW = window.right - window.left, outerH = window.bottom - window.top;
        int left = std::max<int>(plug.left - window.left, 0), top = std::max<int>(plug.top - window.top, 0);
        int width = std::min<int>(plug.right - plug.left, outerW - left), height = std::min<int>(plug.bottom - plug.top, outerH - top);
        if (width <= 0 || height <= 0) return shot;
        BITMAPINFO info {};
        info.bmiHeader = {sizeof(BITMAPINFOHEADER), outerW, -outerH, 1, 32, BI_RGB};
        void* bits = nullptr;
        HDC dc = CreateCompatibleDC(nullptr);
        HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (bitmap)
        {
            auto old = SelectObject(dc, bitmap);
            if (PrintWindow(editor.hwnd, dc, ShotFlags))
            {
                shot.width = width;
                shot.height = height;
                shot.pixels.resize(static_cast<size_t>(width) * height);
                auto* from = static_cast<const uint32_t*>(bits);
                for (int y = 0; y < height; y++)
                    std::memcpy(&shot.pixels[static_cast<size_t>(y) * width], from + static_cast<size_t>(y + top) * outerW + left, width * sizeof(uint32_t));
                shot.Measure();
            }
            SelectObject(dc, old);
            DeleteObject(bitmap);
        }
        DeleteDC(dc);
        return shot;
    }

    /// To a temporary file first: the app may be reading the one that is there.
    bool SaveShot(const Shot& shot)
    {
        int width = std::min(shot.width, ShotMaxWidth), height = std::max(shot.height * width / shot.width, 1);
        Gdiplus::Bitmap source(shot.width, shot.height, shot.width * 4, PixelFormat32bppRGB, reinterpret_cast<BYTE*>(const_cast<uint32_t*>(shot.pixels.data())));
        Gdiplus::Bitmap fitted(width, height, PixelFormat24bppRGB);
        Gdiplus::Graphics g(&fitted);
        g.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);
        g.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHighQuality);
        g.DrawImage(&source, Gdiplus::Rect(0, 0, width, height));
        static const CLSID png {0x557cf406, 0x1a04, 0x11d3, {0x9a, 0x73, 0x00, 0x00, 0xf8, 0x1e, 0xf3, 0x2e}};
        std::wstring temporary = editor.shotFile + L".part";
        return fitted.Save(temporary.c_str(), &png) == Gdiplus::Ok && MoveFileExW(temporary.c_str(), editor.shotFile.c_str(), MOVEFILE_REPLACE_EXISTING);
    }

    void EndSnapshot(bool saved)
    {
        KillTimer(editor.hwnd, ShotTimer);
        editor.shotLooks = 0;
        editor.shotLast = {};
        Say(saved ? "snapshot-done" : "snapshot-failed");
    }

    /// Now, what is there; if not, it looks twice a second until two looks in a row match, past the time a plugin takes to
    /// leave its first screen. One that never stands still (meters) keeps moving: at the end the last one with something on it goes.
    void StartSnapshot(const std::string& file, bool now)
    {
        if (!editor.hwnd) { Say("snapshot-failed"); return; }
        editor.shotFile = Wide(file);
        if (now)
        {
            Shot shot = TakeShot();
            EndSnapshot(shot.Usable() && SaveShot(shot));
            return;
        }
        editor.shotLooks = ShotMaxLooks;
        editor.shotHash = 0;
        editor.shotLast = {};
        SetTimer(editor.hwnd, ShotTimer, ShotEveryMs, nullptr);
    }

    void LookForSnapshot()
    {
        if (!editor.shotLooks || IsIconic(editor.hwnd)) return;
        editor.shotLooks--;
        Shot shot = TakeShot();
        bool settled = shot.Usable() && shot.hash == editor.shotHash && ShotMaxLooks - editor.shotLooks >= ShotMinLooks;
        editor.shotHash = shot.hash;
        if (shot.Usable()) editor.shotLast = std::move(shot);
        if (settled || !editor.shotLooks) EndSnapshot(editor.shotLast.Usable() && SaveShot(editor.shotLast));
    }

    void Command(const std::string& line)
    {
        auto space = line.find(' ');
        std::string cmd = line.substr(0, space), arg = space == std::string::npos ? "" : line.substr(space + 1);
        if (cmd == "look") SetLook(arg);
        else if (cmd == "title")
        {
            editor.title = Wide(arg);
            if (editor.hwnd) SetWindowTextW(editor.hwnd, editor.title.c_str());
        }
        else if (cmd == "toolbar") SetButtons(arg);
        else if (cmd == "toolbar-track") SetTrack(arg);
        else if (cmd == "toolbar-state") SetButtonStates(arg);
        else if (cmd == "keys-panel") SetPanel(std::atoi(arg.c_str()));
        else if (cmd == "key-route") keysToPlugin = arg == "plugin";
        else if (cmd == "state") Say("state " + GetState());
        else if (cmd == "stats") Say(Stats());
        else if (cmd == "params") SayParams();
        else if (cmd == "show-params") ShowParams();
        else if (cmd == "load-state") Say(SetState(arg) ? "loaded" : "error no pude cargar el estado guardado del plugin");
        else if (cmd == "editor") OpenEditor(arg.empty() ? nullptr : reinterpret_cast<HWND>(static_cast<intptr_t>(std::stoll(arg))));
        else if (cmd == "editor-close") CloseEditor();
        else if (cmd == "snapshot") StartSnapshot(arg, false);
        else if (cmd == "snapshot-now") StartSnapshot(arg, true);
        else if (cmd == "quit") PostQuitMessage(0);
    }

    void Shutdown()
    {
        quit = true;
        CloseEditor();
        if (vst2) vst2->Close();
        if (processor) processor->setProcessing(false);
        if (component) component->setActive(false);
        if (controller) controller->setComponentHandler(nullptr);
        data.unprepare();
        processor = nullptr;
        controller = nullptr;
        component = nullptr;
        provider = nullptr;
        module = nullptr;
    }
};

Host* gHost = nullptr;

/// Tells the app where the window is, as seen: GetWindowRect counts the invisible border it resizes from.
void SayEditorRect(HWND hwnd)
{
    RECT r {};
    if (IsIconic(hwnd) || !IsWindowVisible(hwnd))
    {
        Say("editor-rect");
        return;
    }
    if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, sizeof r))) GetWindowRect(hwnd, &r);
    Say("editor-rect " + std::to_string(r.left) + " " + std::to_string(r.top) + " " + std::to_string(r.right) + " " + std::to_string(r.bottom));
}

LRESULT CALLBACK EditorProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_NCCREATE)
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams));
    auto* host = reinterpret_cast<Host*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    switch (msg)
    {
    case WM_WINDOWPOSCHANGED:
        SayEditorRect(hwnd);
        break;
    case WM_ACTIVATE:
        Say(LOWORD(wp) != WA_INACTIVE ? "editor-active" : "editor-inactive");
        break;
    case WM_SIZE:
        if (host && host->editor.plug)
        {
            int bar = host->editor.Bar(), panel = host->editor.Panel(), width = LOWORD(lp), height = std::max(0, HIWORD(lp) - bar - panel);
            RECT was {};
            GetClientRect(host->editor.plug, &was);
            MoveWindow(host->editor.plug, 0, bar, width, height, TRUE);
            if (host->editor.panelHost)
            {
                MoveWindow(host->editor.panelHost, 0, bar + height, width, panel, TRUE);
                // the panel is the app's window: asked, not waited for
                if (HWND content = GetWindow(host->editor.panelHost, GW_CHILD))
                    SetWindowPos(content, nullptr, 0, 0, width, panel, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            }
            // the panel coming and going leaves the plugin alone: asked to resize, some rescale themselves
            bool resized = was.right != width || was.bottom != height;
            if (resized && host->editor.view && host->editor.view->canResize() == kResultTrue)
            {
                ViewRect r {0, 0, width, height};
                host->editor.view->onSize(&r);
            }
            host->RedrawBar();
        }
        break;
    case WM_DPICHANGED:
        if (host && host->editor.plug)
        {
            host->DpiChanged(LOWORD(wp), *reinterpret_cast<RECT*>(lp));
            return 0;
        }
        break;
    case WM_TIMER:
        if (host && wp == ShotTimer)
        {
            host->LookForSnapshot();
            return 0;
        }
        break;
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(hwnd, &ps);
        RECT client {};
        GetClientRect(hwnd, &client);
        if (host) host->PaintBar(dc, client.right);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_MOUSEMOVE:
        if (host)
        {
            int over = host->ButtonAt(LOWORD(lp), HIWORD(lp));
            if (over != host->editor.hover)
            {
                host->editor.hover = over;
                host->RedrawBar();
            }
            TRACKMOUSEEVENT track {sizeof track, TME_LEAVE, hwnd, 0};
            TrackMouseEvent(&track);
        }
        break;
    case WM_MOUSELEAVE:
        if (host)
        {
            host->editor.hover = -1;
            host->RedrawBar();
        }
        break;
    case WM_LBUTTONDOWN:
        if (host)
        {
            int at = host->ButtonAt(LOWORD(lp), HIWORD(lp));
            if (at >= 0) Say("toolbar " + host->editor.buttons[at].id);
        }
        break;
    case WM_DESTROY:
        if (host && (host->editor.view || host->vst2))
        {
            foreignKeys.Stop();
            if (host->vst2) host->vst2->CloseEditor();
            else
            {
                host->editor.view->setFrame(nullptr);
                host->editor.view->removed();
                host->editor.view = nullptr;
            }
            host->editor.hwnd = nullptr;
            host->editor.plug = nullptr;
            host->editor.panelHost = nullptr;
            host->editor.hover = -1;
            // closed before it settled: the next time it opens tries again
            if (host->editor.shotLooks) Say("snapshot-failed");
            host->editor.shotLooks = 0;
            host->editor.shotLast = {};
            Say("editor-closed");
        }
        break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

LRESULT CALLBACK MessageProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_COMMAND_LINE)
    {
        std::unique_ptr<std::string> line(reinterpret_cast<std::string*>(lp));
        if (gHost) gHost->Command(*line);
        return 0;
    }
    if (msg == WM_TIMER && gHost && gHost->vst2)
    {
        gHost->vst2->Idle();
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

/// A crash inside the plugin: say so in the shared memory (the app silences it) and leave.
LONG WINAPI OnCrash(EXCEPTION_POINTERS*)
{
    if (gHost && gHost->shared)
    {
        strncpy_s(gHost->shared->error, "el plugin se cerró de golpe", _TRUNCATE);
        gHost->shared->status = gcvst::Crashed;
    }
    Say("error el plugin se cerró de golpe");
    return EXCEPTION_EXECUTE_HANDLER;
}

int HostMain(const std::string& path, const std::string& cid, const std::wstring& shm, int32 sampleRate, bool offline, int32 maxBlock, DWORD parent)
{
    SetUnhandledExceptionFilter(OnCrash);
    ULONG_PTR gdiplus = 0;
    Gdiplus::GdiplusStartupInput gdiplusInput;
    Gdiplus::GdiplusStartup(&gdiplus, &gdiplusInput, nullptr);
    Host host;
    gHost = &host;
    host.mainThread = GetCurrentThreadId();
    foreignKeys.parent = parent;

    HANDLE map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, shm.c_str());
    if (!map) { Say("error no encontré la memoria compartida"); return 2; }
    host.shared = static_cast<gcvst::Shared*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(gcvst::Shared)));
    host.request = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, (shm + L".req").c_str());
    host.done = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, (shm + L".done").c_str());
    if (!host.shared || !host.request || !host.done || host.shared->magic != gcvst::Magic || host.shared->version != gcvst::Version)
    {
        Say("error la memoria compartida no es de esta versión");
        return 2;
    }

    // OLE, not just COM: a plugin's window registers itself for drag and drop, which fails on a thread without it.
    OleInitialize(nullptr);
    WNDCLASSW wc {};
    wc.lpfnWndProc = EditorProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"XtVstEditor";
    RegisterClassW(&wc);
    WNDCLASSW pc {};
    pc.lpfnWndProc = DefWindowProcW;
    pc.hInstance = wc.hInstance;
    pc.hCursor = wc.hCursor;
    pc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    pc.lpszClassName = L"XtVstPlug";
    RegisterClassW(&pc);
    WNDCLASSW mc {};
    mc.lpfnWndProc = MessageProc;
    mc.hInstance = GetModuleHandleW(nullptr);
    mc.lpszClassName = L"XtVstMessages";
    RegisterClassW(&mc);
    host.messages = CreateWindowExW(0, L"XtVstMessages", L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, mc.hInstance, nullptr);

    static HostApplication hostApp;
    PluginContextFactory::instance().setPluginContext(&hostApp);
    if (!host.Load(path, cid, sampleRate, offline, maxBlock)) return 3;

    std::thread audio([&host] { host.Run(); });
    // stdin: one command per line, handed to the window's thread (VST3 wants state and views there)
    HWND messages = host.messages;
    std::thread input([messages] {
        std::string line;
        while (std::getline(std::cin, line))
        {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            PostMessageW(messages, WM_COMMAND_LINE, 0, reinterpret_cast<LPARAM>(new std::string(line)));
        }
        PostMessageW(messages, WM_COMMAND_LINE, 0, reinterpret_cast<LPARAM>(new std::string("quit")));
    });
    input.detach();
    // the app went away (it crashed, or was killed): so does this
    if (parent)
        if (HANDLE p = OpenProcess(SYNCHRONIZE, FALSE, parent))
            std::thread([p, messages] {
                WaitForSingleObject(p, INFINITE);
                PostMessageW(messages, WM_COMMAND_LINE, 0, reinterpret_cast<LPARAM>(new std::string("quit")));
            }).detach();

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        if (host.TakeKey(msg)) continue;
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    host.quit = true;
    SetEvent(host.request);
    audio.join();
    host.Shutdown();
    PluginContextFactory::instance().setPluginContext(nullptr);
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    std::string scan, path, cid;
    std::wstring shm;
    int32 sampleRate = 48000, maxBlock = gcvst::MaxBlock;
    bool offline = false;
    DWORD parent = 0;
    for (int i = 1; i < argc; i++)
    {
        std::wstring a = argv[i];
        auto next = [&]() { return i + 1 < argc ? std::wstring(argv[++i]) : std::wstring(); };
        if (a == L"--scan") scan = Utf8(next().c_str());
        else if (a == L"--host") path = Utf8(next().c_str());
        else if (a == L"--cid") cid = Utf8(next().c_str());
        else if (a == L"--shm") shm = next();
        else if (a == L"--sr") sampleRate = std::stoi(next());
        else if (a == L"--block") maxBlock = std::stoi(next());
        else if (a == L"--offline") offline = true;
        else if (a == L"--parent") parent = static_cast<DWORD>(std::stoul(next()));
    }
    // before any window: left to Windows, plugins are stretched and never hear the screen's scale
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    // a plugin's message box or crash dialog must not hang an invisible process
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    if (!scan.empty()) return Scan(scan);
    if (!path.empty() && !shm.empty()) return HostMain(path, cid, shm, sampleRate, offline, maxBlock, parent);
    Say("error uso: xtvsthost --scan archivo | --host archivo --cid id --shm nombre [--sr 48000] [--block n] [--offline] [--parent pid]");
    return 1;
}
