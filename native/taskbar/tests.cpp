#include "styles.h"
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Xaml.Media.Animation.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#include <iostream>
#include <stdexcept>
#include <functional>
#include <chrono>
#include <cmath>

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
    WINDOWPOS proposed{};proposed.flags=SWP_NOMOVE;proposed.x=123;proposed.y=456;proposed.cx=1920;proposed.cy=48;
    anchorBarPosition(proposed,RECT{-1920,1008,0,1080},monitor,144,64,false);
    require(!(proposed.flags&SWP_NOMOVE)&&proposed.x==-1920&&proposed.y==984&&proposed.cy==96,
        "size-only requests must grow up from the monitor bottom, ignoring undefined x/y");
    proposed.flags=SWP_NOSIZE;proposed.x=-1920;proposed.y=1078;
    anchorBarPosition(proposed,RECT{-1920,984,0,1080},monitor,144,80,true);
    require(proposed.y==1078&&proposed.cy==120&&proposed.cx==1920,"keep auto-hidden taskbar at its reveal strip");
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
    until([&]{return root.IsLoaded();},"test XAML content did not load");
    StyledElement styledFrame(frame,Kind::Frame),styledIcon(icon,Kind::Icon),styledBackground(background,Kind::Background),styledWidgets(widgets,Kind::Widgets);
    settings.enabled=1;settings.height=64;settings.icon=40;settings.button=52;settings.opacity=25;settings.color=0x123456;settings.hideWidgets=1;settings.radius=8;
    styledFrame.apply(settings);styledIcon.apply(settings);styledBackground.apply(settings);styledWidgets.apply(settings);root.UpdateLayout();
    require(frame.Height()==64&&frame.MaxHeight()==64&&icon.Width()==40,"native XAML dimensions");
    bool invalidRange=false;
    auto checkRange=[&](auto const&,auto const&){invalidRange|=frame.MinHeight()>frame.MaxHeight();};
    auto minToken=frame.RegisterPropertyChangedCallback(xaml::FrameworkElement::MinHeightProperty(),checkRange);
    auto maxToken=frame.RegisterPropertyChangedCallback(xaml::FrameworkElement::MaxHeightProperty(),checkRange);
    for(int height:{80,40,96,32,64}) {
        settings.height=height;settings.icon=std::min(40,height-8);
        styledFrame.apply(settings);styledIcon.apply(settings);root.UpdateLayout();
        require(frame.Height()==height&&frame.MinHeight()==height&&frame.MaxHeight()==height&&icon.ActualWidth()==settings.icon,
            "repeated live size changes must apply in both directions");
    }
    frame.UnregisterPropertyChangedCallback(xaml::FrameworkElement::MinHeightProperty(),minToken);
    frame.UnregisterPropertyChangedCallback(xaml::FrameworkElement::MaxHeightProperty(),maxToken);
    require(!invalidRange,"live resizing temporarily crossed the previous XAML Min/Max range");
    auto color=background.Fill().as<xaml::Media::SolidColorBrush>().Color();
    require(color.A==64&&color.R==0x12&&color.G==0x34&&color.B==0x56,"background color and opacity");
    require(widgets.Visibility()==xaml::Visibility::Collapsed,"hide visual without registry");
    icon.Width(24);require(icon.Width()==40,"reapply after shell property changes");
    settings.enabled=0;styledFrame.apply(settings);styledIcon.apply(settings);styledBackground.apply(settings);styledWidgets.apply(settings);
    require(frame.Height()==48&&icon.Width()==24&&background.Fill()==original&&widgets.Visibility()==xaml::Visibility::Visible,"restore original values");
    icon.Width(28);require(icon.Width()==28,"remove callbacks on disable");
    icon.Width(24);
    // Explorer may set the natural icon size only after a recycled element is
    // discovered. UnsetValue is not a valid restoration baseline in that case.
    {
        xaml::Controls::Image late;StyledElement styled(late,Kind::Icon);
        settings.enabled=1;styled.apply(settings);late.Width(28);late.Height(28);
        require(late.Width()==40,"late shell update must remain styled");
        settings.enabled=0;styled.apply(settings);
        require(late.Width()==28&&late.Height()==28,"restore late Explorer dimensions instead of natural bitmap size");
    }
    {
        // Visual-state animations report effective-value changes while the
        // local value is still our override. It must never become the baseline.
        xaml::Controls::Image animated;animated.Width(24);animated.Height(24);
        animated.Source(xaml::Media::Imaging::WriteableBitmap(256,256));
        animated.MinWidth(64);animated.MinHeight(64);frame.Children().Append(animated);
        StyledElement styled(animated,Kind::Icon);settings.enabled=1;styled.apply(settings);
        root.UpdateLayout();
        require(animated.ActualWidth()==40&&animated.ActualHeight()==40,"constrain arranged bitmap size despite native minimums");
        int ownValueNotifications=0;
        auto observation=animated.RegisterPropertyChangedCallback(xaml::FrameworkElement::WidthProperty(),[&](auto const& sender,auto const& key){
            if(unbox_value<double>(sender.ReadLocalValue(key))==40) ownValueNotifications++;
        });
        xaml::Media::Animation::DoubleAnimation animation;
        animation.To(96);animation.EnableDependentAnimation(true);
        animation.Duration(xaml::DurationHelper::FromTimeSpan(std::chrono::milliseconds(50)));
        xaml::Media::Animation::Storyboard::SetTarget(animation,animated);
        xaml::Media::Animation::Storyboard::SetTargetProperty(animation,L"Width");
        xaml::Media::Animation::Storyboard storyboard;storyboard.Children().Append(animation);storyboard.Begin();
        until([&]{return animated.Width()==96;},"icon storyboard did not animate its effective width");
        root.UpdateLayout();
        require(ownValueNotifications>0,"exercise notification with our unchanged local override");
        require(animated.ActualWidth()==40&&animated.ActualHeight()==40,"animated icon stays inside configured layout size");
        storyboard.Stop();animated.UnregisterPropertyChangedCallback(xaml::FrameworkElement::WidthProperty(),observation);
        require(styled.restore(),"restore animated icon");
        require(animated.Width()==24&&animated.Height()==24&&animated.MinWidth()==64&&animated.MinHeight()==64,
            "animation must not replace native restoration values with our own overrides");
        uint32_t index=0;require(frame.Children().IndexOf(animated,index),"find animated test icon");frame.Children().RemoveAt(index);
    }
    {
        // Repeated diagnostic handles for the same object share one snapshot.
        xaml::Controls::Image recycled;recycled.Width(24);recycled.Height(24);
        auto first=std::make_unique<StyledElement>(recycled,Kind::Icon);settings.enabled=1;first->apply(settings);
        auto alias=std::make_unique<StyledElement>(recycled,Kind::Icon);alias->apply(settings);
        first.reset();
        require(recycled.Width()==40,"removing one diagnostic alias must not restore a still-owned icon");
        alias.reset();
        require(recycled.Width()==24&&recycled.Height()==24&&recycled.MinWidth()==0,
            "last alias restores native dimensions, never an earlier alias's override");
        StyledElement primary(recycled,Kind::Icon),secondary(recycled,Kind::Icon);
        primary.apply(settings);secondary.apply(settings);recycled.Width(28);
        primary.stopObserving();secondary.stopObserving();
        require(secondary.restore()&&primary.restore()&&recycled.Width()==28,"restore aliased snapshot exactly once in either order");
        primary.apply(settings);secondary.apply(settings);
        primary.stopObserving();secondary.stopObserving();
        require(primary.restore()&&secondary.restore()&&recycled.Width()==28,"shared snapshot can be reapplied after disable");
    }
    std::cout<<"PASS: direct XAML styling and restoration. Starting helper attachment."<<std::endl;
    {
        Host host;host.send("1 1 64 40 52 1193046 25 1 8\n");host.applied(1);
        require(frame.Height()==64&&icon.Width()==40,"external module changed real XAML elements");
        require(background.Fill().as<xaml::Media::SolidColorBrush>().Color().A==64,"external module background alpha");
        RECT actual{};GetWindowRect(window,&actual);require(actual.bottom-actual.top==MulDiv(64,GetDpiForWindow(window),96),"native taskbar window resized");
        int sequence=1;
        for(auto sizes:std::vector<std::pair<int,int>>{{80,48},{40,24},{96,64},{64,40}}) {
            sequence++;
            auto command=std::to_string(sequence)+" 1 "+std::to_string(sizes.first)+" "+std::to_string(sizes.second)+" "+std::to_string(std::max(52,sizes.second))+" 1193046 25 1 8\n";
            host.send(command.c_str());host.applied(sequence);
            MONITORINFO screen{sizeof(screen)};GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&screen);GetWindowRect(window,&actual);
            require(frame.Height()==sizes.first&&icon.Width()==sizes.second&&actual.bottom==screen.rcMonitor.bottom&&actual.bottom-actual.top==MulDiv(sizes.first,GetDpiForWindow(window),96),
                "live helper resize failed or moved the bottom edge off-screen");
            WINDOWPOS sizeOnly{window,nullptr,123,456,actual.right-actual.left,48,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE};
            SendMessageW(window,WM_WINDOWPOSCHANGING,0,reinterpret_cast<LPARAM>(&sizeOnly));
            require(!(sizeOnly.flags&SWP_NOMOVE)&&sizeOnly.y+sizeOnly.cy==screen.rcMonitor.bottom,
                "hook failed to anchor a native size-only request at the screen bottom");
        }
        host.send("6 0 64 40 52 1193046 25 1 8\n");host.applied(6);
        require(frame.Height()==48&&icon.Width()==24&&widgets.Visibility()==xaml::Visibility::Visible,"disable restores native XAML");
        host.send("7 1 64 40 52 1193046 25 1 8\n");host.applied(7);host.crash();
        until([&]{return frame.Height()==48&&icon.Width()==24;},"host crash did not restore XAML");
        std::cout<<"PASS: helper attachment and crash restoration."<<std::endl;
    }
    {
        Host restarted;restarted.send("1 1 60 32 48 3355443 50 0 0\n");restarted.applied(1);
        require(frame.Height()==60&&icon.Width()==32,"reconnect after host termination");
        // A running application can recreate its icon before the template has
        // assigned its name. Discover it after that first diagnostic callback.
        xaml::Controls::Image delayed;delayed.Width(24);delayed.Height(24);
        delayed.Source(xaml::Media::Imaging::WriteableBitmap(256,256));frame.Children().Append(delayed);
        auto delay=GetTickCount64()+250;until([&]{return GetTickCount64()>=delay;},"wait for deferred icon initialization");
        delayed.Name(L"Icon");
        until([&]{return delayed.Width()==32;},"module missed a late-named running application icon");
        root.UpdateLayout();require(delayed.ActualWidth()==32&&delayed.ActualHeight()==32,"late icon bitmap has the configured visible size");
        icon.Width(28);icon.Height(28);
        restarted.close();
        until([&]{return frame.Height()==48&&icon.Width()==28&&icon.Height()==28&&background.Fill()==original;},"normal helper exit did not restore latest shell sizes");
        root.UpdateLayout();
        require(delayed.Width()==24&&delayed.Height()==24&&delayed.ActualWidth()==24&&delayed.ActualHeight()==24,
            "normal exit restores the visible size of a late running application icon");
        require(delayed.MinWidth()==0&&std::isinf(delayed.MaxWidth()),"exit removes custom icon constraints");
        delayed.Width(20);require(delayed.Width()==20,"normal exit removes callbacks from late icons");
        uint32_t index=0;require(frame.Children().IndexOf(delayed,index),"find delayed test icon");frame.Children().RemoveAt(index);
        std::cout<<"PASS: helper reconnection. Closing XAML source."<<std::endl;
    }
    {
        // Explorer's layout can update a child when its parent is restored.
        // The module must not overwrite that final shell value afterwards.
        auto layout=frame.RegisterPropertyChangedCallback(xaml::FrameworkElement::HeightProperty(),[&](auto const&,auto const&){
            if(frame.Height()==48) {icon.Width(24);icon.Height(24);}
        });
        Host dependent;dependent.send("1 1 72 48 56 3355443 50 0 0\n");dependent.applied(1);
        icon.Width(32);icon.Height(32);dependent.close();
        until([&]{return frame.Height()==48&&icon.Width()==24&&icon.Height()==24;},"container restoration overwrote Explorer's recalculated icon size");
        frame.UnregisterPropertyChangedCallback(xaml::FrameworkElement::HeightProperty(),layout);
        std::cout<<"PASS: dependent icon layout restoration."<<std::endl;
    }
    source.Close();DestroyWindow(window);manager.Close();
    std::cout<<"PASS: native XAML sizes, color, opacity, visibility, shell reset and restoration; DPI geometry; IPC validation."<<std::endl;
    return 0;
} catch(const hresult_error& error) {std::cerr<<"WinRT error "<<std::hex<<static_cast<uint32_t>(error.code().value)<<std::endl;return 1;}
catch(const std::exception& error) {std::cerr<<error.what()<<std::endl;return 1;}
