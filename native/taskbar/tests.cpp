#include "styles.h"
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#include <iostream>
#include <stdexcept>

using namespace magm;
using namespace winrt;
void require(bool value,const char* message) {if(!value) throw std::runtime_error(message);}
int main() try {
    Settings settings{};settings.sequence=1;require(valid(settings),"valid default settings");
    settings.icon=64;require(!valid(settings),"reject oversized icon");settings.icon=24;
    RECT monitor{-1920,0,0,1080};auto rect=barRect(monitor,144,64);
    require(rect.top==984&&rect.left==-1920&&rect.right==0,"physical geometry and negative monitor origin");
    Shared shared{};publish(&shared,settings);Settings readback{};require(read(&shared,readback)&&readback.icon==24,"shared protocol roundtrip");
    require(classify(L"Taskbar.TaskbarFrame",L"",false)==Kind::Frame,"frame detection");
    require(classify(L"Windows.UI.Xaml.Shapes.Rectangle",L"BackgroundFill",false)==Kind::None,"do not style unrelated windows");
    init_apartment(apartment_type::single_threaded);
    auto manager=xaml::Hosting::WindowsXamlManager::InitializeForCurrentThread();
    HWND window=CreateWindowExW(0,L"STATIC",L"MagmPaper native test",WS_OVERLAPPEDWINDOW|WS_VISIBLE,0,0,500,180,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
    require(window!=nullptr,"test window");
    auto source=xaml::Hosting::DesktopWindowXamlSource();
    auto native=source.as<IDesktopWindowXamlSourceNative>();check_hresult(native->AttachToWindow(window));
    HWND child=nullptr;check_hresult(native->get_WindowHandle(&child));SetWindowPos(child,nullptr,0,0,480,140,SWP_SHOWWINDOW);
    xaml::Controls::Grid root;
    xaml::Controls::Grid frame;frame.Height(48);frame.MinHeight(0);root.Children().Append(frame);
    xaml::Controls::Image icon;icon.Width(24);icon.Height(24);frame.Children().Append(icon);
    xaml::Shapes::Rectangle background;auto original=xaml::Media::SolidColorBrush(Windows::UI::Color{255,20,30,40});background.Fill(original);frame.Children().Append(background);
    xaml::Controls::Button widgets;frame.Children().Append(widgets);
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
    source.Close();DestroyWindow(window);manager.Close();
    std::cout<<"PASS: native XAML sizes, color, opacity, visibility, shell reset and restoration; DPI geometry; IPC validation."<<std::endl;
    return 0;
} catch(const hresult_error& error) {std::cerr<<"WinRT error "<<std::hex<<static_cast<uint32_t>(error.code().value)<<std::endl;return 1;}
catch(const std::exception& error) {std::cerr<<error.what()<<std::endl;return 1;}
