#include "styles.h"
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Xaml.Input.h>
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
com_ptr<IDesktopWindowXamlSourceNative2> inputSource;
int deferResizeAtHeight=0;
LRESULT CALLBACK surface(HWND hwnd,UINT message,WPARAM wp,LPARAM lp) {
    if(message==WM_SIZE&&island) SetWindowPos(island,nullptr,0,0,LOWORD(lp),HIWORD(lp),SWP_NOZORDER|SWP_NOACTIVATE);
    auto result=DefWindowProcW(hwnd,message,wp,lp);
    if(message==WM_WINDOWPOSCHANGED&&deferResizeAtHeight) {
        auto pos=*reinterpret_cast<WINDOWPOS*>(lp);
        if(!(pos.flags&SWP_NOSIZE)&&pos.cy!=deferResizeAtHeight) {
            int oldHeight=deferResizeAtHeight;deferResizeAtHeight=0;
            // Model a shell layout pass that temporarily restores its old height.
            SetWindowPos(hwnd,nullptr,pos.x,pos.y+pos.cy-oldHeight,pos.cx,oldHeight,SWP_NOSENDCHANGING|SWP_NOZORDER|SWP_NOACTIVATE);
        }
    }
    return result;
}
void pump(){
    MSG message{};
    while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) {
        BOOL handled=FALSE;
        if(inputSource) inputSource->PreTranslateMessage(&message,&handled);
        if(!handled) {TranslateMessage(&message);DispatchMessageW(&message);}
    }
}
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
    settings.iconEffect=4;require(!valid(settings),"reject invalid animation flag");settings.iconEffect=0;
    settings.glassOutline=2;require(!valid(settings),"reject invalid glass outline flag");settings.glassOutline=1;
    RECT monitor{-1920,0,0,1080};auto rect=barRect(monitor,144,64);
    require(rect.top==984&&rect.left==-1920&&rect.right==0,"physical geometry and negative monitor origin");
    WINDOWPOS proposed{};proposed.flags=SWP_NOMOVE;proposed.x=123;proposed.y=456;proposed.cx=1920;proposed.cy=48;
    anchorBarPosition(proposed,RECT{-1920,1008,0,1080},monitor,144,64,false);
    require(!(proposed.flags&SWP_NOMOVE)&&proposed.x==-1920&&proposed.y==984&&proposed.cy==96,
        "size-only requests must grow up from the monitor bottom, ignoring undefined x/y");
    proposed.flags=SWP_NOSIZE;proposed.x=-1920;proposed.y=1078;
    anchorBarPosition(proposed,RECT{-1920,984,0,1080},monitor,144,80,true);
    require(proposed.y==1078&&proposed.cy==120&&proposed.cx==1920,"keep auto-hidden taskbar at its reveal strip");
    proposed.flags=SWP_NOSIZE;proposed.x=-1920;proposed.y=1024;
    anchorBarPosition(proposed,RECT{-1920,984,0,1080},monitor,144,64,true);
    require(proposed.y==1024&&proposed.cy==96,"auto-hide animation can move through intermediate positions");
    proposed.flags=SWP_NOMOVE;proposed.cx=1920;
    anchorBarPosition(proposed,RECT{-1920,1024,0,1120},monitor,144,80,true);
    require(proposed.y==1000&&proposed.cy==120,"height change preserves the current auto-hide offset");
    proposed.flags=SWP_NOMOVE;proposed.cx=1920;
    anchorBarPosition(proposed,RECT{-1920,1078,0,1174},monitor,144,80,true);
    require(proposed.y==1078&&proposed.cy==120,"resizing a fully hidden taskbar preserves the reveal strip");
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
    inputSource=source.as<IDesktopWindowXamlSourceNative2>();
    check_hresult(native->get_WindowHandle(&island));SetWindowPos(island,nullptr,0,0,500,48,SWP_SHOWWINDOW);
    xaml::Controls::Grid root;
    xaml::Controls::Grid frame;frame.Name(L"MagmPaper.Test.Frame");frame.Height(48);frame.MinHeight(0);root.Children().Append(frame);
    xaml::Controls::Image icon;icon.Name(L"Icon");icon.Width(24);icon.Height(24);frame.Children().Append(icon);
    xaml::Shapes::Rectangle background;background.Name(L"BackgroundFill");auto original=xaml::Media::SolidColorBrush(Windows::UI::Color{255,20,30,40});background.Fill(original);frame.Children().Append(background);
    xaml::Controls::Button widgets;widgets.Name(L"MagmPaper.Test.Widgets");frame.Children().Append(widgets);
    xaml::Controls::Grid button;button.Name(L"MagmPaper.Test.Button");button.Width(44);button.MaxWidth(48);frame.Children().Append(button);
    source.Content(root);
    until([&]{return root.IsLoaded();},"test XAML content did not load");
    StyledElement styledFrame(frame,Kind::Frame),styledIcon(icon,Kind::Icon),styledBackground(background,Kind::Background),styledWidgets(widgets,Kind::Widgets),styledButton(button,Kind::Button);
    settings.enabled=1;settings.height=64;settings.icon=40;settings.button=52;settings.opacity=25;settings.color=0x123456;settings.hideWidgets=1;settings.radius=8;
    styledFrame.apply(settings);styledIcon.apply(settings);styledBackground.apply(settings);styledWidgets.apply(settings);styledButton.apply(settings);root.UpdateLayout();
    require(frame.Height()==64&&frame.MaxHeight()==64&&icon.Width()==40,"native XAML dimensions");
    require(button.ActualWidth()==52&&button.MaxWidth()==52,"button width can grow beyond the old XAML maximum");
    settings.button=44;styledButton.apply(settings);root.UpdateLayout();
    require(button.ActualWidth()==44&&button.MaxWidth()==44,"button width can shrink while styling stays enabled");
    require(styledButton.restore()&&button.Width()==44&&button.MaxWidth()==48,"restore the original button constraints");
    settings.button=52;
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
        Host host;host.send("1 1 64 40 52 1193046 25 1 8 0 0 0 1\n");host.applied(1);
        require(frame.Height()==64&&icon.Width()==40,"external module changed real XAML elements");
        require(background.Fill().as<xaml::Media::SolidColorBrush>().Color().A==64,"external module background alpha");
        RECT actual{};GetWindowRect(window,&actual);require(actual.bottom-actual.top==MulDiv(64,GetDpiForWindow(window),96),"native taskbar window resized");
        int sequence=1;
        deferResizeAtHeight=actual.bottom-actual.top;
        for(auto sizes:std::vector<std::pair<int,int>>{{80,48},{40,24},{96,64},{64,40}}) {
            sequence++;
            auto command=std::to_string(sequence)+" 1 "+std::to_string(sizes.first)+" "+std::to_string(sizes.second)+" "+std::to_string(std::max(52,sizes.second))+" 1193046 25 1 8 0 0 0 1\n";
            host.send(command.c_str());host.applied(sequence);
            require(deferResizeAtHeight==0,"the simulated shell layout did not defer the resize");
            MONITORINFO screen{sizeof(screen)};GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&screen);GetWindowRect(window,&actual);
            require(frame.Height()==sizes.first&&icon.Width()==sizes.second&&actual.bottom==screen.rcMonitor.bottom&&actual.bottom-actual.top==MulDiv(sizes.first,GetDpiForWindow(window),96),
                "live helper resize failed or moved the bottom edge off-screen");
            WINDOWPOS sizeOnly{window,nullptr,123,456,actual.right-actual.left,48,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE};
            SendMessageW(window,WM_WINDOWPOSCHANGING,0,reinterpret_cast<LPARAM>(&sizeOnly));
            require(!(sizeOnly.flags&SWP_NOMOVE)&&sizeOnly.y+sizeOnly.cy==screen.rcMonitor.bottom,
                "hook failed to anchor a native size-only request at the screen bottom");
        }
        host.send("6 1 64 40 52 1193046 25 1 8 1 11259375 0 1\n");host.applied(6);
        namespace composition=Windows::UI::Composition;
        auto glass=xaml::Hosting::ElementCompositionPreview::GetElementChildVisual(background).as<composition::ContainerVisual>();
        require(glass.Opacity()==.25f&&glass.Children().Count()==4&&background.Fill().as<xaml::Media::SolidColorBrush>().Color().A==0,
            "glass must layer the material and leave the underlying XAML shape transparent");
        auto clip=glass.Clip().as<composition::CompositionGeometricClip>().Geometry().as<composition::CompositionRoundedRectangleGeometry>();
        require(clip.CornerRadius().x==8.f,"glass clip did not follow the configured rounding");
        bool lens=false,secondTint=false;
        composition::ShapeVisual rims{nullptr};
        for(auto child:glass.Children()) {
            if(auto sprite=child.try_as<composition::SpriteVisual>()) {
                auto brush=sprite.Brush();
                if(!sprite.Comment().empty()) std::wcout<<sprite.Comment().c_str()<<std::endl;
                if(brush.try_as<composition::CompositionEffectBrush>()) {
                    auto layer=brush.Comment();
                    lens|=layer==L"MagmPaper.Glass.Lens"||(layer==L"MagmPaper.Glass.Blur"&&sprite.Scale().y>1);
                }
                if(auto tint=brush.try_as<composition::CompositionLinearGradientBrush>();tint&&tint.ColorStops().Size()==5) {
                    auto sampledTint=tint.ColorStops().GetAt(3).Color();
                    secondTint|=sampledTint.R==0xab&&sampledTint.G==0xcd&&sampledTint.B==0xef;
                }
            }
            if(auto shape=child.try_as<composition::ShapeVisual>()) rims=shape;
        }
        require(lens,"glass did not create the blurred lens brush");
        require(secondTint&&rims&&rims.Shapes().Size()==2&&rims.IsVisible(),"glass did not preserve the two tints and two reflective rims");
        host.send("7 1 64 40 52 1193046 25 1 0 1 11259375 0 1\n");host.applied(7);
        require(clip.CornerRadius().x==0&&background.RadiusX()==0&&background.RadiusY()==0,
            "radius zero must make the glass and native rectangle square");
        host.send("8 1 64 40 52 1193046 25 1 0 1 11259375 0 0\n");host.applied(8);
        require(!rims.IsVisible()&&glass.Opacity()==.25f&&xaml::Hosting::ElementCompositionPreview::GetElementChildVisual(background)==glass,
            "hiding the outline must preserve the glass material and opacity");
        host.send("9 1 64 40 52 1193046 25 1 0 1 11259375 0 1\n");host.applied(9);
        require(rims.IsVisible(),"the outline must reappear when enabled");
        host.send("10 1 64 40 52 1193046 25 1 0 2 11259375 0 1\n");host.applied(10);
        auto gradient=background.Fill().as<xaml::Media::LinearGradientBrush>();
        auto stops=gradient.GradientStops();
        require(!xaml::Hosting::ElementCompositionPreview::GetElementChildVisual(background)&&stops.Size()==2&&
            stops.GetAt(0).Color().R==0x12&&stops.GetAt(0).Color().A==64&&
            stops.GetAt(1).Color().R==0xab&&stops.GetAt(1).Color().A==64,
            "plain gradients must use the exact tints and opacity after removing the glass");
        host.send("11 1 64 40 52 1193046 0 1 0 1 11259375 0 1\n");host.applied(11);
        require(!xaml::Hosting::ElementCompositionPreview::GetElementChildVisual(background)&&
            background.Fill().as<xaml::Media::SolidColorBrush>().Color().A==0,
            "zero opacity must remove the glass");
        host.send("12 0 64 40 52 1193046 25 1 8 0 0 0 1\n");host.applied(12);
        require(frame.Height()==48&&icon.Width()==24&&widgets.Visibility()==xaml::Visibility::Visible,"disable restores native XAML");
        host.send("13 1 64 40 52 1193046 25 1 8 0 0 0 1\n");host.applied(13);host.crash();
        until([&]{return frame.Height()==48&&icon.Width()==24;},"host crash did not restore XAML");
        std::cout<<"PASS: helper attachment and crash restoration."<<std::endl;
    }
    {
        Host restarted;restarted.send("1 1 60 32 48 3355443 50 0 0 0 0 0 1\n");restarted.applied(1);
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
        Host dependent;dependent.send("1 1 72 48 56 3355443 50 0 0 0 0 0 1\n");dependent.applied(1);
        icon.Width(32);icon.Height(32);dependent.close();
        until([&]{return frame.Height()==48&&icon.Width()==24&&icon.Height()==24;},"container restoration overwrote Explorer's recalculated icon size");
        frame.UnregisterPropertyChangedCallback(xaml::FrameworkElement::HeightProperty(),layout);
        std::cout<<"PASS: dependent icon layout restoration."<<std::endl;
    }
    {
        // Two full-width hit targets, with an existing native transform on one image.
        xaml::Controls::Grid left,right;
        xaml::Controls::Image first,second;
        xaml::Media::TranslateTransform originalMove;originalMove.X(2);
        first.RenderTransform(originalMove);
        auto addButton=[&](xaml::Controls::Grid const& cell,xaml::Controls::Image const& image,double x) {
            cell.Name(L"MagmPaper.Test.Button");cell.Width(44);
            cell.HorizontalAlignment(xaml::HorizontalAlignment::Left);cell.Margin({x,0,0,0});
            cell.Background(xaml::Media::SolidColorBrush(Windows::UI::Color{255,0x22,0x23,0x2b}));
            image.Name(L"Icon");image.Width(24);image.Height(24);
            cell.Children().Append(image);frame.Children().Append(cell);
        };
        addButton(left,first,20);addButton(right,second,90);
        root.UpdateLayout();
        POINT cursor{};GetCursorPos(&cursor);
        SetWindowPos(window,HWND_TOPMOST,80,80,0,0,SWP_NOSIZE|SWP_SHOWWINDOW);
        SetForegroundWindow(window);
        RECT originalBounds{};GetWindowRect(window,&originalBounds);
        int pointerEvents=0;
        for(auto cell:{left,right}) cell.PointerEntered([&](auto const&,auto const&){pointerEvents++;});
        auto pointAt=[&](LONG x) {
            POINT point{MulDiv(x,GetDpiForWindow(island),96),MulDiv(24,GetDpiForWindow(island),96)};
            ClientToScreen(island,&point);
            const int originX=GetSystemMetrics(SM_XVIRTUALSCREEN),originY=GetSystemMetrics(SM_YVIRTUALSCREEN);
            INPUT input{};input.type=INPUT_MOUSE;
            input.mi.dx=MulDiv(point.x-originX,65535,GetSystemMetrics(SM_CXVIRTUALSCREEN)-1);
            input.mi.dy=MulDiv(point.y-originY,65535,GetSystemMetrics(SM_CYVIRTUALSCREEN)-1);
            input.mi.dwFlags=MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK;
            require(SendInput(1,&input,sizeof(INPUT))==1,"move the cursor across taskbar test buttons");
            HWND at=WindowFromPoint(point);
            require(at==window||IsChild(window,at),"pointer target is outside the test XAML window");
        };
        auto effect=[](xaml::Controls::Image const& image) {
            auto transforms=image.RenderTransform().as<xaml::Media::TransformGroup>().Children();
            return transforms.GetAt(transforms.Size()-1).as<xaml::Media::CompositeTransform>();
        };
        auto offset=[&](xaml::Controls::Image const& image){return effect(image).TranslateY();};
        pointAt(170);
        Host animated;animated.send("1 0 48 24 44 3355443 50 0 0 0 0 1 1\n");animated.applied(1);
        until([&]{return first.RenderTransform().try_as<xaml::Media::TransformGroup>() &&
            second.RenderTransform().try_as<xaml::Media::TransformGroup>();},"animation-only mode did not attach to both images");
        require(first.RenderTransform().as<xaml::Media::TransformGroup>().Children().GetAt(0)==originalMove,
            "hover transform must retain the original transform");
        RECT sameBounds{};GetWindowRect(window,&sameBounds);
        require(EqualRect(&originalBounds,&sameBounds)&&frame.Height()==48&&left.ActualWidth()==44&&background.Fill()==original,
            "animation alone must keep native taskbar geometry and colors");
        pointAt(32);
        until([&]{return pointerEvents>0;},"the XAML island did not receive the pointer enter event");
        BOOL motion=TRUE;SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION,0,&motion,0);
        if(motion) until([&]{const auto y=offset(first);return y<0&&y>-5;},"hover must interpolate instead of jumping");
        else until([&]{return offset(first)==-5;},"reduced motion should lift without interpolation");
        require(offset(second)==0,"the neighboring icon lifted before entering its button");
        pointAt(103);
        until([&]{return offset(first)==0&&offset(second)==-5;},"hover transfer did not lower the first icon and lift the next one");
        pointAt(170);
        until([&]{return offset(second)==0;},"leaving the buttons did not lower the icon");

        animated.send("2 0 48 24 44 3355443 50 0 0 0 0 2 1\n");animated.applied(2);
        pointAt(32);
        until([&]{return offset(first)==-5&&effect(second).Rotation()==-8;},"carousel did not tilt its right neighbor");
        pointAt(103);
        until([&]{return offset(second)==-5&&effect(first).Rotation()==8&&effect(second).Rotation()==0;},"carousel did not transfer to the neighboring icon");
        animated.send("3 0 48 24 44 3355443 50 0 0 0 0 3 1\n");animated.applied(3);
        try {
            until([&]{return std::abs(effect(second).ScaleX()-1.14)<.001&&effect(first).ScaleX()==1&&effect(first).Rotation()==0&&offset(second)==0;},
                "zoom changed a neighbor or did not reset the carousel pose");
        } catch(...) {
            std::cerr<<"Zoom state: selected scale "<<effect(second).ScaleX()<<", lift "<<offset(second)
                <<", neighbor scale "<<effect(first).ScaleX()<<", angle "<<effect(first).Rotation()<<std::endl;
            throw;
        }

        xaml::Media::ScaleTransform shellMove;shellMove.ScaleX(1.1);
        first.RenderTransform(shellMove);
        animated.send("4 0 48 24 44 3355443 50 0 0 0 0 0 1\n");animated.applied(4);
        require(first.RenderTransform()==shellMove&&!second.RenderTransform().try_as<xaml::Media::TransformGroup>(),
            "disabling animation must restore the latest native transform");
        animated.close();
        SetCursorPos(cursor.x,cursor.y);
        SetWindowPos(window,HWND_NOTOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE);
        for(auto cell:{left,right}) {
            uint32_t index=0;require(frame.Children().IndexOf(cell,index),"find animation test button");frame.Children().RemoveAt(index);
        }
        std::cout<<"PASS: taskbar hover motion, adjacent button transfer and cleanup."<<std::endl;
    }
    inputSource=nullptr;source.Close();DestroyWindow(window);manager.Close();
    std::cout<<"PASS: native XAML sizes, color, opacity, visibility, shell reset and restoration; DPI geometry; IPC validation."<<std::endl;
    return 0;
} catch(const hresult_error& error) {std::cerr<<"WinRT error "<<std::hex<<static_cast<uint32_t>(error.code().value)<<std::endl;return 1;}
catch(const std::exception& error) {std::cerr<<error.what()<<std::endl;return 1;}
