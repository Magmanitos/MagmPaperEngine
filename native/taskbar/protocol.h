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
constexpr uint32_t Magic=0x4d475431, Version=2;
constexpr size_t MaxThreads=16;
inline constexpr wchar_t AttachMessage[]=L"MagmPaper.Taskbar.Attach.2";
inline constexpr wchar_t UpdateMessage[]=L"MagmPaper.Taskbar.Update.2";
inline constexpr wchar_t StopMessage[]=L"MagmPaper.Taskbar.Stop.2";
// No paths, executable code or arbitrary property names travel over this protocol.
struct Settings {
    uint32_t sequence=0, enabled=0, height=48, icon=24, button=44;
    uint32_t color=0x171923, opacity=80, hideWidgets=0, radius=0;
};
struct Slot {
    volatile LONG thread=0, applied=0, frames=0, icons=0, backgrounds=0, errors=0;
    volatile LONG errorCode=0, failureStage=0; // 1: XAML, 2: HWND, 3: restoration
    uint64_t window=0;
};
struct Shared {
    uint32_t magic=Magic, version=Version, hostPid=0;
    volatile LONG generation=0, stopped=0, error=0, bars=0, observing=0;
    Settings settings{};
    Slot slots[MaxThreads]{};
};
inline bool valid(const Settings& s) {
    return s.sequence>0&&s.sequence<0x7fffffff&&s.enabled<=1&&s.height>=32&&s.height<=96&&
        s.icon>=16&&s.icon<=64&&s.icon+8<=s.height&&s.button>=28&&s.button<=100&&s.button>=s.icon&&
        s.color<=0xffffff&&s.opacity<=100&&s.hideWidgets<=1&&s.radius<=24;
}
inline std::wstring mappingName(DWORD pid,DWORD host) { return L"Local\\MagmPaper.Taskbar.2."+std::to_wstring(pid)+L"."+std::to_wstring(host); }
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
inline void anchorBarPosition(WINDOWPOS& position,RECT current,RECT monitor,UINT dpi,UINT height,bool autoHide) {
    // SWP_NOMOVE leaves x/y undefined. A size-only request must become a move
    // too, otherwise increasing height grows the taskbar below the screen.
    int x=(position.flags&SWP_NOMOVE)?current.left:position.x;
    int y=(position.flags&SWP_NOMOVE)?current.top:position.y;
    if(position.flags&SWP_NOSIZE) position.cx=current.right-current.left;
    position.cy=MulDiv(static_cast<int>(height),static_cast<int>(dpi),96);
    position.x=x;
    position.y=autoHide&&y>=monitor.bottom-4?y:monitor.bottom-position.cy;
    position.flags&=~(SWP_NOMOVE|SWP_NOSIZE);
}
inline RECT restoredWorkArea(RECT current,RECT monitor,UINT dpi,LONG originalHeight,UINT originalDpi,bool autoHide) {
    // A saved absolute rectangle is invalid after a game changes resolution/DPI.
    // Preserve other appbars and restore only Explorer's strip on this monitor.
    current.bottom=monitor.bottom-(autoHide?0:MulDiv(originalHeight,dpi,originalDpi));
    return current;
}
}
