#include "protocol.h"
#include <xamlom.h>
#include <shellapi.h>
#include <iostream>
#include <sstream>
#include <vector>
#include <thread>
#include <mutex>
#include <memory>

using namespace magm;
namespace {
HWND window=nullptr;
HMODULE library=nullptr;
HANDLE mapping=nullptr,parent=nullptr,parentWait=nullptr;
Shared* shared=nullptr;
DWORD explorer=0;
Settings desired{};
bool connected=false,diagnosticsPending=false,closing=false;
ULONGLONG deadline=0;
UINT taskbarCreated=0,attachMessage=0,updateMessage=0,stopMessage=0;
constexpr UINT Configure=WM_APP+1,Attach=WM_APP+2,DiagnosticResult=WM_APP+3;
std::wstring pluginPath;
std::mutex outputLock;
void event(const char* name,uint32_t sequence=0,LONG code=0) {
    std::lock_guard lock(outputLock);
    LONG frames=0,icons=0,backgrounds=0,errors=0;
    if(shared) for(auto& slot:shared->slots) {frames+=slot.frames;icons+=slot.icons;backgrounds+=slot.backgrounds;errors+=slot.errors;}
    std::cout<<"{\"event\":\""<<name<<"\",\"sequence\":"<<sequence<<",\"code\":"<<code
        <<",\"frames\":"<<frames<<",\"icons\":"<<icons<<",\"backgrounds\":"<<backgrounds<<",\"errors\":"<<errors<<",\"diagnosticError\":"<<(shared?shared->error:0)<<"}"<<std::endl;
}
void signal(UINT message) {
    if(shared) for(auto& slot:shared->slots) if(slot.window) PostMessageW(reinterpret_cast<HWND>(slot.window),message,0,0);
}
void releaseMapping() {
    if(shared) {UnmapViewOfFile(shared);shared=nullptr;}
    if(mapping) {CloseHandle(mapping);mapping=nullptr;}
}
bool sameShell(HWND bar) {DWORD pid=0;GetWindowThreadProcessId(bar,&pid);return pid==explorer;}
void hookBars() {
    auto hook=reinterpret_cast<HOOKPROC>(GetProcAddress(library,"MagmTaskbarHook"));
    if(!hook) return;
    EnumWindows([](HWND bar,LPARAM address)->BOOL {
        wchar_t name[64]{};GetClassNameW(bar,name,64);
        if((wcscmp(name,MainBarClass)&&wcscmp(name,SecondaryBarClass))||!sameShell(bar)) return TRUE;
        DWORD thread=GetWindowThreadProcessId(bar,nullptr);
        HHOOK handle=SetWindowsHookExW(WH_CALLWNDPROC,reinterpret_cast<HOOKPROC>(address),library,thread);
        if(handle) {
            DWORD_PTR result=0;SendMessageTimeoutW(bar,attachMessage,GetCurrentProcessId(),0,SMTO_ABORTIFHUNG,2000,&result);
            UnhookWindowsHookEx(handle);
        }
        return TRUE;
    },reinterpret_cast<LPARAM>(hook));
}
void attach() {
    if(closing||diagnosticsPending) return;
    HWND bar=FindWindowW(MainBarClass,nullptr);DWORD pid=0;
    if(!bar) {SetTimer(window,1,500,nullptr);return;}
    GetWindowThreadProcessId(bar,&pid);
    if(shared&&pid==explorer&&connected) {hookBars();return;}
    if(!shared||pid!=explorer) {
    connected=false;releaseMapping();explorer=pid;
    mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Shared),mappingName(pid,GetCurrentProcessId()).c_str());
    if(!mapping||GetLastError()==ERROR_ALREADY_EXISTS) {event("error",desired.sequence,ERROR_ALREADY_EXISTS);return;}
    shared=static_cast<Shared*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared)));
    if(!shared) {event("error",desired.sequence,GetLastError());return;}
    new(shared) Shared{};shared->hostPid=GetCurrentProcessId();
    auto probe=desired;probe.enabled=0;probe.sequence=0;publish(shared,probe);
    }
    hookBars();
    if(!shared->bars) {SetTimer(window,1,200,nullptr);return;}
    deadline=GetTickCount64()+12000;SetTimer(window,2,100,nullptr);
    if(shared->observing) return;
    diagnosticsPending=true;
    std::thread([pid]{
        HRESULT result=E_NOINTERFACE;
        HMODULE xaml=LoadLibraryExW(L"Windows.UI.Xaml.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(xaml) {
            auto initialize=reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(GetProcAddress(xaml,"InitializeXamlDiagnosticsEx"));
            if(initialize) {
                CLSID cls={0x0476aa92,0x2461,0x4c27,{0xb2,0xe2,0x86,0x6f,0x66,0x92,0x42,0x09}};
                for(int i=1;i<=64;i++) {
                    auto name=L"VisualDiagConnection"+std::to_wstring(i);
                    result=initialize(name.c_str(),pid,L"",pluginPath.c_str(),cls,nullptr);
                    if(result!=HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) break;
                }
            }
            FreeLibrary(xaml);
        }
        PostMessageW(window,DiagnosticResult,0,result);
    }).detach();
}
void apply() {
    if(!connected||!shared) return;
    publish(shared,desired);signal(updateMessage);
    deadline=GetTickCount64()+5000;SetTimer(window,3,50,nullptr);
}
void stop() {
    if(closing) return;closing=true;
    KillTimer(window,1);KillTimer(window,2);KillTimer(window,3);
    if(shared) {auto off=desired;off.enabled=0;off.sequence++;publish(shared,off);signal(stopMessage);}
    // Wait asynchronously for UI threads to restore before the host exits.
    deadline=GetTickCount64()+4000;SetTimer(window,4,50,nullptr);
}
LRESULT CALLBACK procedure(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp) {
    if(msg==Configure) {
        std::unique_ptr<Settings> next(reinterpret_cast<Settings*>(lp));
        if(closing) return 0;
        desired=*next;
        if(connected) apply();else attach();return 0;
    }
    if(msg==Attach||msg==taskbarCreated) {attach();return 0;}
    if(msg==DiagnosticResult) {
        diagnosticsPending=false;
        if(FAILED(static_cast<HRESULT>(lp))) {KillTimer(hwnd,2);event("error",desired.sequence,static_cast<LONG>(lp));}
        return 0;
    }
    if(msg==WM_CLOSE) {stop();return 0;}
    if(msg==WM_TIMER) {
        if(wp==1) {KillTimer(hwnd,1);attach();}
        if(wp==2) {
            LONG frames=0,icons=0,backgrounds=0;for(auto& slot:shared->slots) {frames+=slot.frames;icons+=slot.icons;backgrounds+=slot.backgrounds;}
            if(shared->error) {KillTimer(hwnd,2);event("error",desired.sequence,shared->error);stop();}
            else if(shared->bars&&frames>=shared->bars&&icons>0&&backgrounds>0) {KillTimer(hwnd,2);connected=true;event("ready",desired.sequence);apply();}
            else if(GetTickCount64()>deadline) {KillTimer(hwnd,2);event("error",desired.sequence,ERROR_NOT_SUPPORTED);stop();}
        }
        if(wp==3) {
            bool done=true;LONG errors=0;
            for(auto& slot:shared->slots) if(slot.thread) {done&=slot.applied>=static_cast<LONG>(desired.sequence);errors+=slot.errors;}
            if(done) {KillTimer(hwnd,3);event(errors?"error":"applied",desired.sequence,errors?ERROR_INVALID_DATA:0);}
            else if(GetTickCount64()>deadline) {KillTimer(hwnd,3);event("error",desired.sequence,WAIT_TIMEOUT);}
        }
        if(wp==4) {
            bool done=true;
            if(shared) for(auto& slot:shared->slots) done&=slot.thread==0;
            if(done||GetTickCount64()>deadline) {KillTimer(hwnd,4);event("stopped");DestroyWindow(hwnd);PostQuitMessage(0);}
        }
        return 0;
    }
    return DefWindowProcW(hwnd,msg,wp,lp);
}
void CALLBACK shellEvent(HWINEVENTHOOK,DWORD eventId,HWND bar,LONG object,LONG,DWORD,DWORD) {
    if(object!=OBJID_WINDOW||closing) return;
    wchar_t name[64]{};GetClassNameW(bar,name,64);
    if(eventId==EVENT_OBJECT_CREATE&&(!wcscmp(name,MainBarClass)||!wcscmp(name,SecondaryBarClass))) PostMessageW(window,Attach,0,0);
}
}
int wmain(int argc,wchar_t** argv) {
    if(argc!=3||wcscmp(argv[1],L"--parent")) return 2;
    wchar_t* end=nullptr;DWORD parentId=wcstoul(argv[2],&end,10);if(!parentId||*end) return 2;
    parent=OpenProcess(SYNCHRONIZE,FALSE,parentId);if(!parent) return 3;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    wchar_t path[32768]{};GetModuleFileNameW(nullptr,path,32768);pluginPath=path;
    pluginPath=pluginPath.substr(0,pluginPath.find_last_of(L"\\/")+1)+PluginName;
    library=LoadLibraryExW(pluginPath.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!library) {event("error",0,GetLastError());return 4;}
    taskbarCreated=RegisterWindowMessageW(L"TaskbarCreated");attachMessage=RegisterWindowMessageW(AttachMessage);
    updateMessage=RegisterWindowMessageW(UpdateMessage);stopMessage=RegisterWindowMessageW(StopMessage);
    WNDCLASSW klass{};klass.lpfnWndProc=procedure;klass.hInstance=GetModuleHandleW(nullptr);klass.lpszClassName=L"MagmPaperTaskbar.Host.1";RegisterClassW(&klass);
    window=CreateWindowExW(WS_EX_TOOLWINDOW,klass.lpszClassName,L"",WS_POPUP,0,0,0,0,nullptr,nullptr,klass.hInstance,nullptr);
    if(!window) return 5;
    RegisterWaitForSingleObject(&parentWait,parent,[](void*,BOOLEAN){PostMessageW(window,WM_CLOSE,0,0);},nullptr,INFINITE,WT_EXECUTEONLYONCE);
    HWINEVENTHOOK hook=SetWinEventHook(EVENT_OBJECT_CREATE,EVENT_OBJECT_CREATE,nullptr,shellEvent,0,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);
    std::thread([]{
        std::string line;
        while(std::getline(std::cin,line)) {
            if(line.size()>256) break;
            auto value=std::make_unique<Settings>();std::istringstream in(line);std::string extra;
            if(!(in>>value->sequence>>value->enabled>>value->height>>value->icon>>value->button>>value->color>>value->opacity>>value->hideWidgets>>value->radius)||in>>extra||!valid(*value)) break;
            if(!PostMessageW(window,Configure,0,reinterpret_cast<LPARAM>(value.get()))) break;
            value.release();
        }
        PostMessageW(window,WM_CLOSE,0,0);
    }).detach();
    MSG message{};while(GetMessageW(&message,nullptr,0,0)>0){TranslateMessage(&message);DispatchMessageW(&message);}
    if(hook) UnhookWinEvent(hook);
    // Terminating this process also wakes the module's independent restoration watchdog.
    return 0;
}
