#include "styles.h"
#include <commctrl.h>
#include <shellapi.h>
#include <xamlom.h>
#include <ocidl.h>
#include <unordered_map>
#include <mutex>
#include <atomic>

using namespace winrt;
using namespace magm;
namespace {
HMODULE module;
Shared* shared=nullptr;
HANDLE mapping=nullptr,hostProcess=nullptr,hostWait=nullptr;
std::atomic<bool> stopping=false;
std::atomic<bool> observerStopped=true;
std::mutex initLock;
const UINT attachMessage=RegisterWindowMessageW(AttachMessage);
const UINT updateMessage=RegisterWindowMessageW(UpdateMessage);
const UINT stopMessage=RegisterWindowMessageW(StopMessage);
constexpr UINT Revisit=WM_APP+44;
const CLSID TapClass={0x0476aa92,0x2461,0x4c27,{0xb2,0xe2,0x86,0x6f,0x66,0x92,0x42,0x09}};
// XAML's diagnostics lifetime interface: release each diagnostic handle once
// discovery finishes. Keeping these handles would retain removed shell elements.
struct __declspec(uuid("735941a2-3ee3-495a-8da9-972627003075")) DiagnosticsLifetime:IUnknown {
    virtual HRESULT STDMETHODCALLTYPE UnregisterInstance(InstanceHandle)=0;
    virtual HRESULT STDMETHODCALLTYPE TryGetDispatcherQueueForObject(InstanceHandle,void**)=0;
};
struct Bar { HWND window; RECT original; UINT originalDpi=96; bool resized=false; RECT previousWork{},writtenWork{}; bool changedWork=false; };
struct Context {
    HWND window=nullptr;
    Slot* slot=nullptr;
    std::vector<Bar> bars;
    std::unordered_map<InstanceHandle,std::unique_ptr<StyledElement>> elements;
    std::unordered_map<InstanceHandle,weak_ref<xaml::FrameworkElement>> pending;
    Settings settings{};
    bool revisitQueued=false;
    com_ptr<DiagnosticsLifetime> lifetime;
    std::vector<InstanceHandle> releases;
};
thread_local Context* context=nullptr;
com_ptr<IVisualTreeService3> treeService;
std::mutex serviceLock;

bool initialize(DWORD host=0) {
    std::lock_guard lock(initLock);
    if(shared&&(!host||shared->hostPid==host)) return !stopping;
    if(!host) return false;
    if(shared) {
        if(!stopping||!observerStopped) return false;
        for(auto& slot:shared->slots) if(slot.thread) return false;
        if(hostWait) UnregisterWaitEx(hostWait,INVALID_HANDLE_VALUE);
        hostWait=nullptr;CloseHandle(hostProcess);hostProcess=nullptr;
        UnmapViewOfFile(shared);shared=nullptr;CloseHandle(mapping);mapping=nullptr;
    }
    mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,mappingName(GetCurrentProcessId(),host).c_str());
    if(!mapping) return false;
    auto candidate=static_cast<Shared*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared)));
    if(!candidate||candidate->magic!=Magic||candidate->version!=Version) return false;
    shared=candidate;stopping=false;
    HMODULE pin=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(&initialize),&pin);
    hostProcess=OpenProcess(SYNCHRONIZE,FALSE,shared->hostPid);
    if(!hostProcess) {stopping=true;return false;}
    RegisterWaitForSingleObject(&hostWait,hostProcess,[](void*,BOOLEAN){
        stopping=true;
        if(shared) for(auto& slot:shared->slots) if(slot.window) PostMessageW(reinterpret_cast<HWND>(slot.window),stopMessage,0,0);
    },nullptr,INFINITE,WT_EXECUTEONLYONCE);
    return true;
}
void workArea(Bar& bar,const RECT& desired) {
#ifdef MAGM_HARNESS
    return; // The isolated surface is not the real shell and owns no work area.
#endif
    MONITORINFO info{sizeof(info)};
    if(!GetMonitorInfoW(MonitorFromWindow(bar.window,MONITOR_DEFAULTTONEAREST),&info)) return;
    APPBARDATA data{sizeof(data)};data.hWnd=bar.window;
    bool autoHide=(SHAppBarMessage(ABM_GETSTATE,&data)&ABS_AUTOHIDE)!=0;
    if(autoHide) {
        if(bar.changedWork&&EqualRect(&info.rcWork,&bar.writtenWork)) SystemParametersInfoW(SPI_SETWORKAREA,0,&bar.previousWork,SPIF_SENDCHANGE);
        bar.changedWork=false;return;
    }
    // Preserve other appbars and only adjust the bottom strip owned by Explorer.
    if(!bar.changedWork) bar.previousWork=info.rcWork;
    LONG originalTop=info.rcMonitor.bottom-MulDiv(bar.original.bottom-bar.original.top,GetDpiForWindow(bar.window),bar.originalDpi);
    if(info.rcWork.bottom!=originalTop&&(!bar.changedWork||info.rcWork.bottom!=bar.writtenWork.bottom)) return;
    RECT next=info.rcWork;next.bottom=desired.top;
    if(!EqualRect(&next,&info.rcWork)&&SystemParametersInfoW(SPI_SETWORKAREA,0,&next,SPIF_SENDCHANGE)) {
        bar.writtenWork=next;bar.changedWork=true;
    }
}
void resizeBars(Context& ctx,bool enabled) {
    for(auto& bar:ctx.bars) {
        if(!IsWindow(bar.window)) continue;
        if(enabled) {
            MONITORINFO info{sizeof(info)};
            if(!GetMonitorInfoW(MonitorFromWindow(bar.window,MONITOR_DEFAULTTONEAREST),&info)) continue;
            RECT target=barRect(info.rcMonitor,GetDpiForWindow(bar.window),ctx.settings.height);
            RECT actual{};GetWindowRect(bar.window,&actual);
            int y=actual.top>=info.rcMonitor.bottom-4?actual.top:target.top;
            SetWindowPos(bar.window,nullptr,actual.left,y,actual.right-actual.left,target.bottom-target.top,SWP_NOZORDER|SWP_NOACTIVATE);
            bar.resized=true;workArea(bar,target);
        } else if(bar.resized) {
            MONITORINFO info{sizeof(info)};GetMonitorInfoW(MonitorFromWindow(bar.window,MONITOR_DEFAULTTONEAREST),&info);
            if(bar.changedWork&&EqualRect(&info.rcWork,&bar.writtenWork)) SystemParametersInfoW(SPI_SETWORKAREA,0,&bar.previousWork,SPIF_SENDCHANGE);
            bar.changedWork=false;
            RECT actual{};GetWindowRect(bar.window,&actual);
            int height=MulDiv(bar.original.bottom-bar.original.top,GetDpiForWindow(bar.window),bar.originalDpi);
            int y=actual.top>=info.rcMonitor.bottom-4?actual.top:info.rcMonitor.bottom-height;
            SetWindowPos(bar.window,nullptr,info.rcMonitor.left,y,info.rcMonitor.right-info.rcMonitor.left,height,SWP_NOZORDER|SWP_NOACTIVATE);
            bar.resized=false;
        }
    }
}
void update(Context& ctx) {
    Settings value{};if(!shared||!read(shared,value)) return;
    if(stopping) value.enabled=0;
    ctx.settings=value;
    LONG errors=0;
    for(auto& [id,element]:ctx.elements) try {element->apply(value);} catch(...) {errors++;}
    resizeBars(ctx,value.enabled!=0);
    InterlockedExchange(&ctx.slot->errors,errors);
    InterlockedExchange(&ctx.slot->applied,static_cast<LONG>(value.sequence));
}
LRESULT CALLBACK barProcedure(HWND window,UINT message,WPARAM wp,LPARAM lp,UINT_PTR,DWORD_PTR data) {
    auto& ctx=*reinterpret_cast<Context*>(data);
    if(message==WM_WINDOWPOSCHANGING&&ctx.settings.enabled&&!stopping) {
        auto pos=reinterpret_cast<WINDOWPOS*>(lp);
        if(!(pos->flags&SWP_NOSIZE)||!(pos->flags&SWP_NOMOVE)) {
            MONITORINFO info{sizeof(info)};
            if(GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&info)) {
                RECT target=barRect(info.rcMonitor,GetDpiForWindow(window),ctx.settings.height);
                pos->cy=target.bottom-target.top;pos->flags&=~SWP_NOSIZE;
                if(!(pos->flags&SWP_NOMOVE)&&pos->y<info.rcMonitor.bottom-4) pos->y=target.top;
            }
        }
    }
    if(message==WM_DPICHANGED||message==WM_DISPLAYCHANGE||message==WM_SETTINGCHANGE) PostMessageW(ctx.window,updateMessage,0,0);
    if(message==WM_NCDESTROY) {
        RemoveWindowSubclass(window,barProcedure,1);
        std::erase_if(ctx.bars,[&](const auto& bar){return bar.window==window;});
        if(shared) InterlockedDecrement(&shared->bars);
    }
    return DefSubclassProc(window,message,wp,lp);
}
bool underTaskbar(xaml::FrameworkElement element) {
    for(int depth=0;element&&depth<30;depth++) {
        auto type=get_class_name(element);
#ifdef MAGM_HARNESS
        if(element.Name()==L"MagmPaper.Test.Frame") return true;
#endif
        if(type==L"Taskbar.TaskbarFrame"||type==L"SystemTray.SystemTrayFrame") return true;
        element=xaml::Media::VisualTreeHelper::GetParent(element).try_as<xaml::FrameworkElement>();
    }
    return false;
}
void remember(Context& ctx,InstanceHandle id,xaml::FrameworkElement element,const std::wstring& type) {
    auto kind=classify(type,std::wstring(element.Name()),underTaskbar(element));
    if(kind==Kind::None) return;
    if(ctx.elements.contains(id)) return;
    ctx.elements.emplace(id,std::make_unique<StyledElement>(element,kind));
    if(kind==Kind::Frame) {
        InterlockedIncrement(&ctx.slot->frames);
        // The outer XAML island content grid also constrains the frame's height.
        auto parent=xaml::Media::VisualTreeHelper::GetParent(element).try_as<xaml::FrameworkElement>();
        if(parent) {
            auto parentId=reinterpret_cast<InstanceHandle>(get_abi(parent));
            if(!ctx.elements.contains(parentId)) ctx.elements.emplace(parentId,std::make_unique<StyledElement>(parent,Kind::Content));
        }
    }
    if(kind==Kind::Icon) InterlockedIncrement(&ctx.slot->icons);
    if(kind==Kind::Background) InterlockedIncrement(&ctx.slot->backgrounds);
    if(ctx.settings.enabled) ctx.elements.at(id)->apply(ctx.settings);
}
LRESULT CALLBACK contextProcedure(HWND window,UINT message,WPARAM wp,LPARAM lp) {
    auto ctx=reinterpret_cast<Context*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if(message==WM_NCCREATE) {ctx=static_cast<Context*>(reinterpret_cast<CREATESTRUCT*>(lp)->lpCreateParams);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(ctx));}
    if(!ctx) return DefWindowProcW(window,message,wp,lp);
    try {
        if(message==Revisit) {
            ctx->revisitQueued=false;
            auto pending=std::move(ctx->pending);ctx->pending.clear();
            for(auto& [id,weak]:pending) if(auto element=weak.get()) remember(*ctx,id,element,std::wstring(get_class_name(element)));
            if(ctx->lifetime) for(auto id:ctx->releases) ctx->lifetime->UnregisterInstance(id);
            ctx->releases.clear();
            update(*ctx);return 0;
        }
        if(message==updateMessage) {update(*ctx);return 0;}
        if(message==stopMessage) {
            ctx->settings.enabled=0;
            for(auto& [id,element]:ctx->elements) element->restore();
            resizeBars(*ctx,false);
            for(auto& bar:ctx->bars) RemoveWindowSubclass(bar.window,barProcedure,1);
            ctx->elements.clear();ctx->pending.clear();
            ctx->slot->window=0;InterlockedExchange(&ctx->slot->thread,0);
            DestroyWindow(window);context=nullptr;delete ctx;return 0;
        }
    } catch(...) {if(ctx->slot) InterlockedIncrement(&ctx->slot->errors);}
    return DefWindowProcW(window,message,wp,lp);
}
Context* getContext() {
    if(context) return context;
    if(!initialize()) return nullptr;
    auto ctx=std::make_unique<Context>();
    for(auto& slot:shared->slots) if(InterlockedCompareExchange(&slot.thread,GetCurrentThreadId(),0)==0) {ctx->slot=&slot;break;}
    if(!ctx->slot) return nullptr;
    WNDCLASSW klass{};klass.lpfnWndProc=contextProcedure;klass.hInstance=module;klass.lpszClassName=L"MagmPaperTaskbar.Thread.1";RegisterClassW(&klass);
    ctx->window=CreateWindowExW(0,klass.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,module,ctx.get());
    if(!ctx->window) {InterlockedExchange(&ctx->slot->thread,0);return nullptr;}
    ctx->slot->window=reinterpret_cast<uint64_t>(ctx->window);read(shared,ctx->settings);
    context=ctx.release();return context;
}
// Documented XAML diagnostics callback, independent of Windhawk and symbol hooks.
struct Watcher:implements<Watcher,IVisualTreeServiceCallback2> {
    com_ptr<IXamlDiagnostics> diagnostics;
    com_ptr<DiagnosticsLifetime> lifetime;
    DWORD owner=0;
    explicit Watcher(IUnknown* site):owner(shared->hostPid) {
        check_hresult(site->QueryInterface(guid_of<IXamlDiagnostics>(),diagnostics.put_void()));
        check_hresult(site->QueryInterface(__uuidof(DiagnosticsLifetime),lifetime.put_void()));
    }
    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation relation,VisualElement element,VisualMutationType mutation) noexcept override {
        if(stopping||!shared||shared->hostPid!=owner) return S_OK;
        try {
            auto ctx=getContext();if(!ctx) return S_OK;
            ctx->lifetime=lifetime;
            ctx->releases.push_back(element.Handle);
            if(relation.Parent) ctx->releases.push_back(relation.Parent);
            if(!ctx->revisitQueued) {ctx->revisitQueued=true;PostMessageW(ctx->window,Revisit,0,0);}
            if(mutation==Add) {
                Windows::Foundation::IInspectable object{nullptr};
                check_hresult(diagnostics->GetIInspectableFromHandle(element.Handle,reinterpret_cast<::IInspectable**>(put_abi(object))));
                if(auto value=object.try_as<xaml::FrameworkElement>()) {
                    auto type=std::wstring(get_class_name(value));
                    if(classify(type,std::wstring(value.Name()),true)!=Kind::None) {
                        remember(*ctx,element.Handle,value,type);
                        // Parent links can become available only after Add returns.
                        ctx->pending.insert_or_assign(element.Handle,make_weak(value));
                    }
                }
            } else if(mutation==Remove) {
                ctx->pending.erase(element.Handle);
                auto found=ctx->elements.find(element.Handle);
                if(found!=ctx->elements.end()) {
                    if(found->second->kind==Kind::Frame) InterlockedDecrement(&ctx->slot->frames);
                    if(found->second->kind==Kind::Icon) InterlockedDecrement(&ctx->slot->icons);
                    if(found->second->kind==Kind::Background) InterlockedDecrement(&ctx->slot->backgrounds);
                    ctx->elements.erase(found);
                }
            }
        } catch(...) {if(shared) InterlockedExchange(&shared->error,to_hresult());}
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle,VisualElementState,LPCWSTR) noexcept override {return S_OK;}
};
com_ptr<Watcher> watcher;
struct Tap:implements<Tap,IObjectWithSite> {
    com_ptr<IUnknown> site;
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* value) noexcept override {
        try {
            if(!value) {site=nullptr;return S_OK;}
            if(!initialize()) return E_FAIL;
            site.copy_from(value);
            auto service=site.as<IVisualTreeService3>();auto observer=make_self<Watcher>(value);
            {std::lock_guard lock(serviceLock);treeService=service;watcher=observer;}
            // Advising can synchronously visit UI threads. Never do it under a UI callback.
            using Work=std::pair<com_ptr<IVisualTreeService3>,com_ptr<Watcher>>;
            auto data=new Work(service,observer);observerStopped=false;
            HANDLE thread=CreateThread(nullptr,0,[](void* value)->DWORD {
                std::unique_ptr<Work> pair(static_cast<Work*>(value));
                HRESULT result=pair->first->AdviseVisualTreeChange(pair->second.get());
                if(FAILED(result)&&shared) InterlockedExchange(&shared->error,result);
                WaitForSingleObject(hostProcess,INFINITE);
                stopping=true;pair->first->UnadviseVisualTreeChange(pair->second.get());
                observerStopped=true;
                return 0;
            },data,0,nullptr);
            if(!thread) {delete data;observerStopped=true;return HRESULT_FROM_WIN32(GetLastError());}CloseHandle(thread);return S_OK;
        } catch(...) {return to_hresult();}
    }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID id,void** result) noexcept override {return site?site->QueryInterface(id,result):E_FAIL;}
};
struct Factory:implements<Factory,IClassFactory> {
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID id,void** result) noexcept override {
        if(outer) return CLASS_E_NOAGGREGATION;
        try {return make<Tap>().as(id,result);} catch(...) {return to_hresult();}
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override {return S_OK;}
};
}
extern "C" __declspec(dllexport) LRESULT CALLBACK MagmTaskbarHook(int code,WPARAM wp,LPARAM lp) {
    if(code>=0) {
        auto message=reinterpret_cast<CWPSTRUCT*>(lp);
        if(message->message==attachMessage) try {
            if(!initialize(static_cast<DWORD>(message->wParam))) return CallNextHookEx(nullptr,code,wp,lp);
            auto ctx=getContext();
            if(ctx&&std::none_of(ctx->bars.begin(),ctx->bars.end(),[&](auto& bar){return bar.window==message->hwnd;})) {
                wchar_t name[64]{};GetClassNameW(message->hwnd,name,64);
                if(wcscmp(name,MainBarClass)==0||wcscmp(name,SecondaryBarClass)==0) {
                    RECT rect{};GetWindowRect(message->hwnd,&rect);
                    if(rect.right-rect.left>rect.bottom-rect.top&&SetWindowSubclass(message->hwnd,barProcedure,1,reinterpret_cast<DWORD_PTR>(ctx))) {
                        ctx->bars.push_back({message->hwnd,rect,GetDpiForWindow(message->hwnd)});InterlockedIncrement(&shared->bars);
                    }
                }
            }
        } catch(...) {}
    }
    return CallNextHookEx(nullptr,code,wp,lp);
}
STDAPI DllGetClassObject(REFCLSID cls,REFIID id,void** result) {
    if(!result) return E_POINTER;*result=nullptr;
    if(cls!=TapClass) return CLASS_E_CLASSNOTAVAILABLE;
    try {return make<Factory>().as(id,result);} catch(...) {return to_hresult();}
}
STDAPI DllCanUnloadNow() {return S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,void*) {if(reason==DLL_PROCESS_ATTACH){module=instance;DisableThreadLibraryCalls(instance);}return TRUE;}
