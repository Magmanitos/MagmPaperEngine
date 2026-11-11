#include "styles.h"
#include <cmath>
#include <limits>
namespace magm {
using namespace winrt;
static bool sameLocalValue(xaml::DependencyProperty const& key,Windows::Foundation::IInspectable const& first,Windows::Foundation::IInspectable const& second) {
    if(first==second) return true;
    if(!first||!second) return false;
    if(first==xaml::DependencyProperty::UnsetValue()||second==xaml::DependencyProperty::UnsetValue()) return false;
    if(key==xaml::UIElement::VisibilityProperty())
        return unbox_value<xaml::Visibility>(first)==unbox_value<xaml::Visibility>(second);
    auto a=first.try_as<Windows::Foundation::IPropertyValue>(),b=second.try_as<Windows::Foundation::IPropertyValue>();
    if(a&&b&&a.Type()==Windows::Foundation::PropertyType::Double&&b.Type()==Windows::Foundation::PropertyType::Double) {
        double left=a.GetDouble(),right=b.GetDouble();
        return left==right||(std::isnan(left)&&std::isnan(right));
    }
    return false;
}
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
    if(name==L"Icon"&&(type==L"Windows.UI.Xaml.Controls.Image"||type==L"Windows.UI.Xaml.Controls.ImageIcon"||type==L"Microsoft.UI.Xaml.Controls.AnimatedVisualPlayer")) return Kind::Icon;
    return Kind::None;
}
StyledElement::Registry& StyledElement::registry() {
    // Diagnostics can report one XAML object under more than one handle during
    // reparenting. Those aliases must share exactly one native-value snapshot.
    static thread_local Registry values;return values;
}
StyledElement::StyledElement(xaml::FrameworkElement const& element,Kind type):kind(type) {
    auto owner=element.as<::IUnknown>();identity=reinterpret_cast<uintptr_t>(owner.get());
    auto& values=registry();
    if(auto found=values.find(identity);found!=values.end()) state=found->second.lock();
    if(!state||!state->target.get()) {state=std::make_shared<State>(element);values[identity]=state;}
}
std::shared_ptr<StyledElement::Property> StyledElement::capture(xaml::DependencyProperty const& key) {
    auto element=state->target.get();if(!element) return {};
    std::shared_ptr<Property> property;
    for(auto& p:state->properties) if(p->key==key) {property=p;break;}
    if(!property) {
        auto p=std::make_shared<Property>();p->key=key;p->original=element.ReadLocalValue(key);
        if(auto expression=element.GetBindingExpression(key)) p->binding=expression.ParentBinding();
        property=p;state->properties.push_back(p);
        std::weak_ptr<Property> saved=property;
        std::weak_ptr<bool> guard=state->writing;
        property->token=element.RegisterPropertyChangedCallback(key,[saved,guard](auto const& sender,auto const& changed){
            auto current=saved.lock();auto locked=guard.lock();
            if(!current||!locked||*locked||!current->desired) return;
            *locked=true;
            try {
                auto local=sender.ReadLocalValue(changed);
                // Visual-state animations change the effective value without
                // replacing our local override. Never save our own size as the
                // shell's baseline when an icon animates or changes state.
                if(sameLocalValue(changed,local,current->desired)) {*locked=false;return;}
                // Explorer initializes/rebinds recycled icons AFTER discovery.
                // Save its latest value before overriding it; restoring the
                // initial UnsetValue can otherwise expose a 256px bitmap.
                current->original=local;
                current->binding=nullptr;
                if(auto control=sender.template try_as<xaml::FrameworkElement>())
                    if(auto expression=control.GetBindingExpression(changed)) current->binding=expression.ParentBinding();
                sender.SetValue(changed,current->desired);
            } catch(...) {}
            *locked=false;
        });
        property->observing=true;
    }
    return property;
}
void StyledElement::set(xaml::DependencyProperty const& key,Windows::Foundation::IInspectable const& value) {
    auto element=state->target.get();if(!element) return;
    auto property=capture(key);if(!property) return;
    property->desired=value;*state->writing=true;
    try { element.SetValue(key,value); } catch(...) {*state->writing=false;throw;}
    *state->writing=false;
}
void StyledElement::dimension(xaml::DependencyProperty const& minimum,xaml::DependencyProperty const& maximum,xaml::DependencyProperty const& size,double value) {
    // Snapshot the complete native range before any layout write. Relax the
    // old range first so increasing/decreasing an already-styled element never
    // temporarily crosses its previous Min/Max constraint.
    auto low=capture(minimum),high=capture(maximum),extent=capture(size);
    auto element=state->target.get();if(!element) return;
    auto desired=box_value(value);
    if(low&&high&&extent&&sameLocalValue(minimum,low->desired,desired)&&sameLocalValue(maximum,high->desired,desired)&&sameLocalValue(size,extent->desired,desired)&&
        sameLocalValue(minimum,element.ReadLocalValue(minimum),desired)&&sameLocalValue(maximum,element.ReadLocalValue(maximum),desired)&&sameLocalValue(size,element.ReadLocalValue(size),desired)) return;
    set(maximum,box_value(std::numeric_limits<double>::infinity()));
    set(minimum,box_value(0.0));
    set(size,box_value(value));
    set(maximum,box_value(value));
    set(minimum,box_value(value));
}
void StyledElement::apply(const Settings& settings) {
    if(!settings.enabled) {if(!restore()) throw hresult_error(E_FAIL);return;}
    const double height=static_cast<double>(settings.height);
    switch(kind) {
    case Kind::Frame:case Kind::Content:case Kind::Tray:
        dimension(xaml::FrameworkElement::MinHeightProperty(),xaml::FrameworkElement::MaxHeightProperty(),xaml::FrameworkElement::HeightProperty(),height);break;
    case Kind::Icon:
        // Width/Height alone lose to minimum sizes and animated layout values.
        // Constrain the actual arranged size as well, retaining all originals.
        dimension(xaml::FrameworkElement::MinWidthProperty(),xaml::FrameworkElement::MaxWidthProperty(),xaml::FrameworkElement::WidthProperty(),static_cast<double>(settings.icon));
        dimension(xaml::FrameworkElement::MinHeightProperty(),xaml::FrameworkElement::MaxHeightProperty(),xaml::FrameworkElement::HeightProperty(),static_cast<double>(settings.icon));break;
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
    *state->writing=true;
    if(auto element=state->target.get()) for(auto& p:state->properties) {
        if(p->observing) try { element.UnregisterPropertyChangedCallback(p->key,p->token);p->observing=false; } catch(...) {}
    }
}
bool StyledElement::restore() noexcept {
    bool restored=true;
    stopObserving();
    if(auto element=state->target.get()) {
      auto minimum=[](auto const& key){return key==xaml::FrameworkElement::MinWidthProperty()||key==xaml::FrameworkElement::MinHeightProperty();};
      auto maximum=[](auto const& key){return key==xaml::FrameworkElement::MaxWidthProperty()||key==xaml::FrameworkElement::MaxHeightProperty();};
      // Drop only the constraints we own, then restore sizes, maxima and minima.
      for(auto& p:state->properties) try {
          if(minimum(p->key)) element.SetValue(p->key,box_value(0.0));
          else if(maximum(p->key)) element.SetValue(p->key,box_value(std::numeric_limits<double>::infinity()));
      } catch(...) {restored=false;}
      for(int phase=0;phase<3;phase++) for(auto& p:state->properties) {
        int order=minimum(p->key)?2:maximum(p->key)?1:0;if(order!=phase) continue;
        try {
            if(p->binding) xaml::Data::BindingOperations::SetBinding(element,p->key,p->binding);
            else if(p->original==xaml::DependencyProperty::UnsetValue()) element.ClearValue(p->key);
            else element.SetValue(p->key,p->original);
        } catch(...) {restored=false;}
      }
    }
    state->properties.clear();*state->writing=false;return restored;
}
StyledElement::~StyledElement() {
    if(state.use_count()!=1) return;
    restore();
    auto& values=registry();auto found=values.find(identity);
    if(found!=values.end()&&found->second.lock()==state) values.erase(found);
}
}
