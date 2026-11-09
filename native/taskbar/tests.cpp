#include "styles.h"
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#include <iostream>
#include <stdexcept>
#include <functional>

using namespace magm;
using namespace winrt;
void require(bool value,const char* message) {if(!value) throw std::runtime_error(message);}
HWND island=nullptr;
LRESULT CALLBACK surface(HWND hwnd,UINT message,WPARAM wp,LPARAM lp) {
    if(message==WM_SIZE&&island) SetWindowPos(island,nullptr,0,0,LOWORD(lp),HIWORD(lp),SWP_NOZORDER|SWP_NOACTIVATE);
    return DefWindowProcW(hwnd,message,wp,lp);
}
void pump(){MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}}
void until(const std::function<bool()>& predicate,const char* description) {
    ULONGLONG end=GetTickCount64()+20000;
    while(!predicate()){pump();if(GetTickCount64()>end)throw std::runtime_error(description);Sleep(10);}
}
struct Host {
    HANDLE process=nullptr,input=nullptr,output=nullptr;
    std::string data;
    Host(){
        SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};HANDLE inRead=nullptr,outWrite=nullptr;
        require(CreatePipe(&inRead,&input,&security,0)&&CreatePipe(&output,&outWrite,&security,0),"host pipes");
        SetHandleInformation(input,HANDLE_FLAG_INHERIT,0);SetHandleInformation(output,HANDLE_FLAG_INHERIT,0);
        wchar_t name[32768]{};GetModuleFileNameW(nullptr,name,32768);std::wstring path=name;
        path=path.substr(0,path.find_last_of(L"\\/")+1)+L"MagmTaskbarTestHost.exe";
        std::wstring command=L"\""+path+L"\" --parent "+std::to_wstring(GetCurrentProcessId());
        STARTUPINFOW startup{sizeof(startup)};startup.dwFlags=STARTF_USESTDHANDLES;startup.hStdInput=inRead;startup.hStdOutput=outWrite;startup.hStdError=outWrite;
        PROCESS_INFORMATION info{};BOOL result=CreateProcessW(path.c_str(),command.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&info);
        CloseHandle(inRead);CloseHandle(outWrite);require(result!=FALSE,"launch integrated host");process=info.hProcess;CloseHandle(info.hThread);
    }
    void send(const char* line){DWORD written=0;require(WriteFile(input,line,static_cast<DWORD>(strlen(line)),&written,nullptr)!=FALSE,"write host settings");}
    void applied(int sequence){
        const auto expected="\"event\":\"applied\",\"sequence\":"+std::to_string(sequence)+",";
        until([&]{DWORD available=0;if(PeekNamedPipe(output,nullptr,0,nullptr,&available,nullptr)&&available){char buffer[4096];DWORD count=0;ReadFile(output,buffer,std::min<DWORD>(sizeof(buffer),available),&count,nullptr);data.append(buffer,count);std::cout.write(buffer,count).flush();}
            if(data.find("\"event\":\"error\"")!=std::string::npos)throw std::runtime_error("native host: "+data);
            return data.find(expected)!=std::string::npos;},"native host did not confirm configuration");
    }
    void crash(){TerminateProcess(process,99);}
    void close(){CloseHandle(input);input=nullptr;until([&]{return WaitForSingleObject(process,0)==WAIT_OBJECT_0;},"helper did not exit after EOF");}
    ~Host(){if(process){TerminateProcess(process,99);CloseHandle(process);}if(input)CloseHandle(input);if(output)CloseHandle(output);}
};
int main() try {
    Settings settings{};settings.sequence=1;require(valid(settings),"valid default settings");
    settings.icon=64;require(!valid(settings),"reject oversized icon");settings.icon=24;
    RECT monitor{-1920,0,0,1080};auto rect=barRect(monitor,144,64);
    require(rect.top==984&&rect.left==-1920&&rect.right==0,"physical geometry and negative monitor origin");
    RECT changedMonitor{-2560,0,0,1440},changedWork{-2500,32,0,1344};
    auto restored=restoredWorkArea(changedWork,changedMonitor,144,48,96,false);
    require(restored.bottom==1368&&restored.left==-2500&&restored.top==32,"restore after resolution and DPI change while preserving other appbars");
    require(restoredWorkArea(changedWork,changedMonitor,144,48,96,true).bottom==1440,"auto-hide restores the full monitor work area");
    Shared shared{};publish(&shared,settings);Settings readback{};require(read(&shared,readback)&&readback.icon==24,"shared protocol roundtrip");
    require(classify(L"Taskbar.TaskbarFrame",L"",false)==Kind::Frame,"frame detection");
    require(classify(L"Windows.UI.Xaml.Shapes.Rectangle",L"BackgroundFill",false)==Kind::None,"do not style unrelated windows");
    init_apartment(apartment_type::single_threaded);
    auto manager=xaml::Hosting::WindowsXamlManager::InitializeForCurrentThread();
    WNDCLASSW klass{};klass.lpfnWndProc=surface;klass.hInstance=GetModuleHandleW(nullptr);klass.lpszClassName=MainBarClass;RegisterClassW(&klass);
    HWND window=CreateWindowExW(0,MainBarClass,L"MagmPaper native test",WS_POPUP|WS_VISIBLE,0,0,500,48,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
    require(window!=nullptr,"test window");
    auto source=xaml::Hosting::DesktopWindowXamlSource();
    auto native=source.as<IDesktopWindowXamlSourceNative>();check_hresult(native->AttachToWindow(window));
    check_hresult(native->get_WindowHandle(&island));SetWindowPos(island,nullptr,0,0,500,48,SWP_SHOWWINDOW);
    xaml::Controls::Grid root;
    xaml::Controls::Grid frame;frame.Name(L"MagmPaper.Test.Frame");frame.Height(48);frame.MinHeight(0);root.Children().Append(frame);
    xaml::Controls::Image icon;icon.Name(L"Icon");icon.Width(24);icon.Height(24);frame.Children().Append(icon);
    xaml::Shapes::Rectangle background;background.Name(L"BackgroundFill");auto original=xaml::Media::SolidColorBrush(Windows::UI::Color{255,20,30,40});background.Fill(original);frame.Children().Append(background);
    xaml::Controls::Button widgets;widgets.Name(L"MagmPaper.Test.Widgets");frame.Children().Append(widgets);
    source.Content(root);
    StyledElement styledFrame(frame,Kind::Frame),styledIcon(icon,Kind::Icon),styledBackground(background,Kind::Background),styledWidgets(widgets,Kind::Widgets);
    settings.enabled=1;settings.height=64;settings.icon=40;settings.button=52;settings.opacity=25;settings.color=0x123456;settings.hideWidgets=1;settings.radius=8;
    styledFrame.apply(settings);styledIcon.apply(settings);styledBackground.apply(settings);styledWidgets.apply(settings);root.UpdateLayout();
    require(frame.Height()==64&&frame.MaxHeight()==64&&icon.Width()==40,"native XAML dimensions");
    auto color=background.Fill().as<xaml::Media::SolidColorBrush>().Color();
    require(color.A==64&&color.R==0x12&&color.G==0x34&&color.B==0x56,"background color and opacity");
    require(widgets.Visibility()==xaml::Visibility::Collapsed,"hide visual without registry");
    icon.Width(24);require(icon.Width()==40,"reapply after shell property changes");
    settings.enabled=0;styledFrame.apply(settings);styledIcon.apply(settings);styledBackground.apply(settings);styledWidgets.apply(settings);
    require(frame.Height()==48&&icon.Width()==24&&background.Fill()==original&&widgets.Visibility()==xaml::Visibility::Visible,"restore original values");
    icon.Width(28);require(icon.Width()==28,"remove callbacks on disable");
    icon.Width(24);
    std::cout<<"PASS: direct XAML styling and restoration. Starting helper attachment."<<std::endl;
    {
        Host host;host.send("1 1 64 40 52 1193046 25 1 8\n");host.applied(1);
        require(frame.Height()==64&&icon.Width()==40,"external module changed real XAML elements");
        require(background.Fill().as<xaml::Media::SolidColorBrush>().Color().A==64,"external module background alpha");
        RECT actual{};GetWindowRect(window,&actual);require(actual.bottom-actual.top==MulDiv(64,GetDpiForWindow(window),96),"native taskbar window resized");
        host.send("2 0 64 40 52 1193046 25 1 8\n");host.applied(2);
        require(frame.Height()==48&&icon.Width()==24&&widgets.Visibility()==xaml::Visibility::Visible,"disable restores native XAML");
        host.send("3 1 64 40 52 1193046 25 1 8\n");host.applied(3);host.crash();
        until([&]{return frame.Height()==48&&icon.Width()==24;},"host crash did not restore XAML");
        std::cout<<"PASS: helper attachment and crash restoration."<<std::endl;
    }
    {
        Host restarted;restarted.send("1 1 60 32 48 3355443 50 0 0\n");restarted.applied(1);
        require(frame.Height()==60&&icon.Width()==32,"reconnect after host termination");
        restarted.close();
        until([&]{return frame.Height()==48&&icon.Width()==24&&background.Fill()==original;},"normal helper exit did not restore settings");
        std::cout<<"PASS: helper reconnection. Closing XAML source."<<std::endl;
    }
    source.Close();DestroyWindow(window);manager.Close();
    std::cout<<"PASS: native XAML sizes, color, opacity, visibility, shell reset and restoration; DPI geometry; IPC validation."<<std::endl;
    return 0;
} catch(const hresult_error& error) {std::cerr<<"WinRT error "<<std::hex<<static_cast<uint32_t>(error.code().value)<<std::endl;return 1;}
catch(const std::exception& error) {std::cerr<<error.what()<<std::endl;return 1;}
