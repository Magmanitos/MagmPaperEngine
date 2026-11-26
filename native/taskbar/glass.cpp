#include "glass.h"
#include <d2d1effects.h>
#include <windows.graphics.effects.interop.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <array>
#include <cmath>

namespace magm {
using namespace winrt;
namespace effects=Windows::Graphics::Effects;
namespace interop=ABI::Windows::Graphics::Effects;
using Windows::Foundation::IInspectable;
using Windows::Foundation::IPropertyValue;
using Windows::Foundation::PropertyValue;
using Windows::UI::Color;
using xaml::Hosting::ElementCompositionPreview;

namespace {
// Composition's D2D effect metadata. These nodes only read the compositor's
// backdrop and run in the composition engine, including for animated wallpapers.
struct EffectNode final:implements<EffectNode,effects::IGraphicsEffect,effects::IGraphicsEffectSource,interop::IGraphicsEffectD2D1Interop> {
    GUID id;
    std::vector<IInspectable> values;
    effects::IGraphicsEffectSource source;
    hstring name;
    EffectNode(GUID type,std::vector<IInspectable> properties,effects::IGraphicsEffectSource const& input)
        :id(type),values(std::move(properties)),source(input) {}

    hstring Name() const {return name;}
    void Name(hstring const& value) {name=value;}
    HRESULT __stdcall GetEffectId(GUID* result) noexcept override {*result=id;return S_OK;}
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR,UINT*,interop::GRAPHICS_EFFECT_PROPERTY_MAPPING*) noexcept override {return E_INVALIDARG;}
    HRESULT __stdcall GetPropertyCount(UINT* result) noexcept override {*result=static_cast<UINT>(values.size());return S_OK;}
    HRESULT __stdcall GetSourceCount(UINT* result) noexcept override {*result=1;return S_OK;}
    HRESULT __stdcall GetProperty(UINT index,ABI::Windows::Foundation::IPropertyValue** result) noexcept override {
        *result=nullptr;
        if(index>=values.size()) return E_INVALIDARG;
        try {
            auto value=values[index].as<IPropertyValue>();
            *result=reinterpret_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(value));
            return S_OK;
        } catch(...) {return to_hresult();}
    }
    HRESULT __stdcall GetSource(UINT index,interop::IGraphicsEffectSource** result) noexcept override {
        *result=nullptr;
        if(index!=0) return E_INVALIDARG;
        auto value=source;
        *result=reinterpret_cast<interop::IGraphicsEffectSource*>(detach_abi(value));
        return S_OK;
    }
};

composition::CompositionEffectBrush opticalBrush(composition::Compositor const& compositor,
    composition::CompositionBackdropBrush const& source,bool lens) {
    effects::IGraphicsEffectSource input=composition::CompositionEffectSourceParameter(L"Desktop");
    if(lens) {
        // A small vertical magnification gives the material an optical offset
        // while keeping the sampling area around the actual taskbar strip.
        constexpr std::array<float,6> matrix{1,0,0,1.11f,0,-3};
        auto transform=make<EffectNode>(CLSID_D2D12DAffineTransform,
            std::vector<IInspectable>{PropertyValue::CreateUInt32(D2D1_2DAFFINETRANSFORM_INTERPOLATION_MODE_LINEAR),
                PropertyValue::CreateUInt32(D2D1_BORDER_MODE_HARD),PropertyValue::CreateSingleArray(matrix),
                PropertyValue::CreateSingle(0)},input);
        input=transform.as<effects::IGraphicsEffectSource>();
    }
    auto blur=make<EffectNode>(CLSID_D2D1GaussianBlur,
        std::vector<IInspectable>{PropertyValue::CreateSingle(8),PropertyValue::CreateUInt32(D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED),
            PropertyValue::CreateUInt32(D2D1_BORDER_MODE_HARD)},input);
    auto brush=compositor.CreateEffectFactory(blur).CreateBrush();
    brush.Comment(lens?L"MagmPaper.Glass.Lens":L"MagmPaper.Glass.Blur");
    brush.SetSourceParameter(L"Desktop",source);
    return brush;
}

Color tintFrom(UINT rgb,uint8_t alpha) {
    return {alpha,static_cast<uint8_t>(rgb>>16),static_cast<uint8_t>(rgb>>8),static_cast<uint8_t>(rgb)};
}
Color glint(UINT rgb,float white,uint8_t alpha) {
    auto makeChannel=[&](UINT shift){return static_cast<uint8_t>(std::lround(((rgb>>shift)&255)*(1-white)+255*white));};
    return {alpha,makeChannel(16),makeChannel(8),makeChannel(0)};
}
void setStops(composition::Compositor const& compositor,composition::CompositionLinearGradientBrush const& brush,
    std::initializer_list<std::pair<float,Color>> stops) {
    auto collection=brush.ColorStops();
    collection.Clear();
    for(auto const& [position,color]:stops) collection.Append(compositor.CreateColorGradientStop(position,color));
}
}

double glassRadius(double height,UINT radius) {return std::min(height/2,radius?static_cast<double>(radius):height*.43);}

