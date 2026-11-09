#pragma once
#include <windows.h>
#include <cstdint>
#include <string>
#include <algorithm>

namespace magm {
#ifdef MAGM_HARNESS
inline constexpr wchar_t MainBarClass[]=L"MagmPaperTaskbar.TestSurface";
inline constexpr wchar_t SecondaryBarClass[]=L"MagmPaperTaskbar.TestSecondary";
inline constexpr wchar_t PluginName[]=L"MagmTaskbarHarness.dll";
#else
inline constexpr wchar_t MainBarClass[]=L"Shell_TrayWnd";
inline constexpr wchar_t SecondaryBarClass[]=L"Shell_SecondaryTrayWnd";
inline constexpr wchar_t PluginName[]=L"MagmTaskbar.dll";
#endif
constexpr uint32_t Magic=0x4d475431, Version=1;
constexpr size_t MaxThreads=16;
inline constexpr wchar_t AttachMessage[]=L"MagmPaper.Taskbar.Attach.1";
inline constexpr wchar_t UpdateMessage[]=L"MagmPaper.Taskbar.Update.1";
inline constexpr wchar_t StopMessage[]=L"MagmPaper.Taskbar.Stop.1";
// No paths, executable code or arbitrary property names travel over this protocol.
struct Settings {
    uint32_t sequence=0, enabled=0, height=48, icon=24, button=44;
    uint32_t color=0x171923, opacity=80, hideWidgets=0, radius=0;
};
struct Slot {
    volatile LONG thread=0, applied=0, frames=0, icons=0, backgrounds=0, errors=0;
    uint64_t window=0;
};
struct Shared {
    uint32_t magic=Magic, version=Version, hostPid=0;
    volatile LONG generation=0, stopped=0, error=0, bars=0;
    Settings settings{};
    Slot slots[MaxThreads]{};
};
inline bool valid(const Settings& s) {
    return s.sequence>0&&s.sequence<0x7fffffff&&s.enabled<=1&&s.height>=32&&s.height<=96&&
        s.icon>=16&&s.icon<=64&&s.icon+8<=s.height&&s.button>=28&&s.button<=100&&s.button>=s.icon&&
        s.color<=0xffffff&&s.opacity<=100&&s.hideWidgets<=1&&s.radius<=24;
}
inline std::wstring mappingName(DWORD pid,DWORD host) { return L"Local\\MagmPaper.Taskbar.1."+std::to_wstring(pid)+L"."+std::to_wstring(host); }
inline void publish(Shared* shared,const Settings& settings) {
    InterlockedIncrement(&shared->generation);MemoryBarrier();shared->settings=settings;
    MemoryBarrier();InterlockedIncrement(&shared->generation);
}
inline bool read(Shared* shared,Settings& value) {
    for(int i=0;i<3;i++) {
        LONG a=InterlockedCompareExchange(&shared->generation,0,0);
        if(a&1) continue;
        value=shared->settings;MemoryBarrier();
        if(a==InterlockedCompareExchange(&shared->generation,0,0)) {
            auto check=value;if(check.sequence==0&&!check.enabled) check.sequence=1;
            return valid(check);
        }
    }
    return false;
}
inline RECT barRect(RECT monitor,UINT dpi,UINT height) {
    monitor.top=monitor.bottom-MulDiv(static_cast<int>(height),static_cast<int>(dpi),96);return monitor;
}
}
