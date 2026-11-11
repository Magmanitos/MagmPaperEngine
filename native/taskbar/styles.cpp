#include "styles.h"
namespace magm {
using namespace winrt;
Kind classify(const std::wstring& type,const std::wstring& name,bool inTaskbar) {
#ifdef MAGM_HARNESS
    if(name==L"MagmPaper.Test.Frame") return Kind::Frame;
    if(name==L"MagmPaper.Test.Widgets") return Kind::Widgets;
    if(name==L"MagmPaper.Test.Button") return Kind::Button;
#endif
    if(type==L"Taskbar.TaskbarFrame") return Kind::Frame;
    if(type==L"SystemTray.SystemTrayFrame") return Kind::Tray;
    if(!inTaskbar) return Kind::None;
    if(type==L"Taskbar.AugmentedEntryPointButton") return Kind::Widgets;
    if(type==L"Taskbar.TaskListButton") return Kind::Button;
    if(name==L"BackgroundFill"&&type==L"Windows.UI.Xaml.Shapes.Rectangle") return Kind::Background;
    if(name==L"BackgroundStroke"&&type==L"Windows.UI.Xaml.Shapes.Rectangle") return Kind::Stroke;
    if(name==L"Icon"&&(type==L"Windows.UI.Xaml.Controls.Image"||type==L"Microsoft.UI.Xaml.Controls.AnimatedVisualPlayer")) return Kind::Icon;
    return Kind::None;
}
void StyledElement::set(xaml::DependencyProperty const& key,Windows::Foundation::IInspectable const& value) {
    auto element=target.get();if(!element) return;
    std::shared_ptr<Property> property;
    for(auto& p:properties) if(p->key==key) {property=p;break;}
    if(!property) {
        auto p=std::make_shared<Property>();p->key=key;p->original=element.ReadLocalValue(key);
        if(auto expression=element.GetBindingExpression(key)) p->binding=expression.ParentBinding();
        property=p;properties.push_back(p);
        std::weak_ptr<Property> saved=property;
        std::weak_ptr<bool> guard=writing;
        property->token=element.RegisterPropertyChangedCallback(key,[saved,guard](auto const& sender,auto const& changed){
            auto current=saved.lock();auto locked=guard.lock();
            if(!current||!locked||*locked||!current->desired) return;
            *locked=true;
            try {
                // Explorer initializes/rebinds recycled icons AFTER discovery.
                // Save its latest value before overriding it; restoring the
                // initial UnsetValue can otherwise expose a 256px bitmap.
                current->original=sender.ReadLocalValue(changed);
                current->binding=nullptr;
                if(auto control=sender.template try_as<xaml::FrameworkElement>())
                    if(auto expression=control.GetBindingExpression(changed)) current->binding=expression.ParentBinding();
                sender.SetValue(changed,current->desired);
            } catch(...) {}
            *locked=false;
        });
        property->observing=true;
    }
    property->desired=value;*writing=true;
    try { element.SetValue(key,value); } catch(...) {*writing=false;throw;}
    *writing=false;
}
void StyledElement::apply(const Settings& settings) {
    if(!settings.enabled) {if(!restore()) throw hresult_error(E_FAIL);return;}
    const double height=static_cast<double>(settings.height);
    switch(kind) {
    case Kind::Frame:case Kind::Content:case Kind::Tray:
        set(xaml::FrameworkElement::MinHeightProperty(),box_value(height));
        set(xaml::FrameworkElement::MaxHeightProperty(),box_value(height));
        set(xaml::FrameworkElement::HeightProperty(),box_value(height));break;
    case Kind::Icon:
        set(xaml::FrameworkElement::WidthProperty(),box_value(static_cast<double>(settings.icon)));
        set(xaml::FrameworkElement::HeightProperty(),box_value(static_cast<double>(settings.icon)));break;
    case Kind::Button:
        set(xaml::FrameworkElement::MinWidthProperty(),box_value(static_cast<double>(settings.button)));
        set(xaml::FrameworkElement::WidthProperty(),box_value(static_cast<double>(settings.button)));break;
    case Kind::Background: {
        Windows::UI::Color color{static_cast<uint8_t>((settings.opacity*255+50)/100),
            static_cast<uint8_t>(settings.color>>16),static_cast<uint8_t>(settings.color>>8),static_cast<uint8_t>(settings.color)};
        set(xaml::Shapes::Shape::FillProperty(),xaml::Media::SolidColorBrush(color));
        set(xaml::Shapes::Rectangle::RadiusXProperty(),box_value(static_cast<double>(settings.radius)));
        set(xaml::Shapes::Rectangle::RadiusYProperty(),box_value(static_cast<double>(settings.radius)));break;
    }
    case Kind::Stroke:set(xaml::UIElement::OpacityProperty(),box_value(0.0));break;
    case Kind::Widgets:
        if(settings.hideWidgets) set(xaml::UIElement::VisibilityProperty(),box_value(xaml::Visibility::Collapsed));
        else restore();break;
    default:break;
    }
}
void StyledElement::stopObserving() noexcept {
    *writing=true;
    if(auto element=target.get()) for(auto& p:properties) {
        if(p->observing) try { element.UnregisterPropertyChangedCallback(p->key,p->token);p->observing=false; } catch(...) {}
    }
}
bool StyledElement::restore() noexcept {
    bool restored=true;
    stopObserving();
    if(auto element=target.get()) for(auto& p:properties) {
        try {
            if(p->binding) xaml::Data::BindingOperations::SetBinding(element,p->key,p->binding);
            else if(p->original==xaml::DependencyProperty::UnsetValue()) element.ClearValue(p->key);
            else element.SetValue(p->key,p->original);
        } catch(...) {restored=false;}
    }
    properties.clear();*writing=false;return restored;
}
StyledElement::~StyledElement() {restore();}
}