GlassVisual::GlassVisual(xaml::FrameworkElement const& target):element(target) {
    compositor=ElementCompositionPreview::GetElementVisual(target).Compositor();
    root=compositor.CreateContainerVisual();
    outline=compositor.CreateRoundedRectangleGeometry();
    auto clip=compositor.CreateGeometricClip();clip.Geometry(outline);root.Clip(clip);

    backdrop=compositor.CreateSpriteVisual();
    backdrop.RelativeSizeAdjustment({1,1});backdrop.Opacity(.84f);root.Children().InsertAtTop(backdrop);
    auto addFilm=[&](float x1,float y1,float x2,float y2) {
        auto brush=compositor.CreateLinearGradientBrush();brush.StartPoint({x1,y1});brush.EndPoint({x2,y2});
        auto visual=compositor.CreateSpriteVisual();visual.RelativeSizeAdjustment({1,1});visual.Brush(brush);
        root.Children().InsertAtTop(visual);
        return brush;
    };
    tint=addFilm(0,.12f,1,.82f);
    sheen=addFilm(0,0,0,1);

    auto borders=compositor.CreateShapeVisual();borders.RelativeSizeAdjustment({1,1});root.Children().InsertAtTop(borders);
    auto addRim=[&](composition::CompositionRoundedRectangleGeometry& geometry,float width) {
        geometry=compositor.CreateRoundedRectangleGeometry();
        auto brush=compositor.CreateLinearGradientBrush();brush.StartPoint({0,.14f});brush.EndPoint({1,.86f});
        auto shape=compositor.CreateSpriteShape(geometry);shape.StrokeThickness(width);shape.StrokeBrush(brush);
        borders.Shapes().Append(shape);
        return brush;
    };
    rim=addRim(edge,1.35f);
    innerRim=addRim(innerEdge,.65f);
    previous=ElementCompositionPreview::GetElementChildVisual(target);
    ElementCompositionPreview::SetElementChildVisual(target,root);
}

std::shared_ptr<GlassVisual> GlassVisual::create(xaml::FrameworkElement const& target) {
    auto value=std::shared_ptr<GlassVisual>(new GlassVisual(target));
    std::weak_ptr<GlassVisual> weak=value;
    value->sizeChanged=target.SizeChanged([weak](auto const&,auto const&) {
        if(auto self=weak.lock()) try {self->arrange();} catch(...) {}
    });
    return value;
}

void GlassVisual::arrange() {
    auto target=element.get();if(!target) return;
    float width=static_cast<float>(target.ActualWidth()),height=static_cast<float>(target.ActualHeight());
    float rounding=static_cast<float>(glassRadius(height,radius==UINT_MAX?0:radius));
    root.Size({width,height});outline.Size({width,height});outline.CornerRadius({rounding,rounding});
    auto inset=[&](composition::CompositionRoundedRectangleGeometry const& geometry,float amount) {
        float corner=std::max(0.f,rounding-amount);
        geometry.Offset({amount,amount});geometry.Size({std::max(0.f,width-2*amount),std::max(0.f,height-2*amount)});
        geometry.CornerRadius({corner,corner});
    };
    inset(edge,.8f);inset(innerEdge,2.15f);
}

void GlassVisual::useBackdrop(bool host) {
    backdrop.Brush(nullptr);
    try {
#ifdef MAGM_HARNESS
        // The fixture draws its backdrop inside the same XAML island.
        auto source=compositor.CreateBackdropBrush();
#else
        if(!host) return;
        auto source=compositor.CreateHostBackdropBrush();
#endif
        try {backdrop.Brush(opticalBrush(compositor,source,true));}
        catch(...) {try {backdrop.Brush(opticalBrush(compositor,source,false));} catch(...) {backdrop.Brush(source);}}
    } catch(...) {}
}

void GlassVisual::apply(Settings const& settings,bool host) {
    if(firstAppearance||hostBackdrop!=host) {useBackdrop(host);hostBackdrop=host;firstAppearance=false;}
    if(opacity!=settings.opacity) {opacity=settings.opacity;root.Opacity(opacity/100.0f);}
    if(radius!=settings.radius) {radius=settings.radius;arrange();}
    if(color==settings.color&&endColor==settings.glassColor) return;
    color=settings.color;endColor=settings.glassColor;
    setStops(compositor,tint,{{0,glint(color,.18f,115)},{.23f,tintFrom(color,127)},
        {.52f,tintFrom(color,78)},{.81f,tintFrom(endColor,104)},{1,glint(endColor,.1f,113)}});
    setStops(compositor,sheen,{{0,glint(color,.82f,58)},{.11f,glint(color,.45f,22)},
        {.34f,glint(color,.45f,0)},{.70f,tintFrom(0x111020,0)},{.94f,tintFrom(0x111020,59)},{1,glint(endColor,.38f,23)}});
    setStops(compositor,rim,{{0,glint(color,.95f,223)},{.20f,glint(color,.76f,148)},
        {.49f,glint(color,.55f,70)},{.68f,glint(endColor,.56f,98)},{1,glint(endColor,.96f,208)}});
    setStops(compositor,innerRim,{{0,glint(color,.84f,83)},{.43f,glint(color,.8f,18)},
        {.73f,glint(endColor,.36f,32)},{1,glint(endColor,.89f,106)}});
}

GlassVisual::~GlassVisual() noexcept {
    if(auto target=element.get()) try {
        if(sizeChanged.value) target.SizeChanged(sizeChanged);
        if(ElementCompositionPreview::GetElementChildVisual(target)==root)
            ElementCompositionPreview::SetElementChildVisual(target,previous);
    } catch(...) {}
}
}
