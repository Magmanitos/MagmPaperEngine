#include "styles.h"
#include <winrt/Windows.Devices.Input.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Xaml.Media.Animation.h>
#include <chrono>
#include <cmath>
#include <limits>
#include <optional>
namespace magm {
using namespace winrt;
namespace media=xaml::Media;
namespace animation=media::Animation;

enum class IconEffect:uint32_t { Off, Lift, Carousel, Zoom };
struct IconPose {
    double y=0, scale=1, angle=0;
    bool operator==(IconPose const&) const=default;
};
static thread_local std::vector<IconLift*> icons;

// Hover belongs to the stable button. Only its image moves, so the
// button never moves away from the pointer that triggered the animation.
struct IconLift {
    weak_ref<xaml::FrameworkElement> image, button, frame;
    media::CompositeTransform offset;
    media::TransformGroup transform{nullptr};
    animation::Storyboard transition{nullptr};
    Windows::Foundation::IInspectable original{nullptr};
    xaml::Data::BindingBase binding{nullptr};
    event_token entered{}, moved{}, exited{}, unloaded{};
    int64_t transformChanged=0;
    bool observesTransform=false, hovered=false;
    double distance=5;
    IconEffect effect=IconEffect::Lift;
    IconPose destination{};

    IconLift(xaml::FrameworkElement const& icon,xaml::FrameworkElement const& owner,xaml::FrameworkElement const& bar):image(icon),button(owner),frame(bar) {}

    void attach(const std::shared_ptr<IconLift>& self) {
        auto icon=image.get(), owner=button.get();
        if(!icon||!owner) return;
        std::weak_ptr<IconLift> weak=self;
        auto enter=[weak](auto const&,xaml::Input::PointerRoutedEventArgs const& args) {
            if(auto lift=weak.lock();lift&&args.Pointer().PointerDeviceType()==Windows::Devices::Input::PointerDeviceType::Mouse)
                lift->setHovered(true);
        };
        entered=owner.PointerEntered(enter);
        moved=owner.PointerMoved(enter); // Also handles a template replaced under a stationary button.
        exited=owner.PointerExited([weak](auto const&,auto const&) {if(auto lift=weak.lock()) lift->setHovered(false);});
        unloaded=icon.Unloaded([weak](auto const&,auto const&) {if(auto lift=weak.lock()) lift->setHovered(false,false);});
        transformChanged=icon.RegisterPropertyChangedCallback(xaml::UIElement::RenderTransformProperty(),[weak](auto const&,auto const&) {
            if(auto lift=weak.lock()) try { lift->attachTransform(); } catch(...) {}
        });
        observesTransform=true;
        attachTransform();
        icons.push_back(this);
    }

    void attachTransform() {
        auto icon=image.get();if(!icon) return;
        auto native=icon.RenderTransform();
        if(transform&&native==transform) return;
        auto key=xaml::UIElement::RenderTransformProperty();
        original=icon.ReadLocalValue(key);
        auto expression=icon.GetBindingExpression(key);
        binding=expression?expression.ParentBinding():nullptr;
        transform=media::TransformGroup();
        if(native) transform.Children().Append(native);
        transform.Children().Append(offset);
        icon.RenderTransform(transform);
    }

    void setHovered(bool over,bool animate=true) noexcept {
        if(hovered==over&&animate) return;
        hovered=over;
        try {
            if(auto bar=frame.get()) {
                if(over) for(auto other:icons) if(other!=this&&other->frame.get()==bar) other->hovered=false;
                refresh(bar,animate);
            }
        } catch(...) {}
    }

    std::optional<float> center(xaml::FrameworkElement const& bar) {
        auto owner=button.get();
        if(!owner||!owner.IsLoaded()||owner.Visibility()!=xaml::Visibility::Visible||owner.ActualWidth()<=0) return {};
        try { return owner.TransformToVisual(bar).TransformPoint({static_cast<float>(owner.ActualWidth()/2),0}).X; }
        catch(...) { return {}; }
    }

    static void refresh(xaml::FrameworkElement const& bar,bool animate=true) {
        auto match=std::find_if(icons.begin(),icons.end(),[&](auto icon){return icon->hovered&&icon->frame.get()==bar;});
        auto selected=match==icons.end()?nullptr:*match;
        IconLift* before=nullptr;
        IconLift* after=nullptr;
        if(selected&&selected->effect==IconEffect::Carousel) if(auto x=selected->center(bar)) {
            float left=-std::numeric_limits<float>::infinity(),right=std::numeric_limits<float>::infinity();
            for(auto icon:icons) if(icon->frame.get()==bar) if(auto position=icon->center(bar)) {
                if(*position<*x&&*position>left) {left=*position;before=icon;}
                if(*position>*x&&*position<right) {right=*position;after=icon;}
            }
        }
        for(auto icon:icons) if(icon->frame.get()==bar) {
            IconPose pose{};
            if(icon==selected)
                pose=icon->effect==IconEffect::Zoom?IconPose{0,1.14,0}:IconPose{-icon->distance,1,0};
            else if(icon==before||icon==after) pose={-.36*icon->distance,.97,icon==before?8.0:-8.0};
            icon->move(pose,animate);
        }
    }

    void move(IconPose target,bool animate) noexcept {
        if(destination==target&&animate) return;
        destination=target;
        try {
            if(auto icon=image.get()) {
                auto origin=icon.RenderTransformOrigin();
                offset.CenterX(icon.ActualWidth()*(.5-origin.X));
                offset.CenterY(icon.ActualHeight()*(.5-origin.Y));
            }
            double previousY=offset.TranslateY(),previousXScale=offset.ScaleX(),previousYScale=offset.ScaleY(),previousAngle=offset.Rotation();
            if(transition) {transition.Stop();transition=nullptr;}
            offset.TranslateY(target.y);offset.ScaleX(target.scale);offset.ScaleY(target.scale);offset.Rotation(target.angle);
            BOOL motion=TRUE;
            SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION,0,&motion,0);
            if(!animate||!motion) return;

            auto duration=xaml::DurationHelper::FromTimeSpan(std::chrono::milliseconds(target==IconPose{}?170:120));
            animation::CubicEase ease;
            ease.EasingMode(animation::EasingMode::EaseOut);
            transition=animation::Storyboard();
            auto append=[&](double start,double end,wchar_t const* property) {
                if(start==end) return;
                animation::DoubleAnimation change;
                change.From(start);change.To(end);change.Duration(duration);
                change.FillBehavior(animation::FillBehavior::Stop);
                change.EnableDependentAnimation(true);
                change.EasingFunction(ease);
                animation::Storyboard::SetTarget(change,offset);
                animation::Storyboard::SetTargetProperty(change,property);
                transition.Children().Append(change);
            };
            append(previousY,target.y,L"TranslateY");
            append(previousXScale,target.scale,L"ScaleX");
            append(previousYScale,target.scale,L"ScaleY");
            append(previousAngle,target.angle,L"Rotation");
            if(transition.Children().Size()) transition.Begin();
        } catch(...) {}
    }

    ~IconLift() noexcept {
        std::erase(icons,this);
        if(auto owner=button.get()) try {
            if(entered.value) owner.PointerEntered(entered);
            if(moved.value) owner.PointerMoved(moved);
            if(exited.value) owner.PointerExited(exited);
        } catch(...) {}
        if(auto icon=image.get()) try {
            if(unloaded.value) icon.Unloaded(unloaded);
            auto key=xaml::UIElement::RenderTransformProperty();
            if(observesTransform) icon.UnregisterPropertyChangedCallback(key,transformChanged);
            if(transition) transition.Stop();
            if(!transform||icon.RenderTransform()!=transform) return;
            if(binding) xaml::Data::BindingOperations::SetBinding(icon,key,binding);
            else if(original==xaml::DependencyProperty::UnsetValue()) icon.ClearValue(key);
            else icon.SetValue(key,original);
        } catch(...) {}
    }
};

void refreshIconEffects() {
    std::vector<xaml::FrameworkElement> frames;
    for(auto icon:icons) if(auto bar=icon->frame.get();bar&&std::find(frames.begin(),frames.end(),bar)==frames.end()) {
        frames.push_back(bar);
        IconLift::refresh(bar);
    }
}

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
        dimension(xaml::FrameworkElement::MinWidthProperty(),xaml::FrameworkElement::MaxWidthProperty(),xaml::FrameworkElement::WidthProperty(),static_cast<double>(settings.button));break;
    case Kind::Background: {
        Windows::UI::Color color{static_cast<uint8_t>((settings.opacity*255+50)/100),
            static_cast<uint8_t>(settings.color>>16),static_cast<uint8_t>(settings.color>>8),static_cast<uint8_t>(settings.color)};
        if(settings.glass) {
            xaml::Media::LinearGradientBrush brush;
            brush.StartPoint({0,0});
            brush.EndPoint({0.85f,1});
            auto addStop=[&](double offset,Windows::UI::Color tint) {
                xaml::Media::GradientStop stop;
                stop.Offset(offset);stop.Color(tint);
                brush.GradientStops().Append(stop);
            };
            auto shine=[](uint8_t channel) {return static_cast<uint8_t>((channel*3+255)/4);};
            addStop(0,{color.A,shine(color.R),shine(color.G),shine(color.B)});
            auto middle=color;
            middle.A=static_cast<uint8_t>(color.A*3/4);
            addStop(0.38,middle);
            addStop(1,{static_cast<uint8_t>(color.A*2/3),static_cast<uint8_t>(settings.glassColor>>16),
                static_cast<uint8_t>(settings.glassColor>>8),static_cast<uint8_t>(settings.glassColor)});
            set(xaml::Shapes::Shape::FillProperty(),brush);
        } else set(xaml::Shapes::Shape::FillProperty(),xaml::Media::SolidColorBrush(color));
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
void StyledElement::updateIconLift(const Settings& settings) {
    if(kind!=Kind::Icon) return;
    if(!settings.iconEffect) {state->lift.reset();return;}
    auto icon=state->target.get();
    auto parent=icon;
    xaml::FrameworkElement button{nullptr};
    for(int depth=0;parent&&depth<24;depth++) {
        parent=media::VisualTreeHelper::GetParent(parent).try_as<xaml::FrameworkElement>();
        if(!parent) break;
        auto type=get_class_name(parent);
        auto parentKind=classify(std::wstring(type),std::wstring(parent.Name()),true);
        if(parentKind==Kind::Icon) break; // Avoid translating a nested image twice.
        if(parentKind==Kind::Button&&!button) button=parent;
        if(parentKind!=Kind::Frame||!button) continue;
        if(!state->lift||state->lift->button.get()!=button||state->lift->frame.get()!=parent) {
            state->lift.reset();
            auto lift=std::make_shared<IconLift>(icon,button,parent);
            lift->attach(lift);
            state->lift=std::move(lift);
        }
        state->lift->distance=settings.enabled?std::clamp((settings.height-settings.icon)/2.0-1.0,2.0,5.0):5.0;
        state->lift->effect=static_cast<IconEffect>(settings.iconEffect);
        return;
    }
    state->lift.reset();
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
    state->lift.reset();
    restore();
    auto& values=registry();auto found=values.find(identity);
    if(found!=values.end()&&found->second.lock()==state) values.erase(found);
}
}
